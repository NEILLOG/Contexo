using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Contexo.Core.Parsing.Word;

/// <summary>
/// Resolves paragraph / run formatting through the style inheritance chain (<c>basedOn</c>) and document defaults.
/// Only the properties the Word parser needs are tracked: heading level, list membership, font size and bold.
/// </summary>
internal sealed partial class WordStyleResolver
{
    /// <summary>Half-points. Word treats a missing size as 10 pt.</summary>
    private const int FallbackSize = 20;
    private const int MaxChainDepth = 32;

    private readonly Dictionary<string, Style> _styles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StyleInfo> _cache = new(StringComparer.Ordinal);
    private readonly string? _defaultParagraphStyleId;
    private readonly int? _defaultSize;
    private readonly bool? _defaultBold;

    public WordStyleResolver(MainDocumentPart main)
    {
        ArgumentNullException.ThrowIfNull(main);

        var styles = main.StyleDefinitionsPart?.Styles;
        if (styles is null)
        {
            return;
        }

        foreach (var style in styles.Elements<Style>())
        {
            var id = style.StyleId?.Value;
            if (id is null)
            {
                continue;
            }

            _styles[id] = style;
            if (style.Type?.Value == StyleValues.Paragraph && style.Default?.Value == true)
            {
                _defaultParagraphStyleId ??= id;
            }
        }

        var defaults = styles.DocDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle;
        if (defaults is not null)
        {
            _defaultSize = ReadSize(defaults.FontSize);
            _defaultBold = ReadBold(defaults.Bold);
        }
    }

    /// <summary>Heading level 1..9 for a paragraph, or null for body text.</summary>
    public int? GetHeadingLevel(Paragraph paragraph)
    {
        var direct = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;
        if (direct is not null)
        {
            return direct <= 8 && direct >= 0 ? direct + 1 : null;
        }

        return GetParagraphStyle(paragraph).HeadingLevel;
    }

    public bool IsListItem(Paragraph paragraph)
    {
        var numbering = paragraph.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value;
        if (numbering is not null)
        {
            return numbering != 0;
        }

        return GetParagraphStyle(paragraph).IsList == true;
    }

    /// <summary>True for paragraphs that use one of the built-in table of contents styles.</summary>
    public bool IsTocStyle(Paragraph paragraph) => GetParagraphStyle(paragraph).IsToc;

    /// <summary>Effective font size in half-points of a run inside <paramref name="paragraph"/>.</summary>
    public int GetRunSize(Run run, Paragraph paragraph)
    {
        var properties = run.RunProperties;
        var direct = ReadSize(properties?.FontSize);
        if (direct is not null)
        {
            return direct.Value;
        }

        var fromRunStyle = Resolve(properties?.RunStyle?.Val?.Value, 0).Size;
        if (fromRunStyle is not null)
        {
            return fromRunStyle.Value;
        }

        return GetParagraphStyle(paragraph).Size ?? _defaultSize ?? FallbackSize;
    }

    public bool IsRunBold(Run run, Paragraph paragraph)
    {
        var properties = run.RunProperties;
        var direct = ReadBold(properties?.Bold);
        if (direct is not null)
        {
            return direct.Value;
        }

        var fromRunStyle = Resolve(properties?.RunStyle?.Val?.Value, 0).Bold;
        if (fromRunStyle is not null)
        {
            return fromRunStyle.Value;
        }

        return GetParagraphStyle(paragraph).Bold ?? _defaultBold ?? false;
    }

    private StyleInfo GetParagraphStyle(Paragraph paragraph)
    {
        var id = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? _defaultParagraphStyleId;
        return Resolve(id, 0);
    }

    private StyleInfo Resolve(string? id, int depth)
    {
        if (id is null || depth > MaxChainDepth || !_styles.TryGetValue(id, out var style))
        {
            return StyleInfo.Empty;
        }

        if (_cache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        // Guards against basedOn cycles.
        _cache[id] = StyleInfo.Empty;

        var parent = Resolve(style.BasedOn?.Val?.Value, depth + 1);
        var name = style.StyleName?.Val?.Value;

        var paragraphProperties = style.StyleParagraphProperties;
        var outline = paragraphProperties?.OutlineLevel?.Val?.Value ?? parent.Outline;
        var nameLevel = MatchHeadingName(name) ?? MatchHeadingName(id) ?? parent.NameLevel;
        var numId = paragraphProperties?.NumberingProperties?.NumberingId?.Val?.Value;
        bool? isList = numId is null ? parent.IsList : numId != 0;
        var isToc = parent.IsToc || MatchesToc(name) || MatchesToc(id);

        var runProperties = style.StyleRunProperties;
        var info = new StyleInfo(
            outline,
            nameLevel,
            isList,
            isToc,
            ReadSize(runProperties?.FontSize) ?? parent.Size,
            ReadBold(runProperties?.Bold) ?? parent.Bold);

        _cache[id] = info;
        return info;
    }

    private static int? ReadSize(FontSize? size) =>
        size?.Val?.Value is { } text && int.TryParse(text, out var value) ? value : null;

    private static bool? ReadBold(Bold? bold) => bold is null ? null : bold.Val?.Value ?? true;

    private static int? MatchHeadingName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var match = HeadingNameRegex().Match(name.Trim());
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static bool MatchesToc(string? name) => !string.IsNullOrEmpty(name) && TocNameRegex().IsMatch(name.Trim());

    [GeneratedRegex(@"^(?:heading|標題)\s*([1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingNameRegex();

    [GeneratedRegex(@"^(?:toc|目錄)\s*[1-9]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TocNameRegex();

    private sealed record StyleInfo(int? Outline, int? NameLevel, bool? IsList, bool IsToc, int? Size, bool? Bold)
    {
        public static StyleInfo Empty { get; } = new(null, null, null, false, null, null);

        /// <summary>Explicit outline level wins (9 means body text); otherwise the style name decides.</summary>
        public int? HeadingLevel => Outline is { } outline ? (outline is >= 0 and <= 8 ? outline + 1 : null) : NameLevel;
    }
}
