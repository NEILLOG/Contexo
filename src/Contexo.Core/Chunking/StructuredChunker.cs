using Contexo.Core.Abstractions;

namespace Contexo.Core.Chunking;

/// <summary>
/// Cuts parsed sections into chunks without destroying their structure: prose is split at paragraph / sentence boundaries with overlap,
/// tables are split at row boundaries repeating the header, slides, notes and diagrams stay whole, table summaries are never split.
/// Pure and thread-safe.
/// </summary>
internal sealed class StructuredChunker : IChunker
{
    private const string PrefixSeparator = " › ";

    public IReadOnlyList<Chunk> Split(string documentTitle, IReadOnlyList<DocumentSection> sections, ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(options);

        var hardMax = Math.Max(1, options.HardMaxChars);
        var max = Math.Clamp(options.MaxChars, 1, hardMax);
        var overlap = Math.Clamp(options.OverlapChars, 0, max - 1);
        var min = Math.Max(0, options.MinChars);

        var items = new List<Item>();
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            if (section is null || string.IsNullOrWhiteSpace(section.Text))
            {
                continue;
            }

            AddSection(items, section, i, max, hardMax);
        }

        MergeShortPieces(items, min, max);

        var chunks = new List<Chunk>(items.Count);
        Piece? previous = null;
        foreach (var item in items)
        {
            if (item.Piece is { } piece)
            {
                var text = BuildPieceText(piece, previous, overlap);
                previous = piece;
                AddChunk(chunks, documentTitle, SectionKind.Prose, text, piece.Location, null);
            }
            else
            {
                previous = null;
                var draft = item.Draft!;
                AddChunk(chunks, documentTitle, draft.Kind, draft.Text, draft.Location, draft.TableKey);
            }
        }

        return chunks;
    }

    private static void AddSection(List<Item> items, DocumentSection section, int index, int max, int hardMax)
    {
        switch (section.Kind)
        {
            case SectionKind.TableSummary:
                items.Add(new Item(new Draft(section.Kind, section.Text.Trim(), section.Location, section.TableKey), null));
                break;

            case SectionKind.Table:
                AddTable(items, section, hardMax);
                break;

            case SectionKind.Prose when !section.KeepWhole:
                foreach (var segments in ProseSplitter.Split(section.Text, max))
                {
                    items.Add(new Item(null, new Piece(segments, index, index, section.Location)));
                }

                break;

            default:
                // Prose with KeepWhole, Slide, Notes, Diagram.
                AddWhole(items, section, hardMax);
                break;
        }
    }

    private static void AddWhole(List<Item> items, DocumentSection section, int hardMax)
    {
        var text = section.Text.Trim();
        if (TextMeasure.Length(text) <= hardMax)
        {
            items.Add(new Item(new Draft(section.Kind, text, section.Location, section.TableKey), null));
            return;
        }

        foreach (var segments in ProseSplitter.Split(text, hardMax))
        {
            var part = ProseSplitter.Join(segments).TrimEnd();
            if (part.Length > 0)
            {
                items.Add(new Item(new Draft(section.Kind, part, section.Location, section.TableKey), null));
            }
        }
    }

    private static void AddTable(List<Item> items, DocumentSection section, int hardMax)
    {
        var text = section.Text.Trim();
        if (TextMeasure.Length(text) <= hardMax)
        {
            items.Add(new Item(new Draft(section.Kind, text, section.Location, section.TableKey), null));
            return;
        }

        var parts = HtmlTableSplitter.Split(text, hardMax);
        if (parts is null)
        {
            AddWhole(items, section, hardMax);
            return;
        }

        foreach (var part in parts)
        {
            items.Add(new Item(new Draft(section.Kind, part, section.Location, section.TableKey), null));
        }
    }

    /// <summary>Merges prose pieces shorter than <paramref name="min"/> into a neighbour with the same location (previous first, then next) as long as the result fits.</summary>
    private static void MergeShortPieces(List<Item> items, int min, int max)
    {
        var i = 0;
        while (i < items.Count)
        {
            if (items[i].Piece is not { } piece || piece.Length >= min)
            {
                i++;
                continue;
            }

            if (i > 0 && items[i - 1].Piece is { } before && CanMerge(before, piece, max))
            {
                items[i - 1] = new Item(null, Merge(before, piece));
                items.RemoveAt(i);
                i--;
                continue;
            }

            if (i + 1 < items.Count && items[i + 1].Piece is { } after && CanMerge(piece, after, max))
            {
                items[i] = new Item(null, Merge(piece, after));
                items.RemoveAt(i + 1);
                continue;
            }

            i++;
        }
    }

    private static bool CanMerge(Piece first, Piece second, int max) =>
        SameFlow(first.Location, second.Location) && first.Length + JoinSeparator(second).Length + second.Length <= max;

    private static string JoinSeparator(Piece second) => second.Segments[0].Sep.Length > 0 ? second.Segments[0].Sep : "\n";

    private static Piece Merge(Piece first, Piece second)
    {
        var segments = new List<Seg>(first.Segments.Count + second.Segments.Count);
        segments.AddRange(first.Segments);
        segments.Add(second.Segments[0] with { Sep = JoinSeparator(second) });
        for (var i = 1; i < second.Segments.Count; i++)
        {
            segments.Add(second.Segments[i]);
        }

        return new Piece(segments, first.FirstSection, second.LastSection, first.Location);
    }

    private static bool SameFlow(SourceLocation a, SourceLocation b) =>
        a.Page == b.Page
        && a.Slide == b.Slide
        && string.Equals(a.Sheet, b.Sheet, StringComparison.Ordinal)
        && string.Equals(a.Title, b.Title, StringComparison.Ordinal)
        && SamePath(a.HeadingPath, b.HeadingPath)
        && SamePath(a.EmbeddedPath, b.EmbeddedPath);

    private static bool SamePath(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if ((a?.Count ?? 0) != (b?.Count ?? 0))
        {
            return false;
        }

        for (var i = 0; i < (a?.Count ?? 0); i++)
        {
            if (!string.Equals(a![i], b![i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Piece text, preceded by the tail of the previous piece when both came from the same section.</summary>
    private static string BuildPieceText(Piece piece, Piece? previous, int overlap)
    {
        var body = ProseSplitter.Join(piece.Segments);
        if (previous is null || previous.LastSection != piece.FirstSection || overlap <= 0)
        {
            return body;
        }

        var separator = piece.Segments[0].Sep;
        var tail = ProseSplitter.Overlap(previous.Segments, overlap - TextMeasure.Length(separator));
        return tail.Length == 0 ? body : tail + separator + body;
    }

    private static void AddChunk(List<Chunk> chunks, string documentTitle, SectionKind kind, string text, SourceLocation location, string? tableKey)
    {
        text = text.TrimEnd();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var prefix = BuildPrefix(documentTitle, location);
        var embeddingText = prefix.Length == 0 ? text : prefix + "\n" + text;
        chunks.Add(new Chunk(chunks.Count, kind, text, embeddingText, location, tableKey));
    }

    private static string BuildPrefix(string documentTitle, SourceLocation location)
    {
        var parts = new List<string>();

        void Add(string? part)
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                parts.Add(part.Trim());
            }
        }

        Add(documentTitle);
        if (location.EmbeddedPath is { } embedded)
        {
            foreach (var name in embedded)
            {
                Add(name);
            }
        }

        if (!string.IsNullOrWhiteSpace(location.Sheet))
        {
            Add(location.Sheet);
        }
        else if (location.Slide is { } slide)
        {
            Add($"第 {slide} 張投影片");
        }
        else if (location.Page is { } page)
        {
            Add($"第 {page} 頁");
        }

        string? lastHeading = null;
        if (location.HeadingPath is { } headings)
        {
            foreach (var heading in headings)
            {
                Add(heading);
                lastHeading = heading;
            }
        }

        if (!string.IsNullOrWhiteSpace(location.Title)
            && !string.Equals(location.Title.Trim(), lastHeading?.Trim(), StringComparison.Ordinal))
        {
            Add(location.Title);
        }

        return string.Join(PrefixSeparator, parts);
    }

    private sealed record Draft(SectionKind Kind, string Text, SourceLocation Location, string? TableKey);

    private sealed class Piece(List<Seg> segments, int firstSection, int lastSection, SourceLocation location)
    {
        public List<Seg> Segments { get; } = segments;
        public int FirstSection { get; } = firstSection;
        public int LastSection { get; } = lastSection;
        public SourceLocation Location { get; } = location;
        public int Length { get; } = ProseSplitter.JoinedLength(segments);
    }

    private sealed record Item(Draft? Draft, Piece? Piece);
}
