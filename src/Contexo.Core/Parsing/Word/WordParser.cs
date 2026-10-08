using Contexo.Core.Abstractions;

namespace Contexo.Core.Parsing.Word;

/// <summary>Stub. Implemented by T05.</summary>
internal sealed class WordParser : IDocumentParser
{
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".docx"];

    public Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken) => throw new NotImplementedException("T05");
}
