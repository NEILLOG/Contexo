using Contexo.Core.Abstractions;

namespace Contexo.Core.Parsing.Text;

/// <summary>Stub. Implemented by T04.</summary>
internal sealed class PlainTextParser : IDocumentParser
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".txt", ".md", ".markdown", ".json", ".xml", ".log"];

    public Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken) => throw new NotImplementedException("T04");
}
