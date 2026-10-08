using Contexo.Core.Abstractions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;
using EmbeddedFile = Contexo.Core.Abstractions.EmbeddedFile;
using PigEmbeddedFile = UglyToad.PdfPig.Content.EmbeddedFile;
using PigWord = UglyToad.PdfPig.Content.Word;

namespace Contexo.Core.Parsing.Pdf;

/// <summary>
/// Text-layer PDF parser built on PdfPig. One <see cref="SectionKind.Prose"/> section per page.
/// Scanned PDFs (almost no text) yield no sections, the warning <see cref="ScannedPdfWarning"/> and the page images.
/// </summary>
internal sealed class PdfParser : IDocumentParser
{
    /// <summary>Warning code for a PDF without a usable text layer.</summary>
    public const string ScannedPdfWarning = "scanned-pdf";

    /// <summary>Warning code added when the character limit cut the extracted text.</summary>
    public const string TruncatedWarning = "pdf-text-truncated";

    /// <summary>Warning code added when a scanned PDF has more pages than <see cref="MaxImagePages"/>.</summary>
    public const string ImagesLimitedWarning = "scanned-pdf-images-limited";

    /// <summary>Prefix of the warning added for a page that could not be read, e.g. "pdf-page-unreadable:3".</summary>
    public const string PageUnreadableWarningPrefix = "pdf-page-unreadable:";

    /// <summary>Below this many non-whitespace characters per page (on average) a PDF is treated as scanned.</summary>
    internal const int ScannedCharsPerPageThreshold = 20;

    /// <summary>Only images of the first pages are collected for phase-2 OCR.</summary>
    internal const int MaxImagePages = 50;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".pdf"];

    public Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() => Parse(context, cancellationToken), cancellationToken);
    }

    /// <summary>Maps an exception thrown while opening or reading a PDF to the error code shown to the user.</summary>
    internal static DocumentParseException MapException(Exception exception) => exception switch
    {
        DocumentParseException known => known,
        PdfDocumentEncryptedException => new DocumentParseException(DocumentErrorCode.PasswordProtected, "The PDF is password protected.", exception),
        _ => new DocumentParseException(DocumentErrorCode.Corrupted, "The PDF could not be opened.", exception),
    };

    private static ParsedDocument Parse(ParseContext context, CancellationToken cancellationToken)
    {
        PdfDocument document;
        try
        {
            if (context.Content.CanSeek)
            {
                context.Content.Position = 0;
            }

            document = PdfDocument.Open(context.Content, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw MapException(ex);
        }

        using (document)
        {
            try
            {
                return ParseDocument(document, context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not DocumentParseException)
            {
                throw MapException(ex);
            }
        }
    }

    private static ParsedDocument ParseDocument(PdfDocument document, ParseContext context, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var pageCount = document.NumberOfPages;
        if (pageCount == 0)
        {
            return ParsedDocument.Empty;
        }

        var maxChars = context.Options.MaxExtractedChars;
        var pages = new List<PdfPageText>(pageCount);
        var rawChars = 0L;
        var failedPages = 0;
        var truncated = false;

        for (var number = 1; number <= pageCount; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var text = ExtractPage(document.GetPage(number), number);
                pages.Add(text);
                rawChars += text.NonWhitespaceCount;
            }
            catch (PdfDocumentEncryptedException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                failedPages++;
                warnings.Add(PageUnreadableWarningPrefix + number);
            }

            if (rawChars >= maxChars && number < pageCount)
            {
                truncated = true;
                break;
            }
        }

        if (failedPages == pageCount)
        {
            throw new DocumentParseException(DocumentErrorCode.Corrupted, "No page of the PDF could be read.");
        }

        var embedded = ExtractEmbeddedFiles(document, context);

        if ((double)rawChars / pageCount < ScannedCharsPerPageThreshold)
        {
            var images = CollectImages(document, context, pageCount, warnings, cancellationToken);
            warnings.Add(ScannedPdfWarning);
            return new ParsedDocument([], embedded, images, [], warnings);
        }

        var sections = new List<DocumentSection>();
        var emitted = 0;
        foreach (var page in PdfTextLayout.RemoveHeadersAndFooters(pages))
        {
            var text = PdfTextLayout.BuildPageText(page);
            if (text.Length == 0)
            {
                continue;
            }

            if (emitted + text.Length > maxChars)
            {
                text = text[..Math.Max(0, maxChars - emitted)].TrimEnd();
                truncated = true;
                if (text.Length > 0)
                {
                    sections.Add(CreateSection(text, page.PageNumber, context));
                }

                break;
            }

            emitted += text.Length;
            sections.Add(CreateSection(text, page.PageNumber, context));
        }

        if (truncated)
        {
            warnings.Add(TruncatedWarning);
        }

        return new ParsedDocument(sections, embedded, [], [], warnings);
    }

    private static DocumentSection CreateSection(string text, int pageNumber, ParseContext context) =>
        new(SectionKind.Prose, text, new SourceLocation { Page = pageNumber, EmbeddedPath = context.EmbeddedPath });

    /// <summary>Words -> lines -> blocks (Docstrum) -> reading order. Falls back to plain row grouping if layout analysis fails.</summary>
    private static PdfPageText ExtractPage(Page page, int number)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance)
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();
        if (words.Count == 0)
        {
            return new PdfPageText(number, []);
        }

        try
        {
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
            var result = new List<PdfBlock>();
            foreach (var block in ordered)
            {
                var lines = block.TextLines
                    .Select(l => new PdfLine(l.Text, l.BoundingBox.Left, l.BoundingBox.Bottom, l.BoundingBox.Top))
                    .Where(l => l.Text.Trim().Length > 0)
                    .ToList();
                if (lines.Count > 0)
                {
                    result.Add(new PdfBlock(lines));
                }
            }

            return new PdfPageText(number, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PdfPageText(number, [FallbackBlock(words)]);
        }
    }

    /// <summary>Groups words into rows by vertical position, top to bottom, left to right. One block for the whole page.</summary>
    private static PdfBlock FallbackBlock(List<PigWord> words)
    {
        var rows = new List<List<PigWord>>();
        foreach (var word in words.OrderByDescending(w => (w.BoundingBox.Bottom + w.BoundingBox.Top) / 2))
        {
            var center = (word.BoundingBox.Bottom + word.BoundingBox.Top) / 2;
            var row = rows.FirstOrDefault(r => center >= r.Min(x => x.BoundingBox.Bottom) && center <= r.Max(x => x.BoundingBox.Top));
            if (row is null)
            {
                rows.Add([word]);
            }
            else
            {
                row.Add(word);
            }
        }

        var lines = rows
            .Select(r => r.OrderBy(w => w.BoundingBox.Left).ToList())
            .Select(r => new PdfLine(string.Join(' ', r.Select(w => w.Text)), r[0].BoundingBox.Left, r.Min(w => w.BoundingBox.Bottom), r.Max(w => w.BoundingBox.Top)))
            .ToList();
        return new PdfBlock(lines);
    }

    private static List<ExtractedImage> CollectImages(PdfDocument document, ParseContext context, int pageCount, List<string> warnings, CancellationToken cancellationToken)
    {
        var images = new List<ExtractedImage>();
        var limit = Math.Min(pageCount, MaxImagePages);
        if (pageCount > MaxImagePages)
        {
            warnings.Add(ImagesLimitedWarning);
        }

        for (var number = 1; number <= limit; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var location = new SourceLocation { Page = number, EmbeddedPath = context.EmbeddedPath };
                var index = 0;
                foreach (var image in document.GetPage(number).GetImages())
                {
                    index++;
                    var (bytes, contentType, extension) = Encode(image);
                    if (bytes is null)
                    {
                        continue;
                    }

                    images.Add(new ExtractedImage($"page{number}-image{index}{extension}", contentType, bytes, location, null));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A page whose images cannot be read is simply skipped; OCR is a phase-2 feature.
            }
        }

        return images;
    }

    private static (byte[]? Bytes, string ContentType, string Extension) Encode(IPdfImage image)
    {
        if (image.TryGetPng(out var png) && png is { Length: > 0 })
        {
            return (png, "image/png", ".png");
        }

        // DCT-encoded images keep their original JPEG bytes.
        var raw = image.RawBytes.ToArray();
        if (raw.Length > 2 && raw[0] == 0xFF && raw[1] == 0xD8)
        {
            return (raw, "image/jpeg", ".jpg");
        }

        return (null, string.Empty, string.Empty);
    }

    private static List<EmbeddedFile> ExtractEmbeddedFiles(PdfDocument document, ParseContext context)
    {
        var result = new List<EmbeddedFile>();
        try
        {
            if (!document.Advanced.TryGetEmbeddedFiles(out var files) || files is null)
            {
                return result;
            }

            var location = new SourceLocation { EmbeddedPath = context.EmbeddedPath };
            foreach (PigEmbeddedFile file in files)
            {
                var name = Path.GetFileName(file.Name?.Replace('\\', '/') ?? string.Empty);
                var bytes = file.Bytes.ToArray();
                if (string.IsNullOrWhiteSpace(name) || bytes.Length == 0)
                {
                    continue;
                }

                result.Add(new EmbeddedFile(name, bytes, location));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Attachments are optional; a broken name tree must not fail the whole document.
        }

        return result;
    }
}
