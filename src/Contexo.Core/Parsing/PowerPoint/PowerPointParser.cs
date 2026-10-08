using Contexo.Core.Abstractions;

namespace Contexo.Core.Parsing.PowerPoint;

/// <summary>Stub. Implemented by T06.</summary>
internal sealed class PowerPointParser : IDocumentParser
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".pptx"];

    public Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken) => throw new NotImplementedException("T06");
}
