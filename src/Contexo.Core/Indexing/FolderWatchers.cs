using Contexo.Core.Abstractions;
using Contexo.Core.Common;
using Microsoft.Extensions.Logging;

namespace Contexo.Core.Indexing;

/// <summary>
/// One <see cref="FileSystemWatcher"/> per reachable folder. Events are only hints: they are collected, debounced,
/// and handed over as "please reconcile these paths". They never delete anything by themselves.
/// </summary>
internal sealed class FolderWatchers : IDisposable
{
    private const int MaxCollectedPaths = 500;

    private sealed class Watcher : IDisposable
    {
        private readonly FolderWatchers _owner;
        private readonly Lock _gate = new();
        private readonly HashSet<string> _paths = new(PathUtil.Comparer);
        private readonly FileSystemWatcher _watcher;
        private readonly ITimer _timer;
        private bool _everything;
        private bool _disposed;

        public Watcher(FolderWatchers owner, long folderId, string path)
        {
            _owner = owner;
            FolderId = folderId;
            Path = path;
            _timer = owner._time.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Created += (_, e) => Add(e.FullPath);
            _watcher.Deleted += (_, e) => Add(e.FullPath);
            _watcher.Changed += (_, e) =>
            {
                // A directory's own timestamp changes whenever something inside it does; the children report themselves.
                if (!Directory.Exists(e.FullPath))
                {
                    Add(e.FullPath);
                }
            };
            _watcher.Renamed += (_, e) =>
            {
                Add(e.OldFullPath);
                Add(e.FullPath);
            };
            _watcher.Error += (_, e) =>
            {
                // Buffer overflow or lost handle: events were dropped, so only a full reconciliation is safe.
                _owner._logger.LogWarning("File watcher error ({Error}); reconciling the whole folder", e.GetException().GetType().Name);
                MarkEverything();
            };
            _watcher.EnableRaisingEvents = true;
        }

        public long FolderId { get; }

        public string Path { get; }

        private void Add(string fullPath)
        {
            if (IsNoise(fullPath))
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (!_everything)
                {
                    _paths.Add(fullPath);
                    if (_paths.Count > MaxCollectedPaths)
                    {
                        _everything = true;
                        _paths.Clear();
                    }
                }

                _timer.Change(_owner._debounce, Timeout.InfiniteTimeSpan);
            }
        }

        private void MarkEverything()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _everything = true;
                _paths.Clear();
                _timer.Change(_owner._debounce, Timeout.InfiniteTimeSpan);
            }
        }

        private bool IsNoise(string fullPath)
        {
            string relative;
            try
            {
                relative = System.IO.Path.GetRelativePath(Path, fullPath);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (relative.StartsWith("..", StringComparison.Ordinal))
            {
                return false;
            }

            var segments = relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (FileCategories.IsBuiltInExcludedDirectory(segments[i]))
                {
                    return true;
                }
            }

            return FileCategories.IsBuiltInExcludedFile(segments[^1]);
        }

        private void Flush()
        {
            string[]? paths;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                paths = _everything ? null : _paths.ToArray();
                _everything = false;
                _paths.Clear();
            }

            if (paths is { Length: 0 })
            {
                return;
            }

            try
            {
                _owner._onChange(FolderId, paths);
            }
            catch (Exception ex)
            {
                _owner._logger.LogError("Watcher notification failed: {Error}", ex.GetType().Name);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
            }

            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _timer.Dispose();
        }
    }

    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly Action<long, IReadOnlyList<string>?> _onChange;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, Watcher> _watchers = [];
    private bool _disposed;

    /// <param name="onChange">Folder id and the changed paths; null paths mean "reconcile the whole folder".</param>
    public FolderWatchers(TimeProvider time, TimeSpan debounce, Action<long, IReadOnlyList<string>?> onChange, ILogger logger)
    {
        _time = time;
        _debounce = debounce;
        _onChange = onChange;
        _logger = logger;
    }

    /// <summary>Makes the set of watchers match the folders: reachable folders get one, everything else loses it.</summary>
    public void Sync(IReadOnlyList<WatchedFolder> folders)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var wanted = new Dictionary<long, string>();
            foreach (var folder in folders)
            {
                if (folder.State != FolderState.Unavailable && Directory.Exists(folder.Path))
                {
                    wanted[folder.Id] = folder.Path;
                }
            }

            foreach (var (id, watcher) in _watchers.ToList())
            {
                if (!wanted.TryGetValue(id, out var path) || !string.Equals(path, watcher.Path, StringComparison.OrdinalIgnoreCase))
                {
                    watcher.Dispose();
                    _watchers.Remove(id);
                }
            }

            foreach (var (id, path) in wanted)
            {
                if (_watchers.ContainsKey(id))
                {
                    continue;
                }

                try
                {
                    _watchers[id] = new Watcher(this, id, path);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    _logger.LogWarning("Could not watch a folder ({Error}); periodic scans still cover it", ex.GetType().Name);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }
    }
}
