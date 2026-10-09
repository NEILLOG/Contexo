using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Microsoft.Extensions.Logging;

namespace Contexo.Core.Indexing;

/// <summary>A file that is inside the scan range. <see cref="TooLarge"/> files are listed but never read.</summary>
internal sealed record ScannedFile(string Path, long SizeBytes, DateTimeOffset LastWriteUtc, bool TooLarge);

internal sealed class ScanResult
{
    /// <summary>False when the folder root does not exist or cannot be listed. Nothing may be deleted in that case.</summary>
    public bool RootAvailable { get; set; }

    public List<ScannedFile> Files { get; } = [];

    /// <summary>Cloud-only placeholders (OneDrive "files on demand"). They are not read, but they still exist.</summary>
    public HashSet<string> PlaceholderPaths { get; } = new(PathUtil.Comparer);

    /// <summary>Sub-folders that could not be listed. Documents below them are neither updated nor treated as missing.</summary>
    public List<string> InaccessibleDirectories { get; } = [];
}

/// <summary>What is in scope for one folder: extensions, size limit, and every exclusion (all paths are normalised full paths).</summary>
internal sealed class ScanRules
{
    private readonly List<string> _excludedDirectories;
    private readonly HashSet<string> _excludedFiles;

    private ScanRules(IReadOnlySet<string> extensions, long? maxFileBytes, List<string> excludedDirectories, HashSet<string> excludedFiles, List<string> nestedFolders)
    {
        Extensions = extensions;
        MaxFileBytes = maxFileBytes;
        _excludedDirectories = excludedDirectories;
        _excludedFiles = excludedFiles;
        NestedFolders = nestedFolders;
    }

    /// <summary>Lower-case extensions that are both enabled by the user and understood by a parser.</summary>
    public IReadOnlySet<string> Extensions { get; }

    public long? MaxFileBytes { get; }

    /// <summary>Other watched folders located inside this one. They are scanned by their own folder, not by this one.</summary>
    public IReadOnlyList<string> NestedFolders { get; }

    public static ScanRules Create(
        WatchedFolder folder,
        IReadOnlyList<WatchedFolder> allFolders,
        IReadOnlyList<Exclusion> exclusions,
        AppSettings settings,
        IParserRegistry registry)
    {
        var extensions = new HashSet<string>(PathUtil.Comparer);
        foreach (var extension in FileCategories.GetExtensions(settings.EnabledCategories))
        {
            if (registry.Resolve(extension) is not null)
            {
                extensions.Add(extension);
            }
        }

        var root = PathUtil.Normalize(folder.Path);
        var directories = new List<string>();
        foreach (var relative in folder.ExcludedSubfolders)
        {
            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            var combined = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar));
            directories.Add(PathUtil.Normalize(combined));
        }

        var files = new HashSet<string>(PathUtil.Comparer);
        foreach (var exclusion in exclusions)
        {
            var path = PathUtil.Normalize(exclusion.Path);
            if (exclusion.IsFolder)
            {
                directories.Add(path);
            }
            else
            {
                files.Add(path);
            }
        }

        var nested = new List<string>();
        foreach (var other in allFolders)
        {
            if (other.Id == folder.Id)
            {
                continue;
            }

            var otherPath = PathUtil.Normalize(other.Path);
            if (PathUtil.IsUnder(otherPath, root))
            {
                nested.Add(otherPath);
                directories.Add(otherPath);
            }
        }

        long? maxBytes = settings.MaxFileSizeMb is { } mb ? mb * 1024L * 1024L : null;
        return new ScanRules(extensions, maxBytes, directories, files, nested);
    }

    /// <summary>True when the directory is, or lies inside, an excluded folder.</summary>
    public bool IsDirectoryExcluded(string fullPath)
    {
        foreach (var excluded in _excludedDirectories)
        {
            if (PathUtil.IsUnderOrEqual(fullPath, excluded))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>User exclusions and excluded sub-folders only (built-in rules are applied while enumerating).</summary>
    public bool IsFileExcluded(string fullPath) => _excludedFiles.Contains(fullPath) || IsDirectoryExcluded(fullPath);
}

/// <summary>Lists the files of one watched folder that Contexo is allowed to read. Never writes anything.</summary>
internal sealed class FolderScanner
{
    /// <summary>OneDrive "files on demand" placeholders: RECALL_ON_DATA_ACCESS (0x400000), RECALL_ON_OPEN (0x40000) and Offline.</summary>
    internal const FileAttributes CloudOnly = (FileAttributes)0x400000 | (FileAttributes)0x40000 | FileAttributes.Offline;

    private static readonly EnumerationOptions Listing = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private readonly ILogger _logger;

    public FolderScanner(ILogger logger) => _logger = logger;

    /// <param name="scope">Null scans the whole folder. Otherwise only these files or directories (the folder root is always probed).</param>
    public ScanResult Scan(WatchedFolder folder, ScanRules rules, IReadOnlyList<string>? scope, CancellationToken cancellationToken)
    {
        var result = new ScanResult();
        var rootPath = PathUtil.Normalize(folder.Path);
        var root = new DirectoryInfo(rootPath);

        try
        {
            if (!root.Exists)
            {
                return result;
            }

            if (scope is null)
            {
                if (!ScanDirectory(root, rules, result, isRoot: true, cancellationToken))
                {
                    return result;
                }
            }
            else
            {
                // Make sure the root itself can be listed before trusting a partial result.
                using (var probe = root.EnumerateFileSystemInfos("*", Listing).GetEnumerator())
                {
                    probe.MoveNext();
                }

                foreach (var path in scope)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ScanScopePath(rootPath, path, rules, result, cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Folder {Folder} could not be listed: {Error}", folder.DisplayName, ex.GetType().Name);
            result.Files.Clear();
            result.PlaceholderPaths.Clear();
            result.InaccessibleDirectories.Clear();
            return result;
        }

        result.RootAvailable = true;
        return result;
    }

    private void ScanScopePath(string rootPath, string path, ScanRules rules, ScanResult result, CancellationToken cancellationToken)
    {
        if (!PathUtil.IsUnderOrEqual(path, rootPath))
        {
            return;
        }

        if (string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            ScanDirectory(new DirectoryInfo(rootPath), rules, result, isRoot: true, cancellationToken);
            return;
        }

        // Every ancestor between the root and the path must itself be in scope.
        var parent = Path.GetDirectoryName(path);
        var ancestors = new Stack<string>();
        while (parent is not null && PathUtil.IsUnder(parent, rootPath))
        {
            ancestors.Push(parent);
            parent = Path.GetDirectoryName(parent);
        }

        foreach (var ancestor in ancestors)
        {
            if (!IsDirectoryInScope(new DirectoryInfo(ancestor), rules))
            {
                return;
            }
        }

        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            if (IsDirectoryInScope(directory, rules))
            {
                ScanDirectory(directory, rules, result, isRoot: false, cancellationToken);
            }
        }
        else if (File.Exists(path))
        {
            AddFile(new FileInfo(path), rules, result);
        }
    }

    /// <returns>False only when the root itself could not be listed.</returns>
    private bool ScanDirectory(DirectoryInfo start, ScanRules rules, ScanResult result, bool isRoot, CancellationToken cancellationToken)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(start);
        var first = true;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            List<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos("*", Listing).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (first && isRoot)
                {
                    throw;
                }

                _logger.LogWarning("Sub-folder skipped ({Error})", ex.GetType().Name);
                result.InaccessibleDirectories.Add(directory.FullName);
                first = false;
                continue;
            }

            first = false;
            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo child)
                {
                    if (IsDirectoryInScope(child, rules))
                    {
                        pending.Push(child);
                    }
                }
                else if (entry is FileInfo file)
                {
                    AddFile(file, rules, result);
                }
            }
        }

        return true;
    }

    private static bool IsDirectoryInScope(DirectoryInfo directory, ScanRules rules)
    {
        if (FileCategories.IsBuiltInExcludedDirectory(directory.Name) || FileCategories.IsHiddenOrSystem(directory))
        {
            return false;
        }

        if (rules.IsDirectoryExcluded(directory.FullName))
        {
            return false;
        }

        try
        {
            // Never follow links: they can loop or leave the folder the user chose.
            return directory.LinkTarget is null;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void AddFile(FileInfo file, ScanRules rules, ScanResult result)
    {
        if (FileCategories.IsBuiltInExcludedFile(file.Name) || !rules.Extensions.Contains(file.Extension))
        {
            return;
        }

        var fullName = file.FullName;
        if (rules.IsFileExcluded(fullName))
        {
            return;
        }

        try
        {
            if (FileCategories.IsHiddenOrSystem(file))
            {
                return;
            }

            if ((file.Attributes & CloudOnly) != 0)
            {
                // Reading it would make OneDrive download it. Version 1 leaves such files alone.
                result.PlaceholderPaths.Add(fullName);
                return;
            }

            var length = file.Length;
            var lastWrite = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            var tooLarge = rules.MaxFileBytes is { } max && length > max;
            result.Files.Add(new ScannedFile(fullName, length, lastWrite, tooLarge));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Vanished or unreadable between listing and inspection; the next reconciliation decides.
        }
    }
}
