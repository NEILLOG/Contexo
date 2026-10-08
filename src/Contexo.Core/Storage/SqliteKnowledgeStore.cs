using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Contexo.Core.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Contexo.Core.Storage;

/// <summary>
/// SQLite implementation of <see cref="IKnowledgeStore"/>. Every operation opens its own (pooled) connection, so one instance is safe
/// to use from many threads, and several instances (or processes) can share one database file in WAL mode.
/// </summary>
internal sealed class SqliteKnowledgeStore : IKnowledgeStore
{
    private const string IndexVersionKey = "index_version";
    private const int InClauseBatchSize = 500;
    private static readonly TimeSpan McpActivityRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan McpActivityPurgeInterval = TimeSpan.FromDays(1);

    private const string DocumentColumns =
        "id, folder_id, path, size_bytes, last_write_utc, content_hash, status, error_code, error_message, chunk_count, updated_at, next_retry_at";

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ILogger<SqliteKnowledgeStore> _logger;
    private readonly TimeProvider _time;
    private readonly object _purgeLock = new();
    private DateTimeOffset _lastActivityPurge = DateTimeOffset.MinValue;

    public SqliteKnowledgeStore(IAppPaths paths, ILogger<SqliteKnowledgeStore> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _databasePath = paths.DatabasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    // ------------------------------------------------------------------ setup

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken);

        var sqliteVersion = Version.Parse(await ScalarAsync<string>(connection, null, "SELECT sqlite_version()", cancellationToken) ?? "0.0.0");
        if (sqliteVersion < StoreSchema.MinimumSqliteVersion)
        {
            throw new InvalidOperationException(
                $"SQLite {sqliteVersion} is too old; Contexo needs {StoreSchema.MinimumSqliteVersion} or newer (FTS5 trigram tokenizer).");
        }

        // WAL is persistent in the file; it cannot be changed inside a transaction.
        await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken);

        if (await GetUserVersionAsync(connection, null, cancellationToken) >= StoreSchema.CurrentVersion)
        {
            await EnsureNotNewerAsync(connection, cancellationToken);
            return;
        }

        // Another process may migrate at the same time: re-read the version after taking the write lock.
        await using var transaction = await BeginWriteAsync(connection, cancellationToken);
        var current = await GetUserVersionAsync(connection, transaction, cancellationToken);
        if (current > StoreSchema.CurrentVersion)
        {
            throw NewerSchemaException(current);
        }

        foreach (var migration in StoreSchema.Migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken);
            await ExecuteAsync(connection, transaction, $"PRAGMA user_version = {migration.Version};", cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ScalarAsync<string>(connection, null, "SELECT value FROM meta WHERE key = $key", cancellationToken, ("$key", key));
    }

    public async Task SetMetaAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            null,
            "INSERT INTO meta(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            cancellationToken,
            ("$key", key),
            ("$value", value));
    }

    public async Task<long> GetIndexVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var value = await ScalarAsync<string>(connection, null, "SELECT value FROM meta WHERE key = $key", cancellationToken, ("$key", IndexVersionKey));
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ? version : 0;
    }

    // ------------------------------------------------------------------ folders

    public async Task<IReadOnlyList<WatchedFolder>> GetFoldersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, null, "SELECT id, path, display_name, excluded_subfolders, state, added_at, last_scan_at FROM folders ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<WatchedFolder>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadFolder(reader));
        }

        return result;
    }

    public async Task<WatchedFolder> AddFolderAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(path);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            null,
            "INSERT INTO folders(path, display_name, added_at) VALUES ($path, $name, $now) ON CONFLICT(path) DO NOTHING",
            cancellationToken,
            ("$path", normalized),
            ("$name", GetDisplayName(normalized)),
            ("$now", Iso(_time.GetUtcNow())));

        await using var command = Command(
            connection,
            null,
            "SELECT id, path, display_name, excluded_subfolders, state, added_at, last_scan_at FROM folders WHERE path = $path",
            ("$path", normalized));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadFolder(reader);
    }

    public async Task RemoveFolderAsync(long folderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await BeginWriteAsync(connection, cancellationToken);
        var removed = await ExecuteAsync(connection, transaction, "DELETE FROM folders WHERE id = $id", cancellationToken, ("$id", folderId));
        if (removed > 0)
        {
            await BumpIndexVersionAsync(connection, transaction, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetFolderExclusionsAsync(long folderId, IReadOnlyList<string> excludedSubfolders, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            null,
            "UPDATE folders SET excluded_subfolders = $json WHERE id = $id",
            cancellationToken,
            ("$json", StoreJson.SerializeStrings(excludedSubfolders)),
            ("$id", folderId));
    }

    /// <param name="lastScanAt">When null the stored last-scan time is left unchanged.</param>
    public async Task SetFolderStateAsync(long folderId, FolderState state, DateTimeOffset? lastScanAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            null,
            "UPDATE folders SET state = $state, last_scan_at = COALESCE($scan, last_scan_at) WHERE id = $id",
            cancellationToken,
            ("$state", (int)state),
            ("$scan", lastScanAt is { } scan ? Iso(scan) : null),
            ("$id", folderId));
    }

    // ------------------------------------------------------------------ documents

    public async Task<IReadOnlyList<DocumentRecord>> GetDocumentsAsync(long folderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryDocumentsAsync(connection, $"SELECT {DocumentColumns} FROM documents WHERE folder_id = $folder ORDER BY id", cancellationToken, ("$folder", folderId));
    }

    public async Task<IReadOnlyList<DocumentRecord>> GetFailedDocumentsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryDocumentsAsync(
            connection,
            $"SELECT {DocumentColumns} FROM documents WHERE status = $status ORDER BY updated_at DESC, id DESC LIMIT $limit",
            cancellationToken,
            ("$status", (int)DocumentStatus.Failed),
            ("$limit", Math.Max(limit, 0)));
    }

    public async Task<DocumentRecord?> GetDocumentByPathAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await QueryDocumentsAsync(connection, $"SELECT {DocumentColumns} FROM documents WHERE path = $path", cancellationToken, ("$path", NormalizePath(path)));
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task ReplaceDocumentAsync(DocumentWrite write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var path = NormalizePath(write.Path);
        var modelId = write.ModelId;
        if (write.Chunks.Any(c => c.Vector is not null) && string.IsNullOrEmpty(modelId))
        {
            throw new ArgumentException("ModelId is required when any chunk has a vector.", nameof(write));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await BeginWriteAsync(connection, cancellationToken);

        await ExecuteAsync(connection, transaction, "DELETE FROM documents WHERE path = $path", cancellationToken, ("$path", path));

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO documents(folder_id, path, size_bytes, last_write_utc, content_hash, status, error_code, error_message, chunk_count, updated_at, next_retry_at)
            VALUES ($folder, $path, $size, $write, $hash, $status, 0, NULL, $chunks, $now, NULL)
            """,
            cancellationToken,
            ("$folder", write.FolderId),
            ("$path", path),
            ("$size", write.Fingerprint.SizeBytes),
            ("$write", Iso(write.Fingerprint.LastWriteUtc)),
            ("$hash", write.Fingerprint.ContentHash),
            ("$status", (int)DocumentStatus.Indexed),
            ("$chunks", write.Chunks.Count),
            ("$now", Iso(_time.GetUtcNow())));
        var documentId = await LastInsertRowIdAsync(connection, transaction, cancellationToken);

        var tableIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in write.Tables)
        {
            if (tableIds.ContainsKey(table.TableKey))
            {
                continue;
            }

            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO excel_tables(document_id, table_key, data) VALUES ($doc, $key, $data)",
                cancellationToken,
                ("$doc", documentId),
                ("$key", table.TableKey),
                ("$data", StoreJson.SerializeTable(table)));
            tableIds[table.TableKey] = await LastInsertRowIdAsync(connection, transaction, cancellationToken);
        }

        await using var insertChunk = Command(
            connection,
            transaction,
            """
            INSERT INTO chunks(document_id, ordinal, kind, text, embedding_text, location, excel_table_id)
            VALUES ($doc, $ordinal, $kind, $text, $embed, $location, $table)
            """);
        var pDoc = insertChunk.Parameters.Add("$doc", SqliteType.Integer);
        var pOrdinal = insertChunk.Parameters.Add("$ordinal", SqliteType.Integer);
        var pKind = insertChunk.Parameters.Add("$kind", SqliteType.Integer);
        var pText = insertChunk.Parameters.Add("$text", SqliteType.Text);
        var pEmbed = insertChunk.Parameters.Add("$embed", SqliteType.Text);
        var pLocation = insertChunk.Parameters.Add("$location", SqliteType.Text);
        var pTable = insertChunk.Parameters.Add("$table", SqliteType.Integer);

        await using var insertVector = Command(
            connection,
            transaction,
            "INSERT OR REPLACE INTO embeddings(chunk_id, model, vector) VALUES ($chunk, $model, $vector)");
        var vChunk = insertVector.Parameters.Add("$chunk", SqliteType.Integer);
        var vModel = insertVector.Parameters.Add("$model", SqliteType.Text);
        var vVector = insertVector.Parameters.Add("$vector", SqliteType.Blob);

        foreach (var item in write.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = item.Chunk;
            pDoc.Value = documentId;
            pOrdinal.Value = chunk.Ordinal;
            pKind.Value = (int)chunk.Kind;
            pText.Value = chunk.Text;
            pEmbed.Value = chunk.EmbeddingText;
            pLocation.Value = StoreJson.SerializeLocation(chunk.Location);
            pTable.Value = chunk.TableKey is not null && tableIds.TryGetValue(chunk.TableKey, out var tableId) ? tableId : DBNull.Value;
            await insertChunk.ExecuteNonQueryAsync(cancellationToken);

            if (item.Vector is { } vector)
            {
                vChunk.Value = await LastInsertRowIdAsync(connection, transaction, cancellationToken);
                vModel.Value = modelId;
                vVector.Value = VectorSerializer.ToBytes(vector);
                await insertVector.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await BumpIndexVersionAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkDocumentAsync(
        long folderId,
        string path,
        FileFingerprint fingerprint,
        DocumentStatus status,
        DocumentErrorCode errorCode,
        string? errorMessage,
        DateTimeOffset? nextRetryAt,
        bool keepExistingChunks,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(path);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await BeginWriteAsync(connection, cancellationToken);

        var existingId = await ScalarAsync<long?>(connection, transaction, "SELECT id FROM documents WHERE path = $path", cancellationToken, ("$path", normalized));
        var removedData = false;
        if (existingId is not null && !keepExistingChunks)
        {
            // Chunks first: their delete triggers keep the FTS index in step, and embeddings cascade from them.
            var removedChunks = await ExecuteAsync(connection, transaction, "DELETE FROM chunks WHERE document_id = $id", cancellationToken, ("$id", existingId.Value));
            var removedTables = await ExecuteAsync(connection, transaction, "DELETE FROM excel_tables WHERE document_id = $id", cancellationToken, ("$id", existingId.Value));
            removedData = removedChunks + removedTables > 0;
        }

        var now = Iso(_time.GetUtcNow());
        if (existingId is null)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO documents(folder_id, path, size_bytes, last_write_utc, content_hash, status, error_code, error_message, chunk_count, updated_at, next_retry_at)
                VALUES ($folder, $path, $size, $write, $hash, $status, $code, $message, 0, $now, $retry)
                """,
                cancellationToken,
                ("$folder", folderId),
                ("$path", normalized),
                ("$size", fingerprint.SizeBytes),
                ("$write", Iso(fingerprint.LastWriteUtc)),
                ("$hash", fingerprint.ContentHash),
                ("$status", (int)status),
                ("$code", (int)errorCode),
                ("$message", errorMessage),
                ("$now", now),
                ("$retry", nextRetryAt is { } retry ? Iso(retry) : null));
        }
        else
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE documents SET
                  size_bytes = $size, last_write_utc = $write, content_hash = $hash, status = $status,
                  error_code = $code, error_message = $message, next_retry_at = $retry, updated_at = $now,
                  chunk_count = CASE WHEN $keep THEN chunk_count ELSE 0 END
                WHERE id = $id
                """,
                cancellationToken,
                ("$size", fingerprint.SizeBytes),
                ("$write", Iso(fingerprint.LastWriteUtc)),
                ("$hash", fingerprint.ContentHash),
                ("$status", (int)status),
                ("$code", (int)errorCode),
                ("$message", errorMessage),
                ("$retry", nextRetryAt is { } retry ? Iso(retry) : null),
                ("$now", now),
                ("$keep", keepExistingChunks ? 1 : 0),
                ("$id", existingId.Value));
        }

        if (removedData)
        {
            await BumpIndexVersionAsync(connection, transaction, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteDocumentAsync(long documentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await BeginWriteAsync(connection, cancellationToken);
        var removed = await ExecuteAsync(connection, transaction, "DELETE FROM documents WHERE id = $id", cancellationToken, ("$id", documentId));
        if (removed > 0)
        {
            await BumpIndexVersionAsync(connection, transaction, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MoveDocumentAsync(long documentId, string newPath, CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(newPath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await BeginWriteAsync(connection, cancellationToken);

        var exists = await ScalarAsync<long?>(connection, transaction, "SELECT id FROM documents WHERE id = $id", cancellationToken, ("$id", documentId));
        if (exists is null)
        {
            return;
        }

        var displaced = await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM documents WHERE path = $path AND id <> $id",
            cancellationToken,
            ("$path", normalized),
            ("$id", documentId));
        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE documents SET path = $path, updated_at = $now WHERE id = $id",
            cancellationToken,
            ("$path", normalized),
            ("$now", Iso(_time.GetUtcNow())),
            ("$id", documentId));
        if (displaced > 0)
        {
            await BumpIndexVersionAsync(connection, transaction, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ retrieval

    public async IAsyncEnumerable<StoredVector> ReadVectorsAsync(string modelId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, null, "SELECT chunk_id, vector FROM embeddings WHERE model = $model", ("$model", modelId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return new StoredVector(reader.GetInt64(0), VectorSerializer.FromBytes(reader.GetFieldValue<byte[]>(1)));
        }
    }

    public async Task<IReadOnlyList<KeywordHit>> KeywordSearchAsync(string? ftsQuery, IReadOnlyList<string> likeTerms, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return [];
        }

        var scores = new Dictionary<long, double>();
        await using var connection = await OpenAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(ftsQuery))
        {
            try
            {
                await using var command = Command(
                    connection,
                    null,
                    "SELECT rowid, bm25(chunks_fts) AS score FROM chunks_fts WHERE chunks_fts MATCH $q ORDER BY score LIMIT $limit",
                    ("$q", ftsQuery),
                    ("$limit", limit));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    scores[reader.GetInt64(0)] = -reader.GetDouble(1);
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                // Malformed MATCH expression: treat as no full-text hits. The query text itself is never logged.
                _logger.LogWarning("Full-text query was rejected by SQLite (code {Code}); ignoring it", ex.SqliteExtendedErrorCode);
                scores.Clear();
            }
        }

        var terms = likeTerms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count > 0)
        {
            var matches = terms.Select((_, i) => $"(text LIKE '%' || $t{i} || '%' ESCAPE '\\')").ToList();
            var sql = $"SELECT id, {string.Join(" + ", matches)} AS score FROM chunks WHERE {string.Join(" OR ", matches)} ORDER BY score DESC, id LIMIT $limit";
            await using var command = Command(connection, null, sql, ("$limit", limit));
            for (var i = 0; i < terms.Count; i++)
            {
                command.Parameters.AddWithValue($"$t{i}", EscapeLike(terms[i]));
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                var score = reader.GetDouble(1);
                scores[id] = scores.TryGetValue(id, out var existing) ? existing + score : score;
            }
        }

        return scores
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key)
            .Take(limit)
            .Select(p => new KeywordHit(p.Key, p.Value))
            .ToList();
    }

    public async Task<IReadOnlyList<ChunkDetail>> GetChunksAsync(IReadOnlyList<long> chunkIds, CancellationToken cancellationToken)
    {
        if (chunkIds.Count == 0)
        {
            return [];
        }

        var found = new Dictionary<long, ChunkDetail>();
        await using var connection = await OpenAsync(cancellationToken);
        foreach (var batch in chunkIds.Distinct().Chunk(InClauseBatchSize))
        {
            var names = string.Join(",", batch.Select((_, i) => $"$id{i}"));
            await using var command = Command(
                connection,
                null,
                $"""
                SELECT c.id, c.document_id, d.path, c.kind, c.text, c.location, c.excel_table_id
                FROM chunks c JOIN documents d ON d.id = c.document_id
                WHERE c.id IN ({names})
                """);
            for (var i = 0; i < batch.Length; i++)
            {
                command.Parameters.AddWithValue($"$id{i}", batch[i]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                found[id] = new ChunkDetail(
                    id,
                    reader.GetInt64(1),
                    reader.GetString(2),
                    (SectionKind)reader.GetInt32(3),
                    reader.GetString(4),
                    StoreJson.DeserializeLocation(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : "t" + reader.GetInt64(6).ToString(CultureInfo.InvariantCulture));
            }
        }

        var result = new List<ChunkDetail>(found.Count);
        foreach (var id in chunkIds.Distinct())
        {
            if (found.TryGetValue(id, out var detail))
            {
                result.Add(detail);
            }
        }

        return result;
    }

    public async IAsyncEnumerable<ChunkForEmbedding> ReadChunksMissingVectorAsync(string modelId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(
            connection,
            null,
            """
            SELECT c.id, c.embedding_text FROM chunks c
            WHERE NOT EXISTS (SELECT 1 FROM embeddings e WHERE e.chunk_id = c.id AND e.model = $model)
            ORDER BY c.id
            """,
            ("$model", modelId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return new ChunkForEmbedding(reader.GetInt64(0), reader.GetString(1));
        }
    }

    public async Task SaveVectorsAsync(string modelId, IReadOnlyList<StoredVector> vectors, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        if (vectors.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await BeginWriteAsync(connection, cancellationToken);

        // Chunks may have been deleted while the vectors were being computed: skip those instead of failing the batch.
        var existing = new HashSet<long>();
        foreach (var batch in vectors.Select(v => v.ChunkId).Distinct().Chunk(InClauseBatchSize))
        {
            var names = string.Join(",", batch.Select((_, i) => $"$id{i}"));
            await using var query = Command(connection, transaction, $"SELECT id FROM chunks WHERE id IN ({names})");
            for (var i = 0; i < batch.Length; i++)
            {
                query.Parameters.AddWithValue($"$id{i}", batch[i]);
            }

            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existing.Add(reader.GetInt64(0));
            }
        }

        await using var insert = Command(connection, transaction, "INSERT OR REPLACE INTO embeddings(chunk_id, model, vector) VALUES ($chunk, $model, $vector)");
        var pChunk = insert.Parameters.Add("$chunk", SqliteType.Integer);
        var pModel = insert.Parameters.Add("$model", SqliteType.Text);
        var pVector = insert.Parameters.Add("$vector", SqliteType.Blob);
        pModel.Value = modelId;
        var saved = 0;
        foreach (var vector in vectors)
        {
            if (!existing.Contains(vector.ChunkId))
            {
                continue;
            }

            pChunk.Value = vector.ChunkId;
            pVector.Value = VectorSerializer.ToBytes(vector.Vector);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            saved++;
        }

        if (saved > 0)
        {
            await BumpIndexVersionAsync(connection, transaction, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ spreadsheet tables

    public async Task<ExcelTableRecord?> GetExcelTableAsync(string tableId, CancellationToken cancellationToken)
    {
        if (tableId is null
            || tableId.Length < 2
            || tableId[0] != 't'
            || !long.TryParse(tableId.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(
            connection,
            null,
            "SELECT t.document_id, d.path, t.data FROM excel_tables t JOIN documents d ON d.id = t.document_id WHERE t.id = $id",
            ("$id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExcelTableRecord(tableId, reader.GetInt64(0), reader.GetString(1), StoreJson.DeserializeTable(reader.GetString(2)));
    }

    // ------------------------------------------------------------------ exclusions

    public async Task<IReadOnlyList<Exclusion>> GetExclusionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(connection, null, "SELECT id, path, is_folder, created_at FROM exclusions ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Exclusion>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadExclusion(reader));
        }

        return result;
    }

    public async Task<Exclusion> AddExclusionAsync(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(path);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await BeginWriteAsync(connection, cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO exclusions(path, is_folder, created_at) VALUES ($path, $folder, $now)
            ON CONFLICT(path) DO UPDATE SET is_folder = excluded.is_folder
            """,
            cancellationToken,
            ("$path", normalized),
            ("$folder", isFolder ? 1 : 0),
            ("$now", Iso(_time.GetUtcNow())));

        if (isFolder)
        {
            // Match the folder itself and everything below it, whichever separator the stored path uses.
            var isRoot = normalized.EndsWith('\\') || normalized.EndsWith('/');
            var withBackslash = isRoot ? normalized : normalized + '\\';
            var withSlash = isRoot ? normalized : normalized + '/';
            await ExecuteAsync(
                connection,
                transaction,
                """
                DELETE FROM documents
                WHERE path = $path
                   OR substr(path, 1, length($back)) = $back COLLATE NOCASE
                   OR substr(path, 1, length($slash)) = $slash COLLATE NOCASE
                """,
                cancellationToken,
                ("$path", normalized),
                ("$back", withBackslash),
                ("$slash", withSlash));
        }
        else
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM documents WHERE path = $path", cancellationToken, ("$path", normalized));
        }

        await BumpIndexVersionAsync(connection, transaction, cancellationToken);

        Exclusion result;
        await using (var command = Command(connection, transaction, "SELECT id, path, is_folder, created_at FROM exclusions WHERE path = $path", ("$path", normalized)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            result = ReadExclusion(reader);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task RemoveExclusionAsync(long exclusionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, "DELETE FROM exclusions WHERE id = $id", cancellationToken, ("$id", exclusionId));
    }

    // ------------------------------------------------------------------ MCP activity

    public async Task RecordMcpActivityAsync(McpActivity activity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            null,
            "INSERT INTO mcp_activity(kind, client_name, client_version, tool_name, detail, at) VALUES ($kind, $client, $version, $tool, $detail, $at)",
            cancellationToken,
            ("$kind", (int)activity.Kind),
            ("$client", activity.ClientName),
            ("$version", activity.ClientVersion),
            ("$tool", activity.ToolName),
            ("$detail", activity.Detail),
            ("$at", Iso(activity.At)));

        var now = _time.GetUtcNow();
        bool purge;
        lock (_purgeLock)
        {
            purge = now - _lastActivityPurge >= McpActivityPurgeInterval;
            if (purge)
            {
                _lastActivityPurge = now;
            }
        }

        if (purge)
        {
            await ExecuteAsync(
                connection,
                null,
                "DELETE FROM mcp_activity WHERE at < $cutoff",
                cancellationToken,
                ("$cutoff", Iso(now - McpActivityRetention)));
        }
    }

    public async Task<IReadOnlyList<McpClientActivitySummary>> GetMcpActivitySummariesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(
            connection,
            null,
            """
            SELECT a.client_name,
                   (SELECT v.client_version FROM mcp_activity v
                     WHERE v.client_name = a.client_name AND v.client_version IS NOT NULL
                     ORDER BY v.at DESC, v.id DESC LIMIT 1),
                   MAX(CASE WHEN a.kind = 0 THEN a.at END),
                   MAX(CASE WHEN a.kind = 1 THEN a.at END),
                   MAX(CASE WHEN a.kind = 2 THEN a.at END),
                   (SELECT e.detail FROM mcp_activity e
                     WHERE e.client_name = a.client_name AND e.kind = 2
                     ORDER BY e.at DESC, e.id DESC LIMIT 1)
            FROM mcp_activity a
            GROUP BY a.client_name
            ORDER BY MAX(a.at) DESC, a.client_name
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<McpClientActivitySummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new McpClientActivitySummary(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                ReadNullableTime(reader, 2),
                ReadNullableTime(reader, 3),
                ReadNullableTime(reader, 4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return result;
    }

    // ------------------------------------------------------------------ maintenance

    /// <summary><c>DocumentCount</c> counts every document row (indexed, failed and skipped); <c>FailedDocumentCount</c> is the failed subset.</summary>
    public async Task<StoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = Command(
            connection,
            null,
            """
            SELECT (SELECT COUNT(*) FROM folders),
                   (SELECT COUNT(*) FROM documents),
                   (SELECT COUNT(*) FROM documents WHERE status = $failed),
                   (SELECT COUNT(*) FROM chunks)
            """,
            ("$failed", (int)DocumentStatus.Failed));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new StoreStatistics(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), GetDatabaseBytes());
    }

    public async Task ClearIndexedDataAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using (var transaction = await BeginWriteAsync(connection, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM documents", cancellationToken);
            await ExecuteAsync(connection, transaction, "DELETE FROM mcp_activity", cancellationToken);
            await ExecuteAsync(connection, transaction, "INSERT INTO chunks_fts(chunks_fts) VALUES ('rebuild')", cancellationToken);
            await BumpIndexVersionAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // VACUUM cannot run inside a transaction. Truncating the WAL afterwards makes the freed space visible on disk.
        await ExecuteAsync(connection, null, "VACUUM", cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(connection, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Starts a transaction that takes the write lock immediately (BEGIN IMMEDIATE), so lock waits honour busy_timeout instead of failing on upgrade.</summary>
    private static async Task<SqliteTransaction> BeginWriteAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T?> ScalarAsync<T>(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            return default;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    private static async Task<long> LastInsertRowIdAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) =>
        await ScalarAsync<long>(connection, transaction, "SELECT last_insert_rowid()", cancellationToken);

    private static Task BumpIndexVersionAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO meta(key, value) VALUES ($key, '1')
            ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)
            """,
            cancellationToken,
            ("$key", IndexVersionKey));

    private static async Task<int> GetUserVersionAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken) =>
        await ScalarAsync<int>(connection, transaction, "PRAGMA user_version", cancellationToken);

    private static async Task EnsureNotNewerAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var version = await GetUserVersionAsync(connection, null, cancellationToken);
        if (version > StoreSchema.CurrentVersion)
        {
            throw NewerSchemaException(version);
        }
    }

    private static InvalidOperationException NewerSchemaException(int version) =>
        new($"The database schema version {version} is newer than this program supports ({StoreSchema.CurrentVersion}). Update Contexo.");

    private static async Task<IReadOnlyList<DocumentRecord>> QueryDocumentsAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, null, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<DocumentRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DocumentRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                new FileFingerprint(reader.GetInt64(3), ParseTime(reader.GetString(4)), reader.GetString(5)),
                (DocumentStatus)reader.GetInt32(6),
                (DocumentErrorCode)reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt32(9),
                ParseTime(reader.GetString(10)),
                ReadNullableTime(reader, 11)));
        }

        return result;
    }

    private static WatchedFolder ReadFolder(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        StoreJson.DeserializeStrings(reader.GetString(3)),
        (FolderState)reader.GetInt32(4),
        ParseTime(reader.GetString(5)),
        ReadNullableTime(reader, 6));

    private static Exclusion ReadExclusion(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0, ParseTime(reader.GetString(3)));

    private static DateTimeOffset? ReadNullableTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseTime(reader.GetString(ordinal));

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>Full path without a trailing separator (a drive root keeps its separator).</summary>
    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string GetDisplayName(string normalizedPath)
    {
        var name = Path.GetFileName(normalizedPath);
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        var root = Path.GetPathRoot(normalizedPath)?.TrimEnd('\\', '/');
        return string.IsNullOrEmpty(root) ? normalizedPath : root;
    }

    private static string EscapeLike(string term)
    {
        var builder = new StringBuilder(term.Length + 4);
        foreach (var c in term)
        {
            if (c is '\\' or '%' or '_')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private long GetDatabaseBytes()
    {
        long total = 0;
        foreach (var file in new[] { _databasePath, _databasePath + "-wal" })
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Exists)
                {
                    total += info.Length;
                }
            }
            catch (IOException)
            {
                // The file can disappear between the check and the read (WAL checkpoint); count it as empty.
            }
        }

        return total;
    }
}
