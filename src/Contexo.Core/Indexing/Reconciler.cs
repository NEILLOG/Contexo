using System.Security.Cryptography;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Contexo.Core.Indexing;

/// <param name="Unavailable">The folder could not be listed. Nothing was changed except the folder state.</param>
/// <param name="Items">Files that must be (re)read.</param>
internal sealed record ReconcileResult(bool Unavailable, IReadOnlyList<WorkItem> Items);

/// <summary>
/// Compares what is on disk with what the database knows and repairs the database: moves, deletions, and the list of files to read.
/// The only things it ever deletes are rows in Contexo's own database. User files are only ever read (to hash them).
/// </summary>
internal sealed class Reconciler
{
    private sealed record PendingDeletion(HashSet<long> DocumentIds);

    private sealed record Dismissed(HashSet<long> DocumentIds, DateTimeOffset Until);

    private readonly IKnowledgeStore _store;
    private readonly IParserRegistry _registry;
    private readonly ISettingsStore _settings;
    private readonly FolderScanner _scanner;
    private readonly IndexingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, PendingDeletion> _pending = [];
    private readonly Dictionary<long, Dismissed> _dismissed = [];

    public Reconciler(
        IKnowledgeStore store,
        IParserRegistry registry,
        ISettingsStore settings,
        FolderScanner scanner,
        IndexingOptions options,
        TimeProvider time,
        ILogger logger)
    {
        _store = store;
        _registry = registry;
        _settings = settings;
        _scanner = scanner;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Reports a number of changed files (updates are reported by the document processor, not here).</summary>
    public Action<ActivityKind, int>? ActivityReported { get; set; }

    public Action<MassDeletionPending>? MassDeletionRaised { get; set; }

    /// <param name="scope">Null reconciles the whole folder; otherwise only these files or directories.</param>
    public async Task<ReconcileResult> ReconcileAsync(
        WatchedFolder folder,
        IReadOnlyList<WatchedFolder> allFolders,
        IReadOnlyList<string>? scope,
        CancellationToken cancellationToken)
    {
        var rootPath = PathUtil.Normalize(folder.Path);
        if (scope is not null)
        {
            // A path outside the folder (for example a symlinked temp directory reported by the watcher) cannot be mapped: do everything.
            if (scope.Count == 0 || scope.Count > _options.MaxPartialPaths || scope.Any(p => !PathUtil.IsUnder(p, rootPath)))
            {
                scope = null;
            }
        }

        var now = _time.GetUtcNow();
        var settings = _settings.Current;
        var exclusions = await _store.GetExclusionsAsync(cancellationToken).ConfigureAwait(false);
        var rules = ScanRules.Create(folder, allFolders, exclusions, settings, _registry);
        var scan = await Task.Run(() => _scanner.Scan(folder, rules, scope, cancellationToken), cancellationToken).ConfigureAwait(false);

        if (!scan.RootAvailable)
        {
            _logger.LogWarning("Folder {Folder} is unavailable; keeping its data", folder.DisplayName);
            if (folder.State != FolderState.Unavailable)
            {
                await _store.SetFolderStateAsync(folder.Id, FolderState.Unavailable, null, cancellationToken).ConfigureAwait(false);
            }

            return new ReconcileResult(true, []);
        }

        var allDocuments = await _store.GetDocumentsAsync(folder.Id, cancellationToken).ConfigureAwait(false);
        var documents = scope is null ? allDocuments : allDocuments.Where(d => IsInScope(d.Path, scope)).ToList();
        var documentsByPath = new Dictionary<string, DocumentRecord>(PathUtil.Comparer);
        foreach (var document in documents)
        {
            documentsByPath[document.Path] = document;
        }

        var scannedPaths = new HashSet<string>(PathUtil.Comparer);
        var items = new List<WorkItem>();
        var newFiles = new List<ScannedFile>();
        foreach (var file in scan.Files)
        {
            if (!scannedPaths.Add(file.Path))
            {
                continue;
            }

            if (documentsByPath.TryGetValue(file.Path, out var document))
            {
                if (NeedsWork(document, file, now))
                {
                    items.Add(ToItem(folder.Id, file));
                }
            }
            else
            {
                newFiles.Add(file);
            }
        }

        var missing = new List<DocumentRecord>();
        var outOfScope = new List<DocumentRecord>();
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scannedPaths.Contains(document.Path) || scan.PlaceholderPaths.Contains(document.Path))
            {
                continue;
            }

            if (scan.InaccessibleDirectories.Any(d => PathUtil.IsUnder(document.Path, d)))
            {
                continue;
            }

            if (File.Exists(document.Path))
            {
                // Still on disk but no longer wanted: category switched off, newly excluded, hidden, or now owned by a nested folder.
                outOfScope.Add(document);
            }
            else
            {
                missing.Add(document);
            }
        }

        // Rename / move: a "new" file that has the same content as a vanished one is the same document.
        if (missing.Count > 0 && newFiles.Count > 0)
        {
            await DetectMovesAsync(folder, newFiles, missing, cancellationToken).ConfigureAwait(false);
        }

        foreach (var file in newFiles)
        {
            items.Add(ToItem(folder.Id, file) with { IsNew = true });
        }

        var removed = 0;
        foreach (var document in outOfScope)
        {
            await _store.DeleteDocumentAsync(document.Id, cancellationToken).ConfigureAwait(false);
            removed++;
        }

        if (removed > 0)
        {
            ActivityReported?.Invoke(ActivityKind.Removed, removed);
        }

        var raise = await HandleMissingAsync(folder, allDocuments.Count, missing, cancellationToken).ConfigureAwait(false);

        var newState = folder.State;
        bool hasPending;
        lock (_gate)
        {
            hasPending = _pending.ContainsKey(folder.Id);
        }

        if (raise is not null)
        {
            newState = FolderState.AwaitingDeletionConfirmation;
        }
        else if (folder.State == FolderState.Unavailable || (folder.State == FolderState.AwaitingDeletionConfirmation && !hasPending))
        {
            newState = FolderState.Active;
        }

        if (newState != folder.State || scope is null)
        {
            await _store.SetFolderStateAsync(folder.Id, newState, scope is null ? now : null, cancellationToken).ConfigureAwait(false);
        }

        if (raise is not null)
        {
            RaiseMassDeletion(raise);
        }

        return new ReconcileResult(false, items);
    }

    /// <summary>Answers a mass-deletion question. Returns true when there was nothing pending and the caller should rescan.</summary>
    public async Task<bool> ResolveMassDeletionAsync(long folderId, bool deleteMissing, CancellationToken cancellationToken)
    {
        PendingDeletion? pending;
        lock (_gate)
        {
            _pending.Remove(folderId, out pending);
        }

        var folder = (await _store.GetFoldersAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(f => f.Id == folderId);
        if (folder is null)
        {
            return false;
        }

        if (pending is null)
        {
            // Nothing in memory (for example after a restart): go back to Active and let a fresh scan ask again if needed.
            if (folder.State == FolderState.AwaitingDeletionConfirmation)
            {
                await _store.SetFolderStateAsync(folderId, FolderState.Active, null, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        if (!deleteMissing)
        {
            lock (_gate)
            {
                _dismissed[folderId] = new Dismissed(pending.DocumentIds, _time.GetUtcNow() + _options.MassDeletionSuppression);
            }

            await _store.SetFolderStateAsync(folderId, FolderState.Active, null, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!Directory.Exists(folder.Path))
        {
            await _store.SetFolderStateAsync(folderId, FolderState.Unavailable, null, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var removed = 0;
        foreach (var document in await _store.GetDocumentsAsync(folderId, cancellationToken).ConfigureAwait(false))
        {
            if (!pending.DocumentIds.Contains(document.Id))
            {
                continue;
            }

            // The file may have come back while the question was open.
            if (!File.Exists(document.Path))
            {
                await _store.DeleteDocumentAsync(document.Id, cancellationToken).ConfigureAwait(false);
                removed++;
            }
        }

        if (removed > 0)
        {
            ActivityReported?.Invoke(ActivityKind.Removed, removed);
        }

        await _store.SetFolderStateAsync(folderId, FolderState.Active, null, cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>Forgets in-memory questions about a folder (it was removed).</summary>
    public void Forget(long folderId)
    {
        lock (_gate)
        {
            _pending.Remove(folderId);
            _dismissed.Remove(folderId);
        }
    }

    private async Task<MassDeletionPending?> HandleMissingAsync(WatchedFolder folder, int totalDocuments, List<DocumentRecord> missing, CancellationToken cancellationToken)
    {
        if (missing.Count == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_pending.ContainsKey(folder.Id))
            {
                // Already waiting for the user's answer; delete nothing meanwhile.
                return null;
            }
        }

        var massive = missing.Count >= _options.MassDeletionMinCount && missing.Count > totalDocuments * _options.MassDeletionRatio;
        if (!massive)
        {
            foreach (var document in missing)
            {
                await _store.DeleteDocumentAsync(document.Id, cancellationToken).ConfigureAwait(false);
            }

            ActivityReported?.Invoke(ActivityKind.Removed, missing.Count);
            return null;
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_dismissed.TryGetValue(folder.Id, out var dismissed))
            {
                if (now < dismissed.Until && missing.All(d => dismissed.DocumentIds.Contains(d.Id)))
                {
                    // The user already said "keep" for this same batch.
                    return null;
                }

                _dismissed.Remove(folder.Id);
            }

            _pending[folder.Id] = new PendingDeletion(missing.Select(d => d.Id).ToHashSet());
        }

        _logger.LogWarning("{Missing} of {Total} documents vanished from folder {Folder}; asking before deleting", missing.Count, totalDocuments, folder.DisplayName);
        return new MassDeletionPending(folder.Id, missing.Count, totalDocuments);
    }

    private void RaiseMassDeletion(MassDeletionPending pending)
    {
        try
        {
            MassDeletionRaised?.Invoke(pending);
        }
        catch (Exception ex)
        {
            _logger.LogError("A mass-deletion handler failed: {Error}", ex.GetType().Name);
        }
    }

    private async Task DetectMovesAsync(WatchedFolder folder, List<ScannedFile> newFiles, List<DocumentRecord> missing, CancellationToken cancellationToken)
    {
        var candidates = missing
            .Where(d => d.Status == DocumentStatus.Indexed && d.Fingerprint.ContentHash.Length > 0)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var moved = 0;
        foreach (var file in newFiles.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.TooLarge)
            {
                continue;
            }

            var sameSize = candidates.Where(d => d.Fingerprint.SizeBytes == file.SizeBytes).ToList();
            if (sameSize.Count == 0)
            {
                continue;
            }

            string hash;
            try
            {
                hash = await FileHasher.HashFileAsync(file.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var match = sameSize.FirstOrDefault(d => string.Equals(d.Fingerprint.ContentHash, hash, StringComparison.Ordinal));
            if (match is null)
            {
                continue;
            }

            await _store.MoveDocumentAsync(match.Id, file.Path, cancellationToken).ConfigureAwait(false);
            if (match.Fingerprint.LastWriteUtc != file.LastWriteUtc)
            {
                await _store.MarkDocumentAsync(
                    folder.Id,
                    file.Path,
                    new FileFingerprint(file.SizeBytes, file.LastWriteUtc, hash),
                    DocumentStatus.Indexed,
                    DocumentErrorCode.None,
                    null,
                    null,
                    keepExistingChunks: true,
                    cancellationToken).ConfigureAwait(false);
            }

            candidates.Remove(match);
            missing.Remove(match);
            newFiles.Remove(file);
            moved++;
        }

        if (moved > 0)
        {
            ActivityReported?.Invoke(ActivityKind.Moved, moved);
        }
    }

    private static bool NeedsWork(DocumentRecord document, ScannedFile file, DateTimeOffset now)
    {
        if (document.Status == DocumentStatus.Failed && document.ErrorCode == DocumentErrorCode.Locked)
        {
            // Retry only when the waiting time is over; the stored fingerprint is deliberately stale.
            return document.NextRetryAt is null || document.NextRetryAt <= now;
        }

        if (document.Fingerprint.SizeBytes != file.SizeBytes || document.Fingerprint.LastWriteUtc != file.LastWriteUtc)
        {
            return true;
        }

        var wasTooLarge = document.Status == DocumentStatus.Skipped && document.ErrorCode == DocumentErrorCode.TooLarge;
        return wasTooLarge != file.TooLarge;
    }

    private static bool IsInScope(string path, IReadOnlyList<string> scope)
    {
        foreach (var entry in scope)
        {
            if (PathUtil.IsUnderOrEqual(path, entry))
            {
                return true;
            }
        }

        return false;
    }

    private static WorkItem ToItem(long folderId, ScannedFile file) => new(folderId, file.Path, file.SizeBytes, file.LastWriteUtc, file.TooLarge);
}

internal static class FileHasher
{
    /// <summary>Lower-case hex SHA-256 of a file, opened read-only and shared with whoever else has it open.</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
