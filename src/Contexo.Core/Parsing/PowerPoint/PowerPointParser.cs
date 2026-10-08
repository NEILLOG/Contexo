using System.Text;
using System.Xml;
using Contexo.Core.Abstractions;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;

namespace Contexo.Core.Parsing.PowerPoint;

/// <summary>
/// Parses .pptx files. Emits per slide a <see cref="SectionKind.Slide"/> section (title, text in reading order, tables, chart data),
/// a <see cref="SectionKind.Diagram"/> section (connector relations as Mermaid-style lines and SmartArt trees) and a
/// <see cref="SectionKind.Notes"/> section. Layout and master text (footers, company names) is never emitted.
/// </summary>
internal sealed class PowerPointParser : IDocumentParser
{
    private const string HiddenSuffix = "（隱藏）";

    // Compound-file header: password protected OOXML files are wrapped in one (an old .ppt looks the same).
    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".pptx"];

    public Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var stream = context.Content;
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        CheckNotEncrypted(stream);

        try
        {
            using var document = PresentationDocument.Open(stream, false);
            return Task.FromResult(Read(document, context, cancellationToken));
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or XmlException
            or FormatException or ArgumentException or InvalidOperationException or IOException or KeyNotFoundException)
        {
            throw new DocumentParseException(DocumentErrorCode.Corrupted, "無法開啟這份簡報，檔案可能已損毀。", ex);
        }
    }

    private static ParsedDocument Read(PresentationDocument document, ParseContext context, CancellationToken cancellationToken)
    {
        var presentationPart = document.PresentationPart
            ?? throw new DocumentParseException(DocumentErrorCode.Corrupted, "簡報檔缺少投影片資料。");

        var sections = new List<DocumentSection>();
        var embedded = new List<EmbeddedFile>();
        var images = new List<ExtractedImage>();
        var warnings = new List<string>();
        var budget = Math.Max(context.Options.MaxExtractedChars, 0);
        var used = 0;
        var truncated = false;

        var slideNumber = 0;
        foreach (var slideId in presentationPart.Presentation?.SlideIdList?.Elements<P.SlideId>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            slideNumber++;

            var relationshipId = slideId.RelationshipId?.Value;
            var slidePart = relationshipId is null
                ? null
                : presentationPart.Parts.Where(p => p.RelationshipId == relationshipId).Select(p => p.OpenXmlPart).FirstOrDefault() as SlidePart;
            if (slidePart is null)
            {
                warnings.Add($"第 {slideNumber} 張投影片找不到內容，已略過。");
                continue;
            }

            var content = SlideReader.Read(slidePart, slideNumber, warnings);
            var hidden = slidePart.Slide?.Show?.Value == false;
            var locationTitle = hidden ? (content.Title ?? string.Empty) + HiddenSuffix : content.Title;
            var location = new SourceLocation
            {
                Slide = slideNumber,
                Title = locationTitle,
                EmbeddedPath = context.EmbeddedPath,
            };

            if (!truncated)
            {
                if (content.BodyText.Length > 0)
                {
                    truncated = !TryAdd(sections, new DocumentSection(SectionKind.Slide, content.BodyText, location, KeepWhole: true), ref used, budget);
                }

                if (!truncated && content.DiagramText.Length > 0)
                {
                    truncated = !TryAdd(sections, new DocumentSection(SectionKind.Diagram, content.DiagramText, location, KeepWhole: true), ref used, budget);
                }

                var notes = SlideReader.ReadNotes(slidePart);
                if (!truncated && notes is not null)
                {
                    truncated = !TryAdd(sections, new DocumentSection(SectionKind.Notes, notes, location), ref used, budget);
                }

                if (truncated)
                {
                    warnings.Add("內容太多，後面的部分沒有讀取。");
                }
            }

            embedded.AddRange(OfficeEmbeddedContent.ExtractEmbeddedFiles(slidePart, location));
            images.AddRange(OfficeEmbeddedContent.ExtractImages(slidePart, location, content.Title));
        }

        return new ParsedDocument(sections, embedded, images, [], warnings);
    }

    private static bool TryAdd(List<DocumentSection> sections, DocumentSection section, ref int used, int budget)
    {
        if (used + section.Text.Length > budget)
        {
            return false;
        }

        used += section.Text.Length;
        sections.Add(section);
        return true;
    }

    /// <summary>A compound file is either a password-protected pptx (contains "EncryptedPackage") or a legacy .ppt we do not read.</summary>
    private static void CheckNotEncrypted(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return;
        }

        var header = new byte[CompoundFileSignature.Length];
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        if (read != header.Length || !header.AsSpan().SequenceEqual(CompoundFileSignature))
        {
            return;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        stream.Position = 0;

        var marker = Encoding.Unicode.GetBytes("EncryptedPackage");
        if (buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf(marker) >= 0)
        {
            throw new DocumentParseException(DocumentErrorCode.PasswordProtected, "這份簡報有設定密碼，無法讀取。");
        }

        throw new DocumentParseException(DocumentErrorCode.Corrupted, "這不是新版的 PowerPoint 檔案（.pptx），無法讀取。");
    }
}
