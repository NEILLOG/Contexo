using Contexo.Core.Abstractions;

namespace Contexo.Mcp.Tools;

/// <summary>
/// Turns a <see cref="SourceLocation"/> into the short location text shown after the file name in search results.
/// The format is deliberately identical to Contexo.App.Search.LocationText (the desktop "try search" screen),
/// so a user sees the same wording in both places. Contexo.Mcp does not reference Contexo.App, hence this copy.
/// </summary>
internal static class LocationText
{
    private const string Separator = " · ";
    private const string PathSeparator = " › ";

    /// <returns>The text, or an empty string when the location carries nothing to show.</returns>
    public static string Format(SourceLocation? location)
    {
        if (location is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(location.Sheet))
        {
            parts.Add($"工作表「{location.Sheet}」");
            if (!string.IsNullOrWhiteSpace(location.CellRange))
            {
                parts.Add(location.CellRange);
            }
        }
        else if (!string.IsNullOrWhiteSpace(location.CellRange))
        {
            parts.Add(location.CellRange);
        }

        var titleUsed = false;
        if (location.Slide is { } slide)
        {
            parts.Add(string.IsNullOrWhiteSpace(location.Title) ? $"第 {slide} 張投影片" : $"第 {slide} 張投影片「{location.Title}」");
            titleUsed = true;
        }

        if (location.Page is { } page)
        {
            parts.Add($"第 {page} 頁");
        }

        if (location.HeadingPath is { Count: > 0 } headings)
        {
            var path = string.Join(PathSeparator, headings.Where(h => !string.IsNullOrWhiteSpace(h)));
            if (path.Length > 0)
            {
                parts.Add(path);
            }
        }

        if (!titleUsed && !string.IsNullOrWhiteSpace(location.Title) && !(location.HeadingPath?.Contains(location.Title) ?? false))
        {
            parts.Add($"「{location.Title}」");
        }

        var main = string.Join(Separator, parts);

        if (location.EmbeddedPath is { Count: > 0 } embedded)
        {
            var prefix = "內嵌：" + string.Join(PathSeparator, embedded);
            return main.Length == 0 ? prefix : prefix + PathSeparator + main;
        }

        return main;
    }
}
