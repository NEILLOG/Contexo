using Contexo.Core.Abstractions;
using Contexo.Core.Parsing.Pdf;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;
using static Contexo.Core.Tests.Parsing.Pdf.PdfTestFiles;

namespace Contexo.Core.Tests.Parsing.Pdf;

public sealed class PdfParserTests
{
    private readonly PdfParser _parser = new();

    private Task<ParsedDocument> ParseAsync(byte[] pdf, ParserOptions? options = null, IReadOnlyList<string>? embeddedPath = null, CancellationToken ct = default)
    {
        var stream = new MemoryStream(pdf);
        return _parser.ParseAsync(new ParseContext(stream, "test.pdf", options ?? new ParserOptions(), embeddedPath), ct);
    }

    [Fact]
    public void SupportedExtensions_IsPdf() => Assert.Equal([".pdf"], _parser.SupportedExtensions);

    [Fact]
    public async Task ThreePagePdf_ProducesOneProseSectionPerPageWithPageNumbers()
    {
        var pdf = TextPdf(
            [new TextLine("Quarterly report first page", 700), new TextLine("Revenue grew by ten percent", 685), new TextLine("Second paragraph here", 600)],
            [new TextLine("Second page heading", 700), new TextLine("Costs were stable", 685)],
            [new TextLine("Third page heading", 700), new TextLine("Outlook is positive", 685)]);

        var result = await ParseAsync(pdf);

        Assert.Equal(3, result.Sections.Count);
        Assert.Equal([1, 2, 3], result.Sections.Select(s => s.Location.Page));
        Assert.All(result.Sections, s => Assert.Equal(SectionKind.Prose, s.Kind));
        Assert.DoesNotContain("scanned-pdf", result.Warnings);

        var first = result.Sections[0].Text;
        Assert.Contains("Quarterly report first page", first);
        Assert.Contains("Revenue grew by ten percent", first);
        Assert.Contains("Second paragraph here", first);
        Assert.Contains(' ', first);
        Assert.Contains('\n', first);
        Assert.Contains("Outlook is positive", result.Sections[2].Text);
    }

    [Fact]
    public async Task ParagraphsFarApart_AreSeparatedByBlankLine()
    {
        var pdf = TextPdf(
            [new TextLine("Alpha beta gamma delta", 700), new TextLine("Epsilon zeta eta theta", 685), new TextLine("Completely separate paragraph", 450)]);

        var result = await ParseAsync(pdf);

        var text = Assert.Single(result.Sections).Text;
        var paragraphs = text.Split(PdfTextLayout.ParagraphSeparator);
        Assert.Equal(2, paragraphs.Length);
        Assert.Contains("Alpha beta gamma delta", paragraphs[0]);
        Assert.Contains("Epsilon zeta eta theta", paragraphs[0]);
        Assert.Equal("Completely separate paragraph", paragraphs[1]);
    }

    [Fact]
    public async Task RepeatedHeaderAndPageNumberFooter_AreRemoved_RarePageTextIsKept()
    {
        var pages = new List<IReadOnlyList<TextLine>>();
        for (var i = 1; i <= 5; i++)
        {
            var lines = new List<TextLine>
            {
                new("ACME Confidential Report", 800),
                new($"Body text of page number {i} with unique words", 500),
            };

            if (i == 3)
            {
                lines.Add(new TextLine("Rare footnote only here", 30));
            }
            else
            {
                lines.Add(new TextLine($"Page {i} of 5", 30));
            }

            pages.Add(lines);
        }

        // Page 2 additionally carries a one-off heading line above the shared header.
        pages[1] = [.. pages[1], new TextLine("One-off banner", 830)];

        var result = await ParseAsync(TextPdf([.. pages]));

        Assert.Equal(5, result.Sections.Count);
        Assert.All(result.Sections, s => Assert.DoesNotContain("Page ", s.Text.Replace("Body text of page", "")));
        Assert.Contains("Body text of page number 1", result.Sections[0].Text);
        // Page 2: its top row is the one-off banner, so the shared header (no longer the top row) survives, banner is kept.
        Assert.Contains("One-off banner", result.Sections[1].Text);
        Assert.Contains("Rare footnote only here", result.Sections[2].Text);
        foreach (var index in new[] { 0, 2, 3, 4 })
        {
            Assert.DoesNotContain("ACME", result.Sections[index].Text);
        }
    }

    [Fact]
    public async Task ImageOnlyPdf_IsScanned_NoSections_WarningAndImages()
    {
        var result = await ParseAsync(ImageOnlyPdf(2));

        Assert.Empty(result.Sections);
        Assert.Contains("scanned-pdf", result.Warnings);
        Assert.Equal(2, result.Images.Count);
        Assert.Equal([1, 2], result.Images.Select(i => i.Location.Page));
        Assert.All(result.Images, i =>
        {
            Assert.Equal("image/png", i.ContentType);
            Assert.NotEmpty(i.Content);
        });
    }

    [Fact]
    public async Task ScannedPdf_CollectsImagesOfFirst50PagesOnly()
    {
        var result = await ParseAsync(ImageOnlyPdf(52));

        Assert.Empty(result.Sections);
        Assert.Contains("scanned-pdf", result.Warnings);
        Assert.Contains(PdfParser.ImagesLimitedWarning, result.Warnings);
        Assert.Equal(50, result.Images.Count);
        Assert.Equal(50, result.Images.Max(i => i.Location.Page));
    }

    [Fact]
    public async Task VeryLittleText_IsTreatedAsScanned()
    {
        // 2 pages with 5 letters each: 5 characters per page on average is below the threshold of 20.
        var result = await ParseAsync(TextPdf([new TextLine("Stamp", 700)], [new TextLine("Stamp", 700)]));

        Assert.Empty(result.Sections);
        Assert.Contains("scanned-pdf", result.Warnings);
    }

    [Fact]
    public async Task EncryptedPdf_ThrowsPasswordProtected()
    {
        var ex = await Assert.ThrowsAsync<DocumentParseException>(() => ParseAsync(EncryptedPdf()));

        Assert.Equal(DocumentErrorCode.PasswordProtected, ex.Code);
    }

    [Fact]
    public void MapException_EncryptedException_IsPasswordProtected()
    {
        var mapped = PdfParser.MapException(new PdfDocumentEncryptedException("encrypted"));

        Assert.Equal(DocumentErrorCode.PasswordProtected, mapped.Code);
    }

    [Fact]
    public void MapException_OtherException_IsCorrupted()
    {
        Assert.Equal(DocumentErrorCode.Corrupted, PdfParser.MapException(new InvalidOperationException("boom")).Code);
        Assert.Equal(DocumentErrorCode.Corrupted, PdfParser.MapException(new PdfDocumentFormatException("bad")).Code);
    }

    [Fact]
    public async Task RandomBytes_ThrowsCorrupted()
    {
        var bytes = new byte[4096];
        new Random(42).NextBytes(bytes);

        var ex = await Assert.ThrowsAsync<DocumentParseException>(() => ParseAsync(bytes));

        Assert.Equal(DocumentErrorCode.Corrupted, ex.Code);
    }

    [Fact]
    public async Task EmptyAndTruncatedFiles_ThrowCorrupted()
    {
        var empty = await Assert.ThrowsAsync<DocumentParseException>(() => ParseAsync([]));
        Assert.Equal(DocumentErrorCode.Corrupted, empty.Code);

        var pdf = TextPdf([new TextLine("Some text that is long enough to count", 700)]);
        var cut = await Assert.ThrowsAsync<DocumentParseException>(() => ParseAsync(pdf.AsSpan(0, pdf.Length / 3).ToArray()));
        Assert.Equal(DocumentErrorCode.Corrupted, cut.Code);
    }

    [Fact]
    public async Task EmbeddedPath_IsCopiedIntoSectionLocations()
    {
        var pdf = TextPdf([new TextLine("Attachment content that is long enough", 700)]);

        var result = await ParseAsync(pdf, embeddedPath: ["outer.docx"]);

        var section = Assert.Single(result.Sections);
        Assert.Equal(["outer.docx"], section.Location.EmbeddedPath);
        Assert.Equal(1, section.Location.Page);
    }

    [Fact]
    public async Task MaxExtractedChars_TruncatesWithWarning()
    {
        var pdf = TextPdf(
            [new TextLine("First page has plenty of readable text", 700)],
            [new TextLine("Second page has plenty of readable text", 700)]);

        var result = await ParseAsync(pdf, new ParserOptions { MaxExtractedChars = 50 });

        Assert.Contains(PdfParser.TruncatedWarning, result.Warnings);
        Assert.True(result.Sections.Sum(s => s.Text.Length) <= 50);
        Assert.NotEmpty(result.Sections);
    }

    [Fact]
    public async Task Parser_DoesNotDisposeTheInputStream()
    {
        var stream = new MemoryStream(TextPdf([new TextLine("Stream stays open after parsing", 700)]));

        await _parser.ParseAsync(new ParseContext(stream, "a.pdf", new ParserOptions()), CancellationToken.None);

        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public async Task Cancelled_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ParseAsync(TextPdf([new TextLine("Hello there", 700)]), ct: cts.Token));
    }

    [Fact]
    public void ParserRegistry_ResolvesPdfParser()
    {
        var registry = new Contexo.Core.Parsing.ParserRegistry([_parser]);

        Assert.Same(_parser, registry.Resolve(".PDF"));
    }
}
