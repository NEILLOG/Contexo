using Contexo.Core.Abstractions;
using Contexo.Core.Parsing;
using Contexo.Core.Parsing.Pdf;
using Contexo.Core.Parsing.PowerPoint;
using Contexo.Core.Parsing.Spreadsheet;
using Contexo.Core.Parsing.Text;
using Contexo.Core.Parsing.Word;

namespace Contexo.Core.Tests.Parsing;

public sealed class ParserRegistryTests
{
    private static ParserRegistry CreateWithStubs() => new(
    [
        new PlainTextParser(), new HtmlParser(), new RtfParser(), new WordParser(),
        new PowerPointParser(), new PdfParser(), new SpreadsheetParser(),
    ]);

    [Theory]
    [InlineData(".txt", typeof(PlainTextParser))]
    [InlineData(".md", typeof(PlainTextParser))]
    [InlineData(".markdown", typeof(PlainTextParser))]
    [InlineData(".json", typeof(PlainTextParser))]
    [InlineData(".xml", typeof(PlainTextParser))]
    [InlineData(".log", typeof(PlainTextParser))]
    [InlineData(".html", typeof(HtmlParser))]
    [InlineData(".htm", typeof(HtmlParser))]
    [InlineData(".rtf", typeof(RtfParser))]
    [InlineData(".docx", typeof(WordParser))]
    [InlineData(".pptx", typeof(PowerPointParser))]
    [InlineData(".pdf", typeof(PdfParser))]
    [InlineData(".xlsx", typeof(SpreadsheetParser))]
    [InlineData(".xlsm", typeof(SpreadsheetParser))]
    [InlineData(".csv", typeof(SpreadsheetParser))]
    public void Every_stub_extension_resolves_to_its_parser_in_any_case(string extension, Type expected)
    {
        var registry = CreateWithStubs();

        Assert.IsType(expected, registry.Resolve(extension));
        Assert.IsType(expected, registry.Resolve(extension.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(".doc")]
    [InlineData(".png")]
    [InlineData("docx")]
    [InlineData("")]
    public void Unsupported_extensions_resolve_to_null(string extension) =>
        Assert.Null(CreateWithStubs().Resolve(extension));

    [Fact]
    public void Supported_extensions_list_every_registered_extension()
    {
        var registry = CreateWithStubs();

        Assert.Equal(15, registry.SupportedExtensions.Count);
        Assert.Contains(".docx", registry.SupportedExtensions);
        Assert.Contains(".csv", registry.SupportedExtensions);
    }

    [Fact]
    public void Two_parsers_claiming_the_same_extension_fail_at_construction()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ParserRegistry([new WordParser(), new DuplicateDocxParser()]));

        Assert.Contains(".docx", ex.Message);
    }

    [Fact]
    public void Duplicate_detection_ignores_case()
    {
        Assert.Throws<InvalidOperationException>(() => new ParserRegistry([new WordParser(), new DuplicateDocxParser(".DOCX")]));
    }

    private sealed class DuplicateDocxParser(string extension = ".docx") : IDocumentParser
    {
        public IReadOnlyCollection<string> SupportedExtensions { get; } = [extension];

        public Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ParsedDocument.Empty);
    }
}
