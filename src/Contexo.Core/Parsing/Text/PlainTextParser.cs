using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Contexo.Core.Abstractions;
using Contexo.Core.Common;

namespace Contexo.Core.Parsing.Text;

/// <summary>Parses .txt, .md, .markdown, .json, .xml and .log files. Encoding is detected by <see cref="TextDecoder"/>.</summary>
internal sealed partial class PlainTextParser : IDocumentParser
{
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".txt", ".md", ".markdown", ".json", ".xml", ".log"];

    public Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Content.CanSeek)
        {
            context.Content.Position = 0;
        }

        var text = TextDecoder.Decode(context.Content, out _);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(ParsedDocument.Empty);
        }

        var warnings = new List<string>();
        var extension = ParserSupport.GetExtension(context.FileName);
        var max = context.Options.MaxExtractedChars;

        List<DocumentSection> sections;
        switch (extension)
        {
            case ".md":
            case ".markdown":
                sections = ParseMarkdown(text, context);
                break;
            case ".json":
                sections = [Prose(FormatJson(text), context)];
                break;
            case ".xml":
                sections = [Prose(ExtractXmlText(text), context)];
                break;
            case ".log":
                if (max > 0 && text.Length > max)
                {
                    // The interesting part of a log is usually at the end, so keep the tail.
                    text = ParserSupport.CutAtLineBoundary(text, max, fromEnd: true);
                    warnings.Add("記錄檔過長，只讀取了最後的部分。");
                }

                sections = [Prose(text, context)];
                break;
            default:
                sections = [Prose(text, context)];
                break;
        }

        sections = sections.Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
        if (extension != ".log")
        {
            sections = ParserSupport.ApplyLimit(sections, max, warnings);
        }

        return Task.FromResult(ParserSupport.ToDocument(sections, warnings));
    }

    private static DocumentSection Prose(string text, ParseContext context, IReadOnlyList<string>? headingPath = null) =>
        new(SectionKind.Prose, text.Trim(), ParserSupport.CreateLocation(context, headingPath));

    private static string FormatJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, LenientJson);
            return JsonSerializer.Serialize(document.RootElement, IndentedJson).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static string ExtractXmlText(string text)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };

            using var stringReader = new StringReader(text.TrimStart());
            using var reader = XmlReader.Create(stringReader, settings);
            var sb = new StringBuilder();
            while (reader.Read())
            {
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                {
                    var value = reader.Value.Trim();
                    if (value.Length > 0)
                    {
                        sb.Append(value).Append('\n');
                    }
                }
            }

            return sb.ToString();
        }
        catch (XmlException)
        {
            return text;
        }
    }

    private static List<DocumentSection> ParseMarkdown(string text, ParseContext context)
    {
        var sections = new List<DocumentSection>();
        var headings = new List<(int Level, string Text)>();
        var body = new StringBuilder();
        IReadOnlyList<string>? currentPath = null;
        var currentHasBody = false;
        var fenceChar = '\0';
        var fenceLength = 0;

        // A section that holds only its own heading line is dropped (its title lives on in the children's HeadingPath),
        // except at the end of the document so that a headings-only file still yields something.
        void Flush(bool onlyIfBody)
        {
            if (body.Length > 0 && (currentHasBody || !onlyIfBody))
            {
                sections.Add(Prose(body.ToString(), context, currentPath));
            }

            body.Clear();
            currentHasBody = false;
        }

        foreach (var line in text.Split('\n'))
        {
            var fence = FenceRegex().Match(line);
            if (fenceChar != '\0')
            {
                // Inside a code block: everything belongs to the current section until the closing fence.
                body.Append(line).Append('\n');
                currentHasBody = true;
                if (fence.Success && fence.Groups[1].Value[0] == fenceChar && fence.Groups[1].Length >= fenceLength
                    && fence.Groups[2].Value.Trim().Length == 0)
                {
                    fenceChar = '\0';
                }

                continue;
            }

            if (fence.Success)
            {
                fenceChar = fence.Groups[1].Value[0];
                fenceLength = fence.Groups[1].Length;
                body.Append(line).Append('\n');
                currentHasBody = true;
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                Flush(onlyIfBody: true);

                var level = heading.Groups[1].Length;
                var title = ClosingHashesRegex().Replace(heading.Groups[2].Value, string.Empty).Trim();
                while (headings.Count > 0 && headings[^1].Level >= level)
                {
                    headings.RemoveAt(headings.Count - 1);
                }

                headings.Add((level, title));
                currentPath = headings.Select(h => h.Text).Where(t => t.Length > 0).ToArray();
                body.Append(line.Trim()).Append('\n');
                continue;
            }

            body.Append(line).Append('\n');
            if (!string.IsNullOrWhiteSpace(line))
            {
                currentHasBody = true;
            }
        }

        Flush(onlyIfBody: false);
        return sections;
    }

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})(.*)$")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*))?$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"[ \t]+#+[ \t]*$|^#+$")]
    private static partial Regex ClosingHashesRegex();
}
