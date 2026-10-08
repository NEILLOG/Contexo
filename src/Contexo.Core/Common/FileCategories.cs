using Contexo.Core.Abstractions;

namespace Contexo.Core.Common;

/// <summary>Maps <see cref="FileCategory"/> to extensions and holds the built-in exclusion rules.</summary>
public static class FileCategories
{
    private static readonly string[] DocumentExtensions = [".docx", ".txt", ".md", ".markdown", ".json", ".xml", ".log", ".html", ".htm", ".rtf"];
    private static readonly string[] PresentationExtensions = [".pptx"];
    private static readonly string[] SpreadsheetExtensions = [".xlsx", ".xlsm", ".csv"];
    private static readonly string[] PdfExtensions = [".pdf"];

    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".lnk", ".exe", ".dll", ".sys", ".ini", ".db", ".db-wal", ".db-shm",
    };

    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "Thumbs.db", ".DS_Store",
    };

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", ".vs", ".idea", "$RECYCLE.BIN", "System Volume Information",
    };

    /// <summary>Lower-case extensions including the dot. Email and Images have no extensions in version 1.</summary>
    public static IReadOnlyCollection<string> GetExtensions(FileCategory category) => category switch
    {
        FileCategory.Documents => DocumentExtensions,
        FileCategory.Presentations => PresentationExtensions,
        FileCategory.Spreadsheets => SpreadsheetExtensions,
        FileCategory.Pdf => PdfExtensions,
        FileCategory.Email => [],
        FileCategory.Images => [],
        _ => [],
    };

    /// <summary>Extensions of all given categories, lower-case.</summary>
    public static IReadOnlySet<string> GetExtensions(IEnumerable<FileCategory> categories)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            result.UnionWith(GetExtensions(category));
        }

        return result;
    }

    /// <summary>The category of an extension (case-insensitive, with dot), or null when it belongs to none.</summary>
    public static FileCategory? GetCategory(string extension)
    {
        foreach (var category in Enum.GetValues<FileCategory>())
        {
            foreach (var candidate in GetExtensions(category))
            {
                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                {
                    return category;
                }
            }
        }

        return null;
    }

    /// <summary>True for temporary / system files that are never read: "~$" lock files, .tmp, .lnk, .exe, .dll, .sys, .ini, .db files, Thumbs.db, .DS_Store.</summary>
    /// <param name="path">A full path or just a file name.</param>
    public static bool IsBuiltInExcludedFile(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return name.StartsWith("~$", StringComparison.Ordinal)
            || ExcludedFileNames.Contains(name)
            || ExcludedExtensions.Contains(Path.GetExtension(name));
    }

    /// <summary>True for version-control, build output and system folders; every folder starting with '.' is excluded too.</summary>
    /// <param name="name">The folder name (not a path).</param>
    public static bool IsBuiltInExcludedDirectory(string name) =>
        !string.IsNullOrEmpty(name)
        && (name.StartsWith('.') || ExcludedDirectoryNames.Contains(name));

    /// <summary>True when the file or folder has the Hidden or System attribute.</summary>
    public static bool IsHiddenOrSystem(FileSystemInfo info)
    {
        try
        {
            return (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable attributes: treat as excluded rather than risk reading a protected item.
            return true;
        }
    }
}
