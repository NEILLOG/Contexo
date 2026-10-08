using Contexo.Core.Abstractions;

namespace Contexo.Core.Parsing.Spreadsheet;

/// <summary>Stub. Implemented by T08.</summary>
internal sealed class SpreadsheetRegionReader : ISpreadsheetRegionReader
{
    public Task<SpreadsheetRegion> ReadAsync(string filePath, string sheet, string cellRange, int headerRowCount, CancellationToken cancellationToken) => throw new NotImplementedException("T08");
}
