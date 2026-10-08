using System.Text;
using Contexo.Core.Abstractions;

namespace Contexo.Core.Tests.Parsing.TextParsers;

internal static class TextParserTestHelper
{
    static TextParserTestHelper() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Task<ParsedDocument> ParseAsync(
        IDocumentParser parser,
        string fileName,
        byte[] bytes,
        ParserOptions? options = null,
        IReadOnlyList<string>? embeddedPath = null)
    {
        var stream = new MemoryStream(bytes);
        return parser.ParseAsync(new ParseContext(stream, fileName, options ?? new ParserOptions(), embeddedPath), CancellationToken.None);
    }

    public static Task<ParsedDocument> ParseAsync(
        IDocumentParser parser,
        string fileName,
        string text,
        ParserOptions? options = null,
        IReadOnlyList<string>? embeddedPath = null) =>
        ParseAsync(parser, fileName, new UTF8Encoding(false).GetBytes(text), options, embeddedPath);

    public static string AllText(ParsedDocument document) => string.Join("\n", document.Sections.Select(s => s.Text));
}
