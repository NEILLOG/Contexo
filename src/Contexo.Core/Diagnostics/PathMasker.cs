using System.Text.RegularExpressions;
using Contexo.Core.Abstractions;

namespace Contexo.Core.Diagnostics;

/// <summary>
/// Masks personal information in log lines. Secrets are always masked; paths only when full paths were not requested:
/// watched folder paths become the folder's display name and the user's profile folder becomes %USERPROFILE%.
/// </summary>
internal sealed partial class PathMasker
{
    private const string ProfilePlaceholder = "%USERPROFILE%";

    // "C:\Users\bob", "C:\\Users\\bob" (JSON-escaped), "/Users/bob", "/home/bob": the account name is the part to hide.
    [GeneratedRegex(@"(?:[A-Za-z]:[\\/]+Users[\\/]+|/Users/|/home/)[^\\/\s""'<>|:*?]+", RegexOptions.IgnoreCase)]
    private static partial Regex ProfilePathPattern { get; }

    [GeneratedRegex(@"\b(?<name>api[_-]?key|secret|token|password|passwd)(?<sep>[""']?\s*[:=]\s*[""']?)[^\s""',;]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern { get; }

    private readonly List<(string Needle, string Replacement)> _replacements = [];
    private readonly bool _maskPaths;

    public PathMasker(IReadOnlyList<WatchedFolder> folders, string userProfile, bool includeFullPaths)
    {
        _maskPaths = !includeFullPaths;
        if (!_maskPaths)
        {
            return;
        }

        foreach (var folder in folders.OrderByDescending(f => f.Path.Length))
        {
            AddVariants(folder.Path, folder.DisplayName);
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            AddVariants(userProfile, ProfilePlaceholder);
        }
    }

    public string MaskLogLine(string line)
    {
        var result = SecretPattern.Replace(line, m => m.Groups["name"].Value + m.Groups["sep"].Value + "***");
        if (!_maskPaths)
        {
            return result;
        }

        foreach (var (needle, replacement) in _replacements)
        {
            result = result.Replace(needle, replacement, StringComparison.OrdinalIgnoreCase);
        }

        return ProfilePathPattern.Replace(result, ProfilePlaceholder);
    }

    /// <summary>The part after the last '\' or '/', whatever the current platform's separator is.</summary>
    public static string FileNameOf(string path)
    {
        var index = path.LastIndexOfAny(['\\', '/']);
        return index < 0 ? path : path[(index + 1)..];
    }

    private void AddVariants(string path, string replacement)
    {
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0)
        {
            return;
        }

        foreach (var variant in new[] { trimmed, trimmed.Replace('\\', '/'), trimmed.Replace('/', '\\'), trimmed.Replace("\\", "\\\\") }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _replacements.Add((variant, replacement));
        }
    }
}
