using System.Security.Cryptography;
using Contexo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Contexo.Core.Indexing;

internal enum ProcessOutcome
{
    /// <summary>A document that was not in the database before was read.</summary>
    Added,
    /// <summary>An existing document was read again because its content changed.</summary>
    Updated,
    /// <summary>The content hash was identical; only the fingerprint was refreshed.</summary>
    Unchanged,
    /// <summary>Recorded as failed (corrupted, locked, timed out, ...).</summary>
    Failed,
    /// <summary>Recorded as skipped (too large, unsupported, scanned PDF).</summary>
    Skipped,
    /// <summary>The file disappeared while it was being processed; the next reconciliation deals with it.</summary>
    Vanished,
}

/// <summary>Reads one file: hash, parse (with embedded files), chunk, embed, and write everything in a single transaction.</summary>
internal sealed class DocumentProcessor
{
    private const string ScannedPdfWarning = "scanned-pdf";

    private readonly IKnowledgeStore _store;
    private readonly IParserRegistry _registry;
    private readonly IChunker _chunker;
    private readonly IEmbeddingService _embedding;
    private readonly ParserOptions _parserOptions;
    private readonly ChunkingOptions _chunkingOptions;
    private readonly IndexingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public DocumentProcessor(
        IKnowledgeStore store,
        IParserRegistry registry,
        IChunker chunker,
        IEmbeddingService embedding,
        ParserOptions parserOptions,
        ChunkingOptions chunkingOptions,
        IndexingOptions options,
        TimeProvider time,
        ILogger logger)
    {
        _store = store;
        _registry = registry;
        _chunker = chunker;
        _embedding = embedding;
        _parserOptions = parserOptions;
        _chunkingOptions = chunkingOptions;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<ProcessOutcome> ProcessAsync(WorkItem item, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(item.Path);
        var scanned = new FileFingerprint(item.SizeBytes, item.LastWriteUtc, string.Empty);
        var existing = await _store.GetDocumentByPathAsync(item.Path, cancellationToken).ConfigureAwait(false);

        if (item.TooLarge)
        {
            await MarkAsync(item, scanned, DocumentStatus.Skipped, DocumentErrorCode.TooLarge, null, null, keep: false, cancellationToken).ConfigureAwait(false);
            return ProcessOutcome.Skipped;
        }

        // 1. Read the whole file into memory without blocking anyone else who has it open.
        MemoryStream content;
        FileFingerprint fingerprint;
        try
        {
            var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(item.Path), TimeSpan.Zero);
            await using var file = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            if (file.Length > int.MaxValue)
            {
                await MarkAsync(item, scanned, DocumentStatus.Skipped, DocumentErrorCode.TooLarge, null, null, keep: false, cancellationToken).ConfigureAwait(false);
                return ProcessOutcome.Skipped;
            }

            content = new MemoryStream((int)file.Length);
            await file.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
            content.Position = 0;
            fingerprint = new FileFingerprint(content.Length, lastWrite, HashOf(content));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ProcessOutcome.Vanished;
        }
        catch (UnauthorizedAccessException)
        {
            await MarkAsync(item, scanned, DocumentStatus.Failed, DocumentErrorCode.AccessDenied, "access-denied", null, keep: false, cancellationToken).ConfigureAwait(false);
            return ProcessOutcome.Failed;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            await MarkLockedAsync(item, existing, cancellationToken).ConfigureAwait(false);
            return ProcessOutcome.Failed;
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Could not read {File}: {Error}", fileName, ex.GetType().Name);
            await MarkAsync(item, scanned, DocumentStatus.Failed, DocumentErrorCode.Unknown, "io-error", null, keep: false, cancellationToken).ConfigureAwait(false);
            return ProcessOutcome.Failed;
        }

        await using (content.ConfigureAwait(false))
        {
            // 2. Same content as before: only refresh the fingerprint (touched but not edited).
            if (existing is { Status: DocumentStatus.Indexed } && string.Equals(existing.Fingerprint.ContentHash, fingerprint.ContentHash, StringComparison.Ordinal))
            {
                await MarkAsync(item, fingerprint, DocumentStatus.Indexed, DocumentErrorCode.None, null, null, keep: true, cancellationToken).ConfigureAwait(false);
                return ProcessOutcome.Unchanged;
            }

            var outcome = existing is null ? ProcessOutcome.Added : ProcessOutcome.Updated;
            try
            {
                return await ParseAndStoreAsync(item, fileName, content, fingerprint, outcome, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DocumentParseException ex)
            {
                var retry = ex.Code == DocumentErrorCode.Locked ? _time.GetUtcNow() + _options.LockedRetryDelay : (DateTimeOffset?)null;
                await MarkAsync(item, fingerprint, DocumentStatus.Failed, ex.Code, ex.Message, retry, keep: false, cancellationToken).ConfigureAwait(false);
                return ProcessOutcome.Failed;
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                await MarkLockedAsync(item, existing, cancellationToken).ConfigureAwait(false);
                return ProcessOutcome.Failed;
            }
            catch (UnauthorizedAccessException)
            {
                await MarkAsync(item, fingerprint, DocumentStatus.Failed, DocumentErrorCode.AccessDenied, "access-denied", null, keep: false, cancellationToken).ConfigureAwait(false);
                return ProcessOutcome.Failed;
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Parsing {File} timed out", fileName);
                await MarkAsync(item, fingerprint, DocumentStatus.Failed, DocumentErrorCode.Timeout, "timeout", null, keep: false, cancellationToken).ConfigureAwait(false);
                return ProcessOutcome.Failed;
            }
            catch (NotImplementedException)
            {
                await MarkAsync(item, fingerprint, DocumentStatus.Skipped, DocumentErrorCode.Unsupported, null, null, keep: false, cancellationToken).ConfigureAwait(false);
                return ProcessOutcome.Skipped;
            }
            catch (Exception ex)
            {
                // File names and exception types only: never document content.
                _logger.LogError("Processing {File} failed: {Error}", fileName, ex.GetType().Name);
                await MarkAsync(item, fingerprint, DocumentStatus.Failed, DocumentErrorCode.Unknown, ex.GetType().Name, null, keep: false, cancellationToken).ConfigureAwait(false);
                return ProcessOutcome.Failed;
            }
        }
    }

    private async Task<ProcessOutcome> ParseAndStoreAsync(
        WorkItem item,
        string fileName,
        MemoryStream content,
        FileFingerprint fingerprint,
        ProcessOutcome outcome,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName);
        var parser = _registry.Resolve(extension);
        if (parser is null)
        {
            await MarkAsync(item, fingerprint, DocumentStatus.Skipped, DocumentErrorCode.Unsupported, null, null, keep: false, cancellationToken).ConfigureAwait(false);
            return ProcessOutcome.Skipped;
        }

        // 3-4. Parse the file and, recursively, everything embedded in it, all within one time budget.
        var gathered = new Gathered();
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_options.ParseTimeout);
            try
            {
                await GatherAsync(parser, fileName, content, null, 0, gathered, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                throw new TimeoutException();
            }
        }

        if (gathered.Sections.Count == 0 && gathered.Warnings.Contains(ScannedPdfWarning))
        {
            await MarkAsync(
                item,
                fingerprint,
                DocumentStatus.Skipped,
                DocumentErrorCode.Unsupported,
                "這份 PDF 是掃描檔，這個版本還讀不到文字。",
                null,
                keep: false,
                cancellationToken).ConfigureAwait(false);
            return ProcessOutcome.Skipped;
        }

        // 5. Chunk. The title is the file name without its extension.
        var chunks = _chunker.Split(Path.GetFileNameWithoutExtension(fileName), gathered.Sections, _chunkingOptions);

        // 6. Vectors, when the model is there. Otherwise they are filled in later in the background.
        var vectors = await TryEmbedAsync(chunks, cancellationToken).ConfigureAwait(false);
        var writes = new List<ChunkWrite>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            writes.Add(new ChunkWrite(chunks[i], vectors?[i]));
        }

        var modelId = vectors is null ? null : _embedding.ModelId;
        if (vectors is not null && string.IsNullOrEmpty(modelId))
        {
            writes = chunks.Select(c => new ChunkWrite(c, null)).ToList();
            modelId = null;
        }

        // 7. One transaction replaces the old data, so searches never see a mix.
        await _store.ReplaceDocumentAsync(new DocumentWrite(item.FolderId, item.Path, fingerprint, modelId, writes, gathered.Tables), cancellationToken).ConfigureAwait(false);
        if (gathered.Warnings.Count > 0)
        {
            _logger.LogInformation("{File} was read with {Count} warnings", fileName, gathered.Warnings.Count);
        }

        return outcome;
    }

    private async Task<List<float[]>?> TryEmbedAsync(IReadOnlyList<Chunk> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0 || !_embedding.IsAvailable)
        {
            return null;
        }

        try
        {
            var result = new List<float[]>(chunks.Count);
            for (var start = 0; start < chunks.Count; start += _options.EmbeddingBatchSize)
            {
                var texts = chunks.Skip(start).Take(_options.EmbeddingBatchSize).Select(c => c.EmbeddingText).ToList();
                var batch = await _embedding.EmbedDocumentsAsync(texts, cancellationToken).ConfigureAwait(false);
                if (batch.Count != texts.Count)
                {
                    return null;
                }

                result.AddRange(batch);
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The text is still searchable by keyword; the vectors are retried in the background.
            _logger.LogWarning("Embedding failed ({Error}); vectors will be filled in later", ex.GetType().Name);
            return null;
        }
    }

    private async Task GatherAsync(
        IDocumentParser parser,
        string fileName,
        MemoryStream content,
        IReadOnlyList<string>? embeddedPath,
        int depth,
        Gathered gathered,
        CancellationToken token)
    {
        content.Position = 0;
        var context = new ParseContext(content, fileName, _parserOptions, embeddedPath);

        // Task.Run so that a parser that works synchronously before its first await cannot get past the timeout.
        var parsed = await Task.Run(() => parser.ParseAsync(context, token), token).WaitAsync(token).ConfigureAwait(false);

        gathered.Warnings.AddRange(parsed.Warnings);
        foreach (var section in parsed.Sections)
        {
            gathered.Sections.Add(embeddedPath is null ? section : Prefix(section, embeddedPath));
        }

        foreach (var table in parsed.Tables)
        {
            gathered.Tables.Add(embeddedPath is null ? table : table with { TableKey = PrefixKey(embeddedPath, table.TableKey) });
        }

        if (depth >= _options.MaxEmbeddedDepth)
        {
            return;
        }

        foreach (var embedded in parsed.EmbeddedFiles)
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(embedded.FileName);
            var nestedParser = string.IsNullOrEmpty(name) ? null : _registry.Resolve(Path.GetExtension(name));
            if (nestedParser is null || embedded.Content.Length == 0)
            {
                continue;
            }

            if (gathered.EmbeddedBytes + embedded.Content.Length > _options.MaxEmbeddedBytes)
            {
                _logger.LogWarning("Embedded files of {File} exceed the size budget; the rest is ignored", Path.GetFileName(fileName));
                continue;
            }

            gathered.EmbeddedBytes += embedded.Content.Length;
            var nestedPath = embeddedPath is null ? [name] : embeddedPath.Append(name).ToArray();
            try
            {
                await using var nestedContent = new MemoryStream(embedded.Content, writable: false);
                await GatherAsync(nestedParser, name, nestedContent, nestedPath, depth + 1, gathered, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is DocumentParseException or NotImplementedException or InvalidDataException or IOException)
            {
                // A broken attachment must not fail the file that contains it.
                _logger.LogWarning("Embedded file {Embedded} in {File} was skipped ({Error})", name, Path.GetFileName(fileName), ex.GetType().Name);
            }
        }
    }

    private static DocumentSection Prefix(DocumentSection section, IReadOnlyList<string> embeddedPath)
    {
        var location = section.Location.EmbeddedPath is { Count: > 0 } ? section.Location : section.Location with { EmbeddedPath = embeddedPath };
        var key = section.TableKey is null ? null : PrefixKey(embeddedPath, section.TableKey);
        return section with { Location = location, TableKey = key };
    }

    private static string PrefixKey(IReadOnlyList<string> embeddedPath, string key) => string.Join(" › ", embeddedPath) + "#" + key;

    private static string HashOf(MemoryStream stream)
    {
        if (!stream.TryGetBuffer(out var buffer))
        {
            return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan()));
    }

    /// <summary>
    /// Somebody else has the file open exclusively. Windows: ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33).
    /// macOS / Linux (development only): .NET emulates FileShare with advisory locks and reports the errno EWOULDBLOCK (35 / 11).
    /// </summary>
    internal static bool IsSharingViolation(IOException exception) =>
        OperatingSystem.IsWindows() ? (exception.HResult & 0xFFFF) is 32 or 33 : exception.HResult is 35 or 11;

    private Task MarkLockedAsync(WorkItem item, DocumentRecord? existing, CancellationToken cancellationToken)
    {
        // Keep the old fingerprint (and old chunks) so that the next scan still sees the file as changed.
        var fingerprint = existing?.Fingerprint ?? new FileFingerprint(item.SizeBytes, item.LastWriteUtc, string.Empty);
        return MarkAsync(
            item,
            fingerprint,
            DocumentStatus.Failed,
            DocumentErrorCode.Locked,
            "locked",
            _time.GetUtcNow() + _options.LockedRetryDelay,
            keep: true,
            cancellationToken);
    }

    private Task MarkAsync(
        WorkItem item,
        FileFingerprint fingerprint,
        DocumentStatus status,
        DocumentErrorCode code,
        string? message,
        DateTimeOffset? nextRetryAt,
        bool keep,
        CancellationToken cancellationToken) =>
        _store.MarkDocumentAsync(item.FolderId, item.Path, fingerprint, status, code, message, nextRetryAt, keep, cancellationToken);

    private sealed class Gathered
    {
        public List<DocumentSection> Sections { get; } = [];
        public List<SpreadsheetTable> Tables { get; } = [];
        public List<string> Warnings { get; } = [];
        public long EmbeddedBytes { get; set; }
    }
}
