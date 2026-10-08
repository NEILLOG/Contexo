using Contexo.Core.Abstractions;

namespace Contexo.Core.Parsing.Spreadsheet;

/// <summary>Stub. Implemented by T08.</summary>
internal sealed class SpreadsheetParser : IDocumentParser
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".xlsx", ".xlsm", ".csv"];

    public Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken) => throw new NotImplementedException("T08");
}
