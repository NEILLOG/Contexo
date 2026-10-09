using System.Diagnostics;
using Contexo.Core.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Contexo.Core.Tables;

/// <summary>Modification time and size of the source file when a table was loaded; a change invalidates the cached copy.</summary>
internal readonly record struct FileStamp(DateTime LastWriteUtc, long Length)
{
    public static FileStamp? TryRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>An in-memory SQLite copy of one spreadsheet region. Not thread safe: the caller must hold the table's lock.</summary>
internal sealed class LoadedTable(
    string tableId,
    ExcelTableRecord record,
    FileStamp stamp,
    SqliteConnection connection,
    IReadOnlyList<TableColumn> columns,
    int rowCount,
    DateTimeOffset loadedAt) : IDisposable
{
    public string TableId { get; } = tableId;

    public ExcelTableRecord Record { get; } = record;

    public FileStamp Stamp { get; } = stamp;

    public SqliteConnection Connection { get; } = connection;

    public IReadOnlyList<TableColumn> Columns { get; } = columns;

    public int RowCount { get; } = rowCount;

    public DateTimeOffset LoadedAt { get; } = loadedAt;

    /// <summary>Last time a query used this copy; drives least-recently-used eviction. Guarded by the service's cache lock.</summary>
    public DateTimeOffset LastUsedAt { get; set; } = loadedAt;

    public bool IsSameSource(ExcelTableRecord other) =>
        string.Equals(Record.FilePath, other.FilePath, StringComparison.Ordinal)
        && Record.Table.Sheet == other.Table.Sheet
        && Record.Table.CellRange == other.Table.CellRange
        && Record.Table.HeaderRowCount == other.Table.HeaderRowCount;

    public void Dispose() => Connection.Dispose();
}

/// <summary>Reads a registered region from the original file and copies it into an in-memory SQLite table named "t".</summary>
internal sealed class TableLoader(ISpreadsheetRegionReader reader, TimeProvider timeProvider, ILogger logger)
{
    public const string SqlTableName = "t";

    public async Task<LoadedTable> LoadAsync(ExcelTableRecord record, FileStamp stamp, CancellationToken cancellationToken)
    {
        var table = record.Table;
        var fileName = Path.GetFileName(record.FilePath);
        var clock = Stopwatch.StartNew();

        SpreadsheetRegion region;
        try
        {
            region = await reader.ReadAsync(record.FilePath, table.Sheet, table.CellRange, table.HeaderRowCount, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            throw new TableQueryException($"原始檔案已移動或刪除：{fileName}", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new TableQueryException($"原始檔案已移動或刪除：{fileName}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new TableQueryException($"檔案內容已經改變，找不到原本的表格範圍：{fileName}。請重新搜尋。", ex);
        }
        catch (DocumentParseException ex)
        {
            throw new TableQueryException($"無法讀取原始檔案：{fileName}（{ex.Message}）", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TableQueryException($"無法讀取原始檔案，可能正被其他程式使用：{fileName}", ex);
        }

        if (region.Columns.Count == 0)
        {
            throw new TableQueryException($"這個表格沒有任何欄位：{fileName}");
        }

        var readMs = clock.ElapsedMilliseconds;
        var loaded = await Task.Run(() => Build(record, stamp, region, cancellationToken), cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Loaded table {TableId} ({FileName}): {Rows} rows x {Columns} columns, read {ReadMs} ms, total {TotalMs} ms",
            record.TableId, fileName, loaded.RowCount, loaded.Columns.Count, readMs, clock.ElapsedMilliseconds);
        return loaded;
    }

    private LoadedTable Build(ExcelTableRecord record, FileStamp stamp, SpreadsheetRegion region, CancellationToken cancellationToken)
    {
        var columnCount = region.Columns.Count;
        var sqlNames = TableValueParser.BuildSqlNames(region.Columns);
        var kinds = InferKinds(region, columnCount, cancellationToken);

        var connection = new SqliteConnection("Data Source=:memory:");
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode = OFF");
            Execute(connection, "PRAGMA synchronous = OFF");

            var definitions = string.Join(", ", sqlNames.Select((name, i) => $"\"{name}\" {(kinds[i] == ColumnKind.Number ? "REAL" : "TEXT")}"));
            try
            {
                Execute(connection, $"CREATE TABLE {SqlTableName} ({definitions})");
            }
            catch (SqliteException ex)
            {
                throw new TableQueryException("這個表格的欄位太多，無法載入查詢。", ex);
            }

            Insert(connection, region, kinds, cancellationToken);

            // From here on the copy can only be read, even if the SQL check ever has a gap.
            Execute(connection, "PRAGMA query_only = ON");
            LimitValueLength(connection);

            var columns = new List<TableColumn>(columnCount);
            for (var i = 0; i < columnCount; i++)
            {
                columns.Add(new TableColumn(sqlNames[i], region.Columns[i], kinds[i] switch
                {
                    ColumnKind.Number => "number",
                    ColumnKind.Date => "date",
                    _ => "text",
                }));
            }

            return new LoadedTable(record.TableId, record, stamp, connection, columns, region.Rows.Count, timeProvider.GetUtcNow());
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static ColumnKind[] InferKinds(SpreadsheetRegion region, int columnCount, CancellationToken cancellationToken)
    {
        var kinds = new ColumnKind[columnCount];
        for (var c = 0; c < columnCount; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var couldBeNumber = true;
            var couldBeDate = true;
            var anyValue = false;
            foreach (var row in region.Rows)
            {
                var text = row[c];
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                anyValue = true;
                if (couldBeNumber && !TableValueParser.TryParseNumber(text, out _))
                {
                    couldBeNumber = false;
                }

                if (couldBeDate && !TableValueParser.TryParseDate(text, out _))
                {
                    couldBeDate = false;
                }

                if (!couldBeNumber && !couldBeDate)
                {
                    break;
                }
            }

            kinds[c] = !anyValue ? ColumnKind.Text : couldBeNumber ? ColumnKind.Number : couldBeDate ? ColumnKind.Date : ColumnKind.Text;
        }

        return kinds;
    }

    private static void Insert(SqliteConnection connection, SpreadsheetRegion region, ColumnKind[] kinds, CancellationToken cancellationToken)
    {
        var columnCount = kinds.Length;
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {SqlTableName} VALUES ({string.Join(", ", Enumerable.Range(0, columnCount).Select(i => "$p" + i))})";
        var parameters = new SqliteParameter[columnCount];
        for (var i = 0; i < columnCount; i++)
        {
            parameters[i] = command.Parameters.Add("$p" + i, kinds[i] == ColumnKind.Number ? SqliteType.Real : SqliteType.Text);
        }

        command.Prepare();

        var rowIndex = 0;
        foreach (var row in region.Rows)
        {
            if ((++rowIndex & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var c = 0; c < columnCount; c++)
            {
                var text = c < row.Count ? row[c] : null;
                parameters[c].Value = ToDbValue(text, kinds[c]);
            }

            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static object ToDbValue(string? text, ColumnKind kind)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DBNull.Value;
        }

        switch (kind)
        {
            case ColumnKind.Number when TableValueParser.TryParseNumber(text, out var number):
                return number;
            case ColumnKind.Date when TableValueParser.TryParseDate(text, out var date):
                return TableValueParser.FormatDate(date);
            default:
                return text;
        }
    }

    /// <summary>Caps the size of one string / blob so something like zeroblob(2000000000) cannot exhaust memory.</summary>
    private static void LimitValueLength(SqliteConnection connection) =>
        SQLitePCL.raw.sqlite3_limit(connection.Handle, SQLitePCL.raw.SQLITE_LIMIT_LENGTH, 16 * 1024 * 1024);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private enum ColumnKind
    {
        Text,
        Number,
        Date,
    }
}
