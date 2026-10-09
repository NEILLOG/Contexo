using System.Reflection;
using Contexo.Core.Abstractions;
using Contexo.Core.Tables;
using Contexo.Core.Tests.Common;

namespace Contexo.Core.Tests.Tables;

/// <summary>Implements only GetExcelTableAsync of <see cref="IKnowledgeStore"/>; any other call fails the test.</summary>
public class FakeStoreProxy : DispatchProxy
{
    public Dictionary<string, ExcelTableRecord> Tables { get; } = [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IKnowledgeStore.GetExcelTableAsync))
        {
            Tables.TryGetValue((string)args![0]!, out var record);
            return Task.FromResult(record);
        }

        throw new NotSupportedException(targetMethod?.Name);
    }
}

/// <summary>Returns a fixed region and counts how often the file was read.</summary>
internal sealed class FakeRegionReader : ISpreadsheetRegionReader
{
    public Dictionary<string, SpreadsheetRegion> Regions { get; } = [];

    public int ReadCount { get; private set; }

    public Task<SpreadsheetRegion> ReadAsync(string filePath, string sheet, string cellRange, int headerRowCount, CancellationToken cancellationToken)
    {
        ReadCount++;
        return Task.FromResult(Regions[filePath]);
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan span) => _now += span;
}

/// <summary>A service wired to a fake store and reader, with real files in a temp folder so the modification time can be checked.</summary>
internal sealed class TableHarness : IDisposable
{
    private readonly FakeStoreProxy _store;
    private readonly TempDirectory _files = new();
    private int _nextId = 1;

    public TableHarness()
    {
        _store = DispatchProxy.Create<IKnowledgeStore, FakeStoreProxy>() as FakeStoreProxy
            ?? throw new InvalidOperationException();
        Store = (IKnowledgeStore)_store;
        Service = new TableQueryService(Store, Reader, Logger, Time);
    }

    public IKnowledgeStore Store { get; }

    public FakeRegionReader Reader { get; } = new();

    public ManualTimeProvider Time { get; } = new();

    public TestLogger<TableQueryService> Logger { get; } = new();

    public TableQueryService Service { get; }

    public string Directory => _files.Path;

    /// <summary>Registers a table backed by an empty file on disk and returns its id.</summary>
    public string AddTable(string[] columns, IEnumerable<string?[]> rows, string fileName = "報表.xlsx")
    {
        var id = "t" + _nextId++;
        var path = _files.Combine(id + "-" + fileName);
        File.WriteAllText(path, "x");
        var data = rows.Select(r => (IReadOnlyList<string?>)r).ToList();
        Reader.Regions[path] = new SpreadsheetRegion(columns, data);
        _store.Tables[id] = new ExcelTableRecord(id, 1, path, new SpreadsheetTable(
            "Sheet1!A1:Z999", "Sheet1", "A1:Z999", 1, columns, data.Count, [], "desc"));
        return id;
    }

    public string PathOf(string tableId) => _store.Tables[tableId].FilePath;

    public void Remove(string tableId) => _store.Tables.Remove(tableId);

    public void Dispose()
    {
        Service.Dispose();
        _files.Dispose();
    }
}
