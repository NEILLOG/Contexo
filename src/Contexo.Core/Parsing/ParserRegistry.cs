using Contexo.Core.Abstractions;

namespace Contexo.Core.Parsing;

/// <summary>Maps file extensions to parsers. Built from every registered <see cref="IDocumentParser"/>; two parsers claiming one extension is a startup error.</summary>
internal sealed class ParserRegistry : IParserRegistry
{
    private readonly Dictionary<string, IDocumentParser> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    public ParserRegistry(IEnumerable<IDocumentParser> parsers)
    {
        foreach (var parser in parsers)
        {
            foreach (var extension in parser.SupportedExtensions)
            {
                if (!_byExtension.TryAdd(extension, parser))
                {
                    throw new InvalidOperationException(
                        $"Extension '{extension}' is claimed by both {_byExtension[extension].GetType().Name} and {parser.GetType().Name}.");
                }
            }
        }

        SupportedExtensions = _byExtension.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyCollection<string> SupportedExtensions { get; }

    public IDocumentParser? Resolve(string extension) =>
        !string.IsNullOrEmpty(extension) && _byExtension.TryGetValue(extension, out var parser) ? parser : null;
}
