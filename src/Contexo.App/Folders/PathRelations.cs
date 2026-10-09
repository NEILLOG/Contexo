namespace Contexo.App.Folders;

/// <summary>
/// Path comparison for the folder screens. Works on text only (no file system access) and accepts both '\' and '/',
/// so "C:\A\報價" never contains "C:\A\報價單" and the result does not depend on the operating system running the code.
/// </summary>
public static class PathRelations
{
    /// <summary>Trims trailing separators and unifies separators to '/'. The root ("C:\" or "/") keeps its final separator.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var text = path.Trim().Replace('\\', '/');
        var minimum = text.StartsWith('/') ? 1 : text.Length >= 3 && text[1] == ':' && text[2] == '/' ? 3 : 0;
        while (text.Length > minimum && text.EndsWith('/'))
        {
            text = text[..^1];
        }

        return text;
    }

    public static bool AreSame(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="child"/> lies strictly inside <paramref name="parent"/> (directory boundary, case-insensitive).</summary>
    public static bool IsInside(string parent, string child)
    {
        var p = Normalize(parent);
        var c = Normalize(child);
        if (p.Length == 0 || c.Length <= p.Length)
        {
            return false;
        }

        var prefix = p.EndsWith('/') ? p : p + "/";
        return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The last segment of a path (the folder or file name).</summary>
    public static string GetName(string path)
    {
        var text = Normalize(path);
        var index = text.LastIndexOf('/');
        var name = index >= 0 ? text[(index + 1)..] : text;
        return name.Length > 0 ? name : text;
    }

    /// <summary>Directory names between <paramref name="root"/> and the file or folder at <paramref name="path"/>, excluding the final name.</summary>
    public static IReadOnlyList<string> GetDirectoriesBelow(string root, string path)
    {
        if (!IsInside(root, path))
        {
            return [];
        }

        var r = Normalize(root);
        var relative = Normalize(path)[(r.EndsWith('/') ? r.Length : r.Length + 1)..];
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 1 ? [] : parts[..^1];
    }

    /// <summary>Shortens a long path by replacing its middle with an ellipsis, keeping the beginning and the end.</summary>
    public static string ShortenMiddle(string path, int maxLength = 56)
    {
        if (path.Length <= maxLength || maxLength < 8)
        {
            return path;
        }

        var keep = maxLength - 1;
        var tail = keep * 2 / 3;
        var head = keep - tail;
        return path[..head] + "…" + path[^tail..];
    }
}
