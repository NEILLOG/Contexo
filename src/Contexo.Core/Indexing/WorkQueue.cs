namespace Contexo.Core.Indexing;

/// <summary>One file waiting to be read.</summary>
/// <param name="IsNew">The database had no row for this path when it was queued.</param>
internal sealed record WorkItem(long FolderId, string Path, long SizeBytes, DateTimeOffset LastWriteUtc, bool TooLarge, bool IsNew = false);

/// <summary>
/// Files waiting to be processed. Priority files (retries and file system events) come first, newest first;
/// everything else is ordered by modification time, newest first. A path is queued at most once.
/// </summary>
internal sealed class WorkQueue
{
    private sealed class Entry(WorkItem item, bool priority)
    {
        public WorkItem Item { get; set; } = item;
        public bool Priority { get; set; } = priority;
        public LinkedListNode<WorkItem>? Node { get; set; }
    }

    private sealed class ByRecency : IComparer<WorkItem>
    {
        public int Compare(WorkItem? x, WorkItem? y)
        {
            var byTime = y!.LastWriteUtc.CompareTo(x!.LastWriteUtc);
            return byTime != 0 ? byTime : StringComparer.OrdinalIgnoreCase.Compare(x.Path, y.Path);
        }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<WorkItem> _priority = new();
    private readonly SortedSet<WorkItem> _normal = new(new ByRecency());

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Queued files of a folder: all of them, and those that are not in the database yet.</summary>
    public (int All, int New) CountForFolder(long folderId)
    {
        lock (_gate)
        {
            var all = 0;
            var added = 0;
            foreach (var entry in _entries.Values)
            {
                if (entry.Item.FolderId != folderId)
                {
                    continue;
                }

                all++;
                if (entry.Item.IsNew)
                {
                    added++;
                }
            }

            return (all, added);
        }
    }

    /// <summary>Adds the file, or replaces the queued entry of the same path.</summary>
    /// <returns>True when the path was not queued before.</returns>
    public bool Enqueue(WorkItem item, bool priority)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(item.Path, out var existing))
            {
                Remove(existing);
                existing.Item = item;
                existing.Priority = existing.Priority || priority;
                Insert(existing);
                return false;
            }

            var entry = new Entry(item, priority);
            _entries[item.Path] = entry;
            Insert(entry);
            return true;
        }
    }

    public bool TryDequeue(out WorkItem item)
    {
        lock (_gate)
        {
            if (_priority.First is { } node)
            {
                _priority.RemoveFirst();
                item = node.Value;
                _entries.Remove(item.Path);
                return true;
            }

            if (_normal.Count > 0)
            {
                item = _normal.Min!;
                _normal.Remove(item);
                _entries.Remove(item.Path);
                return true;
            }
        }

        item = null!;
        return false;
    }

    /// <summary>Drops everything queued for a folder (it was removed). Returns how many entries were dropped.</summary>
    public int RemoveFolder(long folderId)
    {
        lock (_gate)
        {
            var doomed = _entries.Values.Where(e => e.Item.FolderId == folderId).ToList();
            foreach (var entry in doomed)
            {
                Remove(entry);
                _entries.Remove(entry.Item.Path);
            }

            return doomed.Count;
        }
    }

    private void Insert(Entry entry)
    {
        if (entry.Priority)
        {
            entry.Node = _priority.AddFirst(entry.Item);
        }
        else
        {
            _normal.Add(entry.Item);
        }
    }

    private void Remove(Entry entry)
    {
        if (entry.Priority)
        {
            if (entry.Node is not null)
            {
                _priority.Remove(entry.Node);
                entry.Node = null;
            }
        }
        else
        {
            _normal.Remove(entry.Item);
        }
    }
}
