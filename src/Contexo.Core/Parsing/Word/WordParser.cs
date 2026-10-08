using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Contexo.Core.Parsing.Word;

/// <summary>
/// Parser for .docx. Emits prose sections split at headings and one section per table, in reading order.
/// Tracked changes are read as if accepted; headers, footers and automatic tables of contents are ignored.
/// </summary>
internal sealed class WordParser : IDocumentParser
{
    private const int MaxHeadingLength = 40;
    private const int MaxContextTextLength = 500;

    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".docx"];

    public Task<ParsedDocument> ParseAsync(ParseContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var stream = context.Content;
        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        if (HasOleHeader(stream))
        {
            throw new DocumentParseException(DocumentErrorCode.PasswordProtected, "The Word file is password protected or is not an Open XML document.");
        }

        WordprocessingDocument document;
        try
        {
            document = WordprocessingDocument.Open(stream, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DocumentParseException(DocumentErrorCode.Corrupted, "The Word file could not be opened.", ex);
        }

        using (document)
        {
            try
            {
                return Task.FromResult(Parse(document, context, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not DocumentParseException)
            {
                throw new DocumentParseException(DocumentErrorCode.Corrupted, "The Word file could not be read.", ex);
            }
        }
    }

    private static bool HasOleHeader(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[4];
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        stream.Seek(0, SeekOrigin.Begin);
        return read == 4 && header[0] == 0xD0 && header[1] == 0xCF && header[2] == 0x11 && header[3] == 0xE0;
    }

    private static ParsedDocument Parse(WordprocessingDocument document, ParseContext parseContext, CancellationToken cancellationToken)
    {
        var main = document.MainDocumentPart;
        var body = main?.Document?.Body;
        if (main is null || body is null)
        {
            throw new DocumentParseException(DocumentErrorCode.Corrupted, "The file has no Word document body.");
        }

        var context = new WordContext(main);
        var blocks = ReadBlocks(body, context, cancellationToken);
        AssignHeadings(blocks);

        var builder = new SectionBuilder(parseContext, context, cancellationToken);
        foreach (var block in blocks)
        {
            if (!builder.Add(block))
            {
                break;
            }
        }

        builder.AppendComments(ReadComments(main, context));
        return builder.Finish(main);
    }

    // ---- pass 1: read every body block in order ----

    private static List<Block> ReadBlocks(Body body, WordContext context, CancellationToken cancellationToken)
    {
        var blocks = new List<Block>();
        var extractor = new WordTextExtractor(context);
        CollectBlocks(body, context, extractor, blocks, cancellationToken);
        return blocks;
    }

    private static void CollectBlocks(OpenXmlElement container, WordContext context, WordTextExtractor extractor, List<Block> blocks, CancellationToken cancellationToken)
    {
        foreach (var child in container.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (child)
            {
                case Paragraph paragraph:
                    blocks.Add(ReadParagraphBlock(paragraph, context, extractor));
                    break;

                case Table table:
                    blocks.Add(new Block { Table = table });
                    break;

                case SdtBlock sdt:
                    if (!WordBlocks.IsTableOfContents(sdt) && sdt.SdtContentBlock is { } content)
                    {
                        CollectBlocks(content, context, extractor, blocks, cancellationToken);
                    }

                    break;

                case CustomXmlBlock custom:
                    CollectBlocks(custom, context, extractor, blocks, cancellationToken);
                    break;
            }
        }
    }

    private static Block ReadParagraphBlock(Paragraph paragraph, WordContext context, WordTextExtractor extractor)
    {
        var result = extractor.ReadParagraph(paragraph);
        var styles = context.Styles;
        var block = new Block
        {
            Paragraph = paragraph,
            Result = result,
            References = context.TakePendingReferences(),
            IsToc = extractor.TocTouched || styles.IsTocStyle(paragraph),
            IsList = styles.IsListItem(paragraph),
            StyleLevel = styles.GetHeadingLevel(paragraph),
        };

        // Formatting used by the "bold and large" heading heuristic.
        var allBold = true;
        var size = 0;
        foreach (var run in EnumerateRuns(paragraph))
        {
            var length = VisibleLength(run);
            if (length == 0)
            {
                continue;
            }

            var runSize = styles.GetRunSize(run, paragraph);
            block.SizeChars[runSize] = block.SizeChars.GetValueOrDefault(runSize) + length;
            size = Math.Max(size, runSize);
            if (!styles.IsRunBold(run, paragraph))
            {
                allBold = false;
            }
        }

        block.AllBold = allBold && size > 0;
        block.Size = size;
        return block;
    }

    private static IEnumerable<Run> EnumerateRuns(OpenXmlElement element)
    {
        foreach (var child in element.ChildElements)
        {
            switch (child)
            {
                case Run run:
                    yield return run;
                    break;

                case DeletedRun:
                case MoveFromRun:
                case ParagraphProperties:
                    break;

                default:
                    foreach (var nested in EnumerateRuns(child))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    private static int VisibleLength(Run run)
    {
        var length = 0;
        foreach (var text in run.Elements<DocumentFormat.OpenXml.Wordprocessing.Text>())
        {
            foreach (var ch in text.Text)
            {
                if (!char.IsWhiteSpace(ch))
                {
                    length++;
                }
            }
        }

        return length;
    }

    // ---- heading detection for paragraphs without heading styles ----

    private static void AssignHeadings(List<Block> blocks)
    {
        var histogram = new Dictionary<int, int>();
        foreach (var block in blocks)
        {
            if (block.Paragraph is null || block.IsToc || block.StyleLevel is not null)
            {
                continue;
            }

            foreach (var (size, chars) in block.SizeChars)
            {
                histogram[size] = histogram.GetValueOrDefault(size) + chars;
            }
        }

        if (histogram.Count == 0)
        {
            return;
        }

        // Mode of the font size by character count; ties go to the smaller size.
        var bodySize = histogram.OrderByDescending(static p => p.Value).ThenBy(static p => p.Key).First().Key;

        bool IsCandidate(Block block) =>
            block.Paragraph is not null
            && !block.IsToc
            && block.StyleLevel is null
            && !block.IsList
            && block.AllBold
            && block.Size > bodySize
            && block.Result!.Text.Length is > 0 and <= MaxHeadingLength;

        // Walk backwards so each candidate knows what follows the run of candidates it belongs to.
        var followedByBody = false;
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            var block = blocks[i];
            if (block.Table is not null)
            {
                followedByBody = true;
                continue;
            }

            if (block.IsToc || (block.Result!.Text.Length == 0 && block.Result.ExtraLines.Count == 0))
            {
                continue;
            }

            if (block.StyleLevel is not null)
            {
                followedByBody = false;
            }
            else if (IsCandidate(block))
            {
                block.HeuristicHeading = followedByBody;
            }
            else
            {
                followedByBody = true;
            }
        }

        // Levels follow the font size, largest first.
        var sizes = blocks.Where(static b => b.HeuristicHeading).Select(static b => b.Size).Distinct().OrderByDescending(static s => s).ToList();
        foreach (var block in blocks.Where(static b => b.HeuristicHeading))
        {
            block.HeuristicLevel = sizes.IndexOf(block.Size) + 1;
        }
    }

    // ---- comments ----

    private static List<string> ReadComments(MainDocumentPart main, WordContext context)
    {
        var result = new List<string>();
        var comments = main.WordprocessingCommentsPart?.Comments;
        if (comments is null)
        {
            return result;
        }

        foreach (var comment in comments.Elements<Comment>())
        {
            var text = string.Join(' ', new WordTextExtractor(context).ReadLines(comment)).Trim();
            if (text.Length > 0)
            {
                result.Add("〔註解〕" + text);
            }
        }

        return result;
    }

    // ---- pass 2: build sections ----

    private sealed class Block
    {
        public Paragraph? Paragraph { get; init; }

        public Table? Table { get; init; }

        public ParagraphText? Result { get; init; }

        public List<string> References { get; init; } = [];

        public bool IsToc { get; init; }

        public bool IsList { get; init; }

        public int? StyleLevel { get; init; }

        public Dictionary<int, int> SizeChars { get; } = [];

        public bool AllBold { get; set; }

        public int Size { get; set; }

        public bool HeuristicHeading { get; set; }

        public int HeuristicLevel { get; set; }

        public int? HeadingLevel => StyleLevel ?? (HeuristicHeading ? HeuristicLevel : null);
    }

    private sealed record ReferenceLocation(string[] HeadingPath, string? ContextText);

    private sealed class SectionBuilder
    {
        private readonly ParseContext _parseContext;
        private readonly WordContext _context;
        private readonly CancellationToken _cancellationToken;
        private readonly List<DocumentSection> _sections = [];
        private readonly List<string> _warnings = [];
        private readonly List<(int Level, string Text)> _path = [];
        private readonly List<string> _prose = [];
        private readonly Dictionary<string, ReferenceLocation> _references = new(StringComparer.Ordinal);
        private (int Level, string Text, string[] Parent)? _leafHeading;
        private bool _currentHasContent;
        private string? _lastText;
        private int _totalChars;
        private bool _truncated;

        public SectionBuilder(ParseContext parseContext, WordContext context, CancellationToken cancellationToken)
        {
            _parseContext = parseContext;
            _context = context;
            _cancellationToken = cancellationToken;
        }

        /// <summary>Adds one block. Returns false once the character limit has been reached.</summary>
        public bool Add(Block block)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_totalChars > _parseContext.Options.MaxExtractedChars)
            {
                if (!_truncated)
                {
                    _truncated = true;
                    _warnings.Add("文件內容太多，後面的部分沒有讀取。");
                }

                return false;
            }

            if (block.Table is not null)
            {
                AddTable(block);
                return true;
            }

            if (block.IsToc || block.Result is null)
            {
                return true;
            }

            var result = block.Result;
            var level = block.HeadingLevel;
            var headingText = NormalizeHeading(result.Text);
            if (level is not null && headingText.Length > 0)
            {
                FlushProse();
                CloseLeafHeading(level.Value);
                while (_path.Count > 0 && _path[^1].Level >= level.Value)
                {
                    _path.RemoveAt(_path.Count - 1);
                }

                var parent = _path.Select(static p => p.Text).ToArray();
                _path.Add((level.Value, headingText));
                _leafHeading = (level.Value, headingText, parent);
                _currentHasContent = false;
                AddLines(result.ExtraLines);
            }
            else
            {
                var lines = new List<string>();
                WordTextExtractor.AddParagraphLines(lines, result, block.IsList);
                AddLines(lines);
            }

            // A picture on its own line is described by the closest text before it.
            if (result.Text.Length > 0)
            {
                _lastText = result.Text;
            }

            RegisterReferences(block.References, result.Text.Length > 0 ? result.Text : _lastText);
            return true;
        }

        public void AppendComments(List<string> comments)
        {
            if (comments.Count == 0)
            {
                return;
            }

            FlushProse();
            CloseLeafHeading(0);
            var text = string.Join('\n', comments);
            if (_sections.Count > 0 && _sections[^1].Kind == SectionKind.Prose)
            {
                var last = _sections[^1];
                _sections[^1] = last with { Text = last.Text + "\n" + text };
            }
            else
            {
                _sections.Add(new DocumentSection(SectionKind.Prose, text, CurrentLocation()));
            }
        }

        public ParsedDocument Finish(MainDocumentPart main)
        {
            FlushProse();
            CloseLeafHeading(0);

            var embeddedFiles = OfficeEmbeddedContent.ExtractEmbeddedFiles(main, SourceLocation.None)
                .Select(file => file with { ContainerLocation = ReferenceSourceLocation(WordContext.EmbeddedKey(Path.GetFileNameWithoutExtension(file.FileName)), out _) })
                .ToList();
            var images = OfficeEmbeddedContent.ExtractImages(main, SourceLocation.None, null)
                .Select(image =>
                {
                    var location = ReferenceSourceLocation(WordContext.ImageKey(image.FileName), out var contextText);
                    return image with { Location = location, ContextText = contextText };
                })
                .ToList();

            return new ParsedDocument(_sections, embeddedFiles, images, [], _warnings);
        }

        private void AddTable(Block block)
        {
            FlushProse();
            var model = WordBlocks.ReadTable(block.Table!, _context);
            var references = _context.TakePendingReferences();
            if (model is not null)
            {
                var html = HtmlTableRenderer.Render(model);
                _sections.Add(new DocumentSection(SectionKind.Table, html, CurrentLocation(), KeepWhole: true));
                _totalChars += html.Length;
                _currentHasContent = true;
            }

            RegisterReferences(references, null);
        }

        private void AddLines(IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                _prose.Add(line);
                _totalChars += line.Length + 1;
                _currentHasContent = true;
            }
        }

        private void FlushProse()
        {
            if (_prose.Count == 0)
            {
                return;
            }

            var text = string.Join('\n', _prose).Trim();
            _prose.Clear();
            if (text.Length > 0)
            {
                _sections.Add(new DocumentSection(SectionKind.Prose, text, CurrentLocation()));
            }
        }

        /// <summary>
        /// A heading with nothing below it (no text, table or deeper heading) would otherwise vanish, because
        /// headings only appear as the heading path of the sections under them. Emit its text on its own.
        /// </summary>
        private void CloseLeafHeading(int nextLevel)
        {
            if (_leafHeading is { } leaf && !_currentHasContent && nextLevel <= leaf.Level)
            {
                _sections.Add(new DocumentSection(SectionKind.Prose, leaf.Text, LocationFor(leaf.Parent)));
                _totalChars += leaf.Text.Length;
            }

            _leafHeading = null;
        }

        private void RegisterReferences(List<string> keys, string? contextText)
        {
            if (keys.Count == 0)
            {
                return;
            }

            var trimmed = string.IsNullOrWhiteSpace(contextText) ? null : contextText.Trim();
            if (trimmed is { Length: > MaxContextTextLength })
            {
                trimmed = trimmed[..MaxContextTextLength];
            }

            var path = _path.Select(static p => p.Text).ToArray();
            foreach (var key in keys)
            {
                _references.TryAdd(key, new ReferenceLocation(path, trimmed));
            }
        }

        private SourceLocation ReferenceSourceLocation(string key, out string? contextText)
        {
            if (_references.TryGetValue(key, out var reference))
            {
                contextText = reference.ContextText;
                return LocationFor(reference.HeadingPath);
            }

            contextText = null;
            return LocationFor([]);
        }

        private SourceLocation CurrentLocation() => LocationFor(_path.Select(static p => p.Text).ToArray());

        private SourceLocation LocationFor(string[] headingPath) => new()
        {
            HeadingPath = headingPath,
            EmbeddedPath = _parseContext.EmbeddedPath,
        };

        private static string NormalizeHeading(string text) =>
            string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
