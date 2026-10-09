namespace Contexo.Core.Indexing;

/// <summary>Path rules shared by the scanner and the reconciler: case-insensitive, no trailing separator.</summary>
internal static class PathUtil
{
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Same normalisation as the knowledge store applies to every stored path.</summary>
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSeparator(char c) => c == '\\' || c == '/';

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="directory"/>.</summary>
    public static bool IsUnder(string path, string directory)
    {
        if (path.Length <= directory.Length || !path.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsSeparator(path[directory.Length]) || (directory.Length > 0 && IsSeparator(directory[^1]));
    }

    public static bool IsUnderOrEqual(string path, string directory) =>
        string.Equals(path, directory, StringComparison.OrdinalIgnoreCase) || IsUnder(path, directory);
}
