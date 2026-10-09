using Contexo.Core.Common;

namespace Contexo.App.Folders;

/// <summary>A common location offered in the first-run wizard.</summary>
public sealed record KnownFolder(string Key, string DisplayName, string Path, bool SelectedByDefault);

/// <summary>Finds the usual places people keep documents (Documents, Desktop, OneDrive, Downloads). Only existing folders are returned.</summary>
public interface IKnownFolders
{
    IReadOnlyList<KnownFolder> GetKnownFolders();
}

public readonly record struct FileCount(int Count, bool Truncated);

/// <summary>Counts the files Contexo would read in a folder.</summary>
public interface IFolderFileCounter
{
    /// <param name="extensions">Lower-case extensions including the dot.</param>
    /// <param name="limit">Counting stops once this many files were found.</param>
    /// <returns>The number of files, at most <paramref name="limit"/>; <c>Truncated</c> is true when the limit was reached.</returns>
    Task<FileCount> CountAsync(string path, IReadOnlySet<string> extensions, int limit, CancellationToken cancellationToken);
}

/// <summary>Lists sub folders for the "choose sub folders" tree and checks that a folder exists.</summary>
public interface IFolderTreeReader
{
    bool DirectoryExists(string path);

    /// <summary>Names of the visible child folders, sorted. Built-in excluded, hidden, system and linked folders are left out. Unreadable folders give an empty list.</summary>
    Task<IReadOnlyList<string>> GetChildFoldersAsync(string path, CancellationToken cancellationToken);
}

public sealed class SystemKnownFolders : IKnownFolders
{
    public IReadOnlyList<KnownFolder> GetKnownFolders()
    {
        var result = new List<KnownFolder>();

        void Add(string key, string name, string? path, bool selected)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            if (result.Any(r => PathRelations.AreSame(r.Path, path)))
            {
                return;
            }

            result.Add(new KnownFolder(key, name, path, selected));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add("documents", "文件", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), true);
        Add("desktop", "桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false);
        foreach (var variable in new[] { "OneDrive", "OneDriveCommercial" })
        {
            var path = Environment.GetEnvironmentVariable(variable);
            Add(variable.ToLowerInvariant(), string.IsNullOrWhiteSpace(path) ? "OneDrive" : PathRelations.GetName(path), path, true);
        }

        Add("downloads", "下載", string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Downloads"), false);
        return result;
    }
}

public sealed class FileSystemFolderFileCounter : IFolderFileCounter
{
    public Task<FileCount> CountAsync(string path, IReadOnlySet<string> extensions, int limit, CancellationToken cancellationToken) =>
        Task.Run(() => Count(path, extensions, limit, cancellationToken), cancellationToken);

    private static FileCount Count(string root, IReadOnlySet<string> extensions, int limit, CancellationToken cancellationToken)
    {
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                var info = new DirectoryInfo(directory);
                foreach (var entry in info.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.LinkTarget is not null || FileCategories.IsHiddenOrSystem(entry))
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo)
                    {
                        if (!FileCategories.IsBuiltInExcludedDirectory(entry.Name))
                        {
                            pending.Push(entry.FullName);
                        }
                    }
                    else if (!FileCategories.IsBuiltInExcludedFile(entry.Name) && extensions.Contains(entry.Extension))
                    {
                        count++;
                        if (count >= limit)
                        {
                            return new FileCount(limit, true);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be read is simply not counted.
            }
        }

        return new FileCount(count, false);
    }
}

public sealed class FileSystemFolderTreeReader : IFolderTreeReader
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public Task<IReadOnlyList<string>> GetChildFoldersAsync(string path, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var names = new List<string>();
            try
            {
                foreach (var directory in new DirectoryInfo(path).EnumerateDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (directory.LinkTarget is not null
                        || FileCategories.IsBuiltInExcludedDirectory(directory.Name)
                        || FileCategories.IsHiddenOrSystem(directory))
                    {
                        continue;
                    }

                    names.Add(directory.Name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable: show it without children.
            }

            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            return names;
        }, cancellationToken);
}
