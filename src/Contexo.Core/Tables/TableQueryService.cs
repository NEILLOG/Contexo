using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Contexo.Core.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Contexo.Core.Tables;

/// <summary>
/// describe_table / query_table for large Excel tables. The data is read from the original file when first asked for and copied into an in-memory SQLite database.
/// Up to 4 copies are kept for 60 seconds (least recently used goes first); a copy is dropped early when the file changes.
/// </summary>
internal sealed class TableQueryService : ITableQueryService, IDisposable
{
    internal const int MaxCachedTables = 4;
    internal const int MaxSampleRows = 20;
    internal const int MaxRowsLimit = 500;
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    private readonly IKnowledgeStore _store;
    private readonly ILogger<TableQueryService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TableLoader _loader;

    // _sync guards _cache and LoadedTable.LastUsedAt. Each table id has one gate that serialises loading and using its connection.
    private readonly object _sync = new();
    private readonly Dictionary<string, LoadedTable> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private bool _disposed;

    public TableQueryService(IKnowledgeStore store, ISpreadsheetRegionReader reader, ILogger<TableQueryService> logger, TimeProvider? timeProvider = null)
    {
        _store = store;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _loader = new TableLoader(reader, _timeProvider, logger);
    }

    public Task<TableDescription> DescribeAsync(string tableId, int sampleRows, CancellationToken cancellationToken)
    {
        var count = Math.Clamp(sampleRows, 0, MaxSampleRows);
        return WithTableAsync(tableId, table =>
        {
            var rows = new List<IReadOnlyList<string?>>(count);
            if (count > 0)
            {
                using var command = table.Connection.CreateCommand();
                command.CommandText = $"SELECT * FROM {TableLoader.SqlTableName} LIMIT {count.ToString(CultureInfo.InvariantCulture)}";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(ReadRow(reader));
                }
            }

            var record = table.Record;
            return new TableDescription(
                table.TableId, record.FilePath, record.Table.Sheet, record.Table.CellRange,
                TableLoader.SqlTableName, table.Columns, table.RowCount, rows);
        }, cancellationToken);
    }

    public Task<TableQueryResult> QueryAsync(string tableId, string sql, int maxRows, CancellationToken cancellationToken)
    {
        var statement = SqlGuard.Validate(sql);
        var limit = Math.Clamp(maxRows, 1, MaxRowsLimit);
        return WithTableAsync(tableId, table => Execute(table, statement, limit, cancellationToken), cancellationToken);
    }

    public void Dispose()
    {
        List<LoadedTable> toClose;
        lock (_sync)
        {
            _disposed = true;
            toClose = [.. _cache.Values];
            _cache.Clear();
        }

        foreach (var table in toClose)
        {
            table.Dispose();
        }
    }

    private TableQueryResult Execute(LoadedTable table, string statement, int limit, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(QueryTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var command = table.Connection.CreateCommand();
        command.CommandText = statement;
        command.CommandTimeout = (int)QueryTimeout.TotalSeconds;

        // CommandTimeout only limits waiting for locks, so a runaway query is stopped by interrupting the connection.
        using var interrupt = linked.Token.Register(static state => SQLitePCL.raw.sqlite3_interrupt(((SqliteConnection)state!).Handle), table.Connection);

        var columns = new List<string>();
        var rows = new List<IReadOnlyList<string?>>();
        var truncated = false;
        try
        {
            using var reader = command.ExecuteReader();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }

            while (reader.Read())
            {
                if (rows.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                linked.Token.ThrowIfCancellationRequested();
                rows.Add(ReadRow(reader));
            }
        }
        catch (SqliteException ex) when (linked.IsCancellationRequested)
        {
            throw Cancelled(cancellationToken, timeout, ex);
        }
        catch (OperationCanceledException ex) when (linked.IsCancellationRequested)
        {
            throw Cancelled(cancellationToken, timeout, ex);
        }
        catch (SqliteException ex)
        {
            throw new TableQueryException(
                $"SQL 執行失敗：{ex.Message} 資料表名稱固定為 t，可用欄位：{ColumnList(table)}", ex);
        }

        _logger.LogInformation("Queried table {TableId}: {Rows} rows, truncated {Truncated}, {ElapsedMs} ms", table.TableId, rows.Count, truncated, clock.ElapsedMilliseconds);
        return new TableQueryResult(columns, rows, truncated);
    }

    private static Exception Cancelled(CancellationToken callerToken, CancellationTokenSource timeout, Exception inner) =>
        callerToken.IsCancellationRequested && !timeout.IsCancellationRequested
            ? new OperationCanceledException(callerToken)
            : new TableQueryException($"查詢超過 {QueryTimeout.TotalSeconds:0} 秒，已經停止。請縮小範圍或簡化查詢（例如加上 WHERE 或 LIMIT）。", inner);

    private static string ColumnList(LoadedTable table) =>
        string.Join("、", table.Columns.Select(c => $"\"{c.SqlName}\"（{c.InferredType}）"));

    private static List<string?> ReadRow(SqliteDataReader reader)
    {
        var row = new List<string?>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row.Add(FormatValue(reader, i));
        }

        return row;
    }

    private static string? FormatValue(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        switch (reader.GetFieldType(ordinal).Name)
        {
            case nameof(Int64):
                return reader.GetInt64(ordinal).ToString(CultureInfo.InvariantCulture);
            case nameof(Double):
                return FormatDouble(reader.GetDouble(ordinal));
            case nameof(Byte) + "[]":
                return "(二進位資料)";
            default:
                return reader.GetString(ordinal);
        }
    }

    /// <summary>1280000.0 becomes "1280000"; 0.1 + 0.2 becomes "0.3" (15 significant digits).</summary>
    internal static string FormatDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        if (Math.Abs(value) < 1e15 && value == Math.Floor(value))
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }

        return value.ToString("G15", CultureInfo.InvariantCulture);
    }

    private async Task<T> WithTableAsync<T>(string tableId, Func<LoadedTable, T> work, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(tableId))
        {
            throw new TableQueryException(NotFoundMessage);
        }

        var record = await _store.GetExcelTableAsync(tableId, cancellationToken).ConfigureAwait(false)
            ?? throw new TableQueryException(NotFoundMessage);
        var fileName = Path.GetFileName(record.FilePath);
        var stamp = FileStamp.TryRead(record.FilePath);
        if (stamp is null)
        {
            Evict(tableId);
            throw new TableQueryException($"原始檔案已移動或刪除：{fileName}");
        }

        PurgeExpired();

        var gate = _gates.GetOrAdd(tableId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var table = TryGetCached(tableId, record, stamp.Value);
            if (table is null)
            {
                table = await _loader.LoadAsync(record, stamp.Value, cancellationToken).ConfigureAwait(false);
                Add(table);
            }

            return await Task.Run(() => work(table), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private const string NotFoundMessage = "找不到這個表格，可能已被移除。請重新搜尋。";

    /// <summary>Returns the cached copy when it is still fresh. A stale copy is closed here (the caller holds the table's gate, so nobody is using it).</summary>
    private LoadedTable? TryGetCached(string tableId, ExcelTableRecord record, FileStamp stamp)
    {
        LoadedTable? stale = null;
        LoadedTable? fresh = null;
        lock (_sync)
        {
            if (_cache.TryGetValue(tableId, out var existing))
            {
                var now = _timeProvider.GetUtcNow();
                if (existing.Stamp == stamp && existing.IsSameSource(record) && now - existing.LoadedAt < CacheLifetime)
                {
                    existing.LastUsedAt = now;
                    fresh = existing;
                }
                else
                {
                    _cache.Remove(tableId);
                    stale = existing;
                }
            }
        }

        stale?.Dispose();
        return fresh;
    }

    private void Add(LoadedTable table)
    {
        List<LoadedTable> evicted = [];
        lock (_sync)
        {
            _cache[table.TableId] = table;
            while (_cache.Count > MaxCachedTables)
            {
                var oldest = _cache.Values.Where(t => !ReferenceEquals(t, table)).MinBy(t => t.LastUsedAt)!;
                _cache.Remove(oldest.TableId);
                evicted.Add(oldest);
            }
        }

        foreach (var item in evicted)
        {
            CloseWhenIdle(item);
        }
    }

    private void Evict(string tableId)
    {
        LoadedTable? removed;
        lock (_sync)
        {
            _cache.Remove(tableId, out removed);
        }

        if (removed is not null)
        {
            CloseWhenIdle(removed);
        }
    }

    private void PurgeExpired()
    {
        List<LoadedTable> expired;
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            expired = [.. _cache.Values.Where(t => now - t.LoadedAt >= CacheLifetime)];
            foreach (var table in expired)
            {
                _cache.Remove(table.TableId);
            }
        }

        foreach (var table in expired)
        {
            CloseWhenIdle(table);
        }
    }

    /// <summary>Another query may still be running on this copy; closes it as soon as that table's gate is free.</summary>
    private void CloseWhenIdle(LoadedTable table)
    {
        var gate = _gates.GetOrAdd(table.TableId, static _ => new SemaphoreSlim(1, 1));
        _ = Task.Run(async () =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                table.Dispose();
            }
            finally
            {
                gate.Release();
            }
        });
    }
}
