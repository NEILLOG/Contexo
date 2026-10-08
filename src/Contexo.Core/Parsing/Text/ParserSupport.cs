using Contexo.Core.Abstractions;

namespace Contexo.Core.Parsing.Text;

/// <summary>Helpers shared by the plain text, HTML and RTF parsers.</summary>
internal static class ParserSupport
{
    /// <summary>Lower-case extension of the file name including the dot, or an empty string.</summary>
    public static string GetExtension(string fileName) => Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();

    /// <summary>Reads the whole stream from the start (when seekable). The stream is not disposed.</summary>
    public static byte[] ReadAllBytes(Stream stream)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static SourceLocation CreateLocation(ParseContext context, IReadOnlyList<string>? headingPath = null) => new()
    {
        HeadingPath = headingPath is { Count: > 0 } ? headingPath : null,
        EmbeddedPath = context.EmbeddedPath,
    };

    public static ParsedDocument ToDocument(List<DocumentSection> sections, List<string> warnings) =>
        sections.Count == 0 && warnings.Count == 0
            ? ParsedDocument.Empty
            : new ParsedDocument(sections, [], [], [], warnings);

    /// <summary>
    /// Enforces <see cref="ParserOptions.MaxExtractedChars"/> over the whole file: later content is dropped, a prose section
    /// that crosses the limit is cut, and a warning is added. Tables are never cut in the middle.
    /// </summary>
    public static List<DocumentSection> ApplyLimit(List<DocumentSection> sections, int maxChars, List<string> warnings)
    {
        if (maxChars <= 0)
        {
            return sections;
        }

        var result = new List<DocumentSection>(sections.Count);
        var total = 0;
        foreach (var section in sections)
        {
            if (total + section.Text.Length <= maxChars)
            {
                result.Add(section);
                total += section.Text.Length;
                continue;
            }

            var remaining = maxChars - total;
            if (section.Kind == SectionKind.Prose && remaining > 0)
            {
                result.Add(section with { Text = CutAtLineBoundary(section.Text, remaining, fromEnd: false) });
            }

            warnings.Add("內容過長，後面的部分沒有讀取。");
            return result;
        }

        return result;
    }

    /// <summary>Cuts <paramref name="text"/> to at most <paramref name="length"/> characters, preferring a line boundary.</summary>
    public static string CutAtLineBoundary(string text, int length, bool fromEnd)
    {
        if (text.Length <= length)
        {
            return text;
        }

        if (!fromEnd)
        {
            var cut = text.LastIndexOf('\n', length - 1, length);
            return (cut > length / 2 ? text[..cut] : text[..length]).TrimEnd();
        }

        var start = text.Length - length;
        var newline = text.IndexOf('\n', start);
        if (newline >= 0 && newline < text.Length - length / 2)
        {
            start = newline + 1;
        }

        // Do not start in the middle of a surrogate pair.
        if (start < text.Length && char.IsLowSurrogate(text[start]))
        {
            start++;
        }

        return text[start..].TrimStart();
    }
}
