namespace Contexo.Core.Storage;

/// <summary>One step of the schema history. Applied in order inside a transaction; <c>PRAGMA user_version</c> records the last applied version.</summary>
internal sealed record SchemaMigration(int Version, string Sql);

internal static class StoreSchema
{
    /// <summary>Oldest SQLite that supports the FTS5 trigram tokenizer.</summary>
    public static readonly Version MinimumSqliteVersion = new(3, 34, 0);

    public static IReadOnlyList<SchemaMigration> Migrations { get; } =
    [
        new(1, Version1),
    ];

    public static int CurrentVersion => Migrations[^1].Version;

    private const string Version1 = """
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

        CREATE TABLE folders (
          id INTEGER PRIMARY KEY,
          path TEXT NOT NULL UNIQUE COLLATE NOCASE,
          display_name TEXT NOT NULL,
          excluded_subfolders TEXT NOT NULL DEFAULT '[]',
          state INTEGER NOT NULL DEFAULT 0,
          added_at TEXT NOT NULL,
          last_scan_at TEXT);

        CREATE TABLE documents (
          id INTEGER PRIMARY KEY,
          folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
          path TEXT NOT NULL UNIQUE COLLATE NOCASE,
          size_bytes INTEGER NOT NULL,
          last_write_utc TEXT NOT NULL,
          content_hash TEXT NOT NULL,
          status INTEGER NOT NULL,
          error_code INTEGER NOT NULL DEFAULT 0,
          error_message TEXT,
          chunk_count INTEGER NOT NULL DEFAULT 0,
          updated_at TEXT NOT NULL,
          next_retry_at TEXT);
        CREATE INDEX ix_documents_folder ON documents(folder_id);
        CREATE INDEX ix_documents_hash ON documents(content_hash);

        CREATE TABLE excel_tables (
          id INTEGER PRIMARY KEY,
          document_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
          table_key TEXT NOT NULL,
          data TEXT NOT NULL,
          UNIQUE(document_id, table_key));

        CREATE TABLE chunks (
          id INTEGER PRIMARY KEY,
          document_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
          ordinal INTEGER NOT NULL,
          kind INTEGER NOT NULL,
          text TEXT NOT NULL,
          embedding_text TEXT NOT NULL,
          location TEXT NOT NULL,
          excel_table_id INTEGER REFERENCES excel_tables(id) ON DELETE SET NULL);
        CREATE INDEX ix_chunks_document ON chunks(document_id);

        CREATE TABLE embeddings (
          chunk_id INTEGER NOT NULL REFERENCES chunks(id) ON DELETE CASCADE,
          model TEXT NOT NULL,
          vector BLOB NOT NULL,
          PRIMARY KEY (chunk_id, model));
        CREATE INDEX ix_embeddings_model ON embeddings(model);

        CREATE VIRTUAL TABLE chunks_fts USING fts5(
          text, content='chunks', content_rowid='id', tokenize='trigram');

        CREATE TRIGGER chunks_ai AFTER INSERT ON chunks BEGIN
          INSERT INTO chunks_fts(rowid, text) VALUES (new.id, new.text);
        END;
        CREATE TRIGGER chunks_ad AFTER DELETE ON chunks BEGIN
          INSERT INTO chunks_fts(chunks_fts, rowid, text) VALUES ('delete', old.id, old.text);
        END;
        CREATE TRIGGER chunks_au AFTER UPDATE ON chunks BEGIN
          INSERT INTO chunks_fts(chunks_fts, rowid, text) VALUES ('delete', old.id, old.text);
          INSERT INTO chunks_fts(rowid, text) VALUES (new.id, new.text);
        END;

        CREATE TABLE exclusions (
          id INTEGER PRIMARY KEY,
          path TEXT NOT NULL UNIQUE COLLATE NOCASE,
          is_folder INTEGER NOT NULL,
          created_at TEXT NOT NULL);

        CREATE TABLE mcp_activity (
          id INTEGER PRIMARY KEY,
          kind INTEGER NOT NULL,
          client_name TEXT NOT NULL,
          client_version TEXT,
          tool_name TEXT,
          detail TEXT,
          at TEXT NOT NULL);
        CREATE INDEX ix_mcp_activity_client ON mcp_activity(client_name, kind, at);
        """;
}
