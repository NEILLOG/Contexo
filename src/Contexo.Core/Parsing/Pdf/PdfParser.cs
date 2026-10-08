using Contexo.Core.Abstractions;

namespace Contexo.Core.Parsing.Pdf;

/// <summary>Stub. Implemented by T07.</summary>
internal sealed class PdfParser : IDocumentParser
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".pdf"];

    public Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken) => throw new NotImplementedException("T07");
}
