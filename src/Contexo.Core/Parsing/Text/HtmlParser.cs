using Contexo.Core.Abstractions;

namespace Contexo.Core.Parsing.Text;

/// <summary>Stub. Implemented by T04.</summary>
internal sealed class HtmlParser : IDocumentParser
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".html", ".htm"];

    public Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken) => throw new NotImplementedException("T04");
}
