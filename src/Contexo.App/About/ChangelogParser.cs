using System.Text.RegularExpressions;

namespace Contexo.App.About;

/// <summary>One release of the version history card.</summary>
/// <param name="Version">Heading text, e.g. "1.4.2" or "未發布".</param>
/// <param name="Date">"2026-10-06" or null when the heading has no date.</param>
public sealed record ChangelogEntry(string Version, string? Date, IReadOnlyList<string> Items)
{
    public bool HasDate => Date is not null;
}

/// <summary>
/// Reads CHANGELOG.md. Format: a "## 1.4.2 - 2026-10-06" heading per release (the date is optional) followed by "- " bullets.
/// Anything else (the title line, blank lines, prose) is ignored.
/// </summary>
public static partial class ChangelogParser
{
    public const int DefaultMaxVersions = 5;

    public const string ResourceName = "CHANGELOG.md";

    [GeneratedRegex(@"^##\s+(?<version>.+?)(?:\s+[-–—]\s+(?<date>\d{4}-\d{2}-\d{2}))?\s*$")]
    private static partial Regex HeadingPattern { get; }

    [GeneratedRegex(@"^\s*[-*+]\s+(?<text>.+?)\s*$")]
    private static partial Regex BulletPattern { get; }

    /// <summary>Returns at most <paramref name="maxVersions"/> releases, in file order (newest first by convention).</summary>
    public static IReadOnlyList<ChangelogEntry> Parse(string? text, int maxVersions = DefaultMaxVersions)
    {
        if (string.IsNullOrWhiteSpace(text) || maxVersions <= 0)
        {
            return [];
        }

        var entries = new List<ChangelogEntry>();
        string? version = null;
        string? date = null;
        var items = new List<string>();

        void Flush()
        {
            if (version is not null)
            {
                entries.Add(new ChangelogEntry(version, date, items.ToArray()));
            }

            items.Clear();
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var heading = HeadingPattern.Match(trimmed);
            if (heading.Success)
            {
                Flush();
                if (entries.Count >= maxVersions)
                {
                    return entries;
                }

                version = heading.Groups["version"].Value.Trim();
                date = heading.Groups["date"].Success ? heading.Groups["date"].Value : null;
                continue;
            }

            var bullet = BulletPattern.Match(trimmed);
            if (version is not null && bullet.Success)
            {
                items.Add(bullet.Groups["text"].Value);
            }
        }

        Flush();
        return entries.Count > maxVersions ? entries.Take(maxVersions).ToArray() : entries;
    }

    /// <summary>Reads the CHANGELOG.md embedded in Contexo.App at build time; null when it was not embedded.</summary>
    public static string? ReadEmbedded()
    {
        using var stream = typeof(ChangelogParser).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
