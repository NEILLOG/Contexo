# T02 SQLite 儲存層

- **狀態**：完成
- **波次**：1
- **相依**：T01
- **必讀**：`AGENTS.md`、`plan/01-architecture.md`（儲存結構）、`src/Contexo.Core/Abstractions/Storage.cs`、`Parsing.cs`、`Chunking.cs`

## 目標

實作 `Storage.SqliteKnowledgeStore : IKnowledgeStore`。它是桌面程式（寫入）與 MCP（讀取、記錄活動）兩個程序共用的唯一資料庫。

## 要做

### 連線

- 資料庫路徑取自 `IAppPaths.DatabasePath`。
- 每個操作開新連線（`Microsoft.Data.Sqlite` 內建連線池）。每條連線開啟後執行：`PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;`。
- `InitializeAsync`：`PRAGMA journal_mode=WAL;`、建立或升級 schema。用 `PRAGMA user_version` 記錄 schema 版本（第一版為 1），升級程式寫成依版本逐步執行的 migration 清單。
- 所有多步驟寫入包在交易中。

### Schema（版本 1）

路徑欄位一律 `COLLATE NOCASE`，存 `Path.GetFullPath` 正規化後的完整路徑。

```sql
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

CREATE TABLE folders (
  id INTEGER PRIMARY KEY,
  path TEXT NOT NULL UNIQUE COLLATE NOCASE,
  display_name TEXT NOT NULL,
  excluded_subfolders TEXT NOT NULL DEFAULT '[]',   -- JSON array
  state INTEGER NOT NULL DEFAULT 0,                 -- FolderState
  added_at TEXT NOT NULL,
  last_scan_at TEXT);

CREATE TABLE documents (
  id INTEGER PRIMARY KEY,
  folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
  path TEXT NOT NULL UNIQUE COLLATE NOCASE,
  size_bytes INTEGER NOT NULL,
  last_write_utc TEXT NOT NULL,
  content_hash TEXT NOT NULL,
  status INTEGER NOT NULL,                          -- DocumentStatus
  error_code INTEGER NOT NULL DEFAULT 0,            -- DocumentErrorCode
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
  data TEXT NOT NULL,                               -- SpreadsheetTable as JSON
  UNIQUE(document_id, table_key));

CREATE TABLE chunks (
  id INTEGER PRIMARY KEY,
  document_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
  ordinal INTEGER NOT NULL,
  kind INTEGER NOT NULL,                            -- SectionKind
  text TEXT NOT NULL,
  embedding_text TEXT NOT NULL,                     -- Chunk.EmbeddingText，換模型時重算向量用
  location TEXT NOT NULL,                           -- SourceLocation as JSON
  excel_table_id INTEGER REFERENCES excel_tables(id) ON DELETE SET NULL);
CREATE INDEX ix_chunks_document ON chunks(document_id);

CREATE TABLE embeddings (
  chunk_id INTEGER NOT NULL REFERENCES chunks(id) ON DELETE CASCADE,
  model TEXT NOT NULL,
  vector BLOB NOT NULL,                             -- float32 little-endian
  PRIMARY KEY (chunk_id, model));
CREATE INDEX ix_embeddings_model ON embeddings(model);

CREATE VIRTUAL TABLE chunks_fts USING fts5(
  text, content='chunks', content_rowid='id', tokenize='trigram');
-- 以 AFTER INSERT / AFTER DELETE / AFTER UPDATE 觸發器同步 chunks_fts（FTS5 external content 標準寫法）

CREATE TABLE exclusions (
  id INTEGER PRIMARY KEY,
  path TEXT NOT NULL UNIQUE COLLATE NOCASE,
  is_folder INTEGER NOT NULL,
  created_at TEXT NOT NULL);

CREATE TABLE mcp_activity (
  id INTEGER PRIMARY KEY,
  kind INTEGER NOT NULL,                            -- McpEventKind
  client_name TEXT NOT NULL,
  client_version TEXT,
  tool_name TEXT,
  detail TEXT,
  at TEXT NOT NULL);
CREATE INDEX ix_mcp_activity_client ON mcp_activity(client_name, kind, at);
```

時間一律存 ISO 8601 UTC（`DateTimeOffset.UtcNow.ToString("O")`）。

### 方法行為

- `GetIndexVersionAsync`：讀 `meta.index_version`（不存在為 0）。下列操作在**同一交易內**將它加一：`ReplaceDocumentAsync`、`MarkDocumentAsync`（當 `keepExistingChunks=false` 且確實刪除了資料）、`DeleteDocumentAsync`、`RemoveFolderAsync`、`AddExclusionAsync`、`SaveVectorsAsync`、`ClearIndexedDataAsync`。
- `AddFolderAsync`：路徑正規化；`DisplayName` 為資料夾名稱（磁碟根目錄用磁碟代號）。已存在時回傳既有資料，不拋例外。
- `ReplaceDocumentAsync`：單一交易內 → 刪除同路徑舊文件（cascade）→ 插入 documents（status=Indexed）→ 插入 excel_tables → 依序插入 chunks（`TableKey` 對應到剛插入的 excel_tables.id）→ 插入 embeddings（`Vector` 為 null 的略過）。`ChunkCount` 寫入 documents。
- `MarkDocumentAsync`：
  - `keepExistingChunks=true`：保留 chunks 與向量，更新 fingerprint（size、last_write、hash）、status、錯誤、`next_retry_at`、`updated_at`；文件不存在時新建一筆（chunk_count=0）。
  - `keepExistingChunks=false`：刪除舊資料，留下一筆只有狀態的 documents 列。
- `MoveDocumentAsync`：只更新路徑。若新路徑已被其他文件占用，先刪除那一筆。
- `AddExclusionAsync`：檔案 → 刪除該路徑文件；資料夾 → 刪除路徑等於它或以「它 + 目錄分隔符」開頭的所有文件（分隔符 `\` 與 `/` 都要處理）。同一交易。
- `ReadVectorsAsync`：以 `IAsyncEnumerable` 串流，不要一次載入全部到 List。
- `KeywordSearchAsync`：
  - `ftsQuery` 非 null：`SELECT rowid, bm25(chunks_fts) FROM chunks_fts WHERE chunks_fts MATCH $q ORDER BY bm25 LIMIT $limit`，`Rank = -bm25`。MATCH 語法錯誤時記錄警告並視為無結果，不拋例外。
  - `likeTerms`：每個詞 `text LIKE '%' || $t || '%'`（記得跳脫 `%`、`_`），命中詞數越多 rank 越高；與 FTS 結果合併去重後取前 `limit`。
- `ReadChunksMissingVectorAsync`：`SELECT c.id, c.embedding_text FROM chunks c WHERE NOT EXISTS (SELECT 1 FROM embeddings e WHERE e.chunk_id=c.id AND e.model=$m) ORDER BY c.id`，串流回傳。
- `SaveVectorsAsync`：單一交易 `INSERT OR REPLACE`；chunk 已不存在時（外鍵失敗）略過該筆，不讓整批失敗——先以 `SELECT id FROM chunks WHERE id IN (...)` 過濾。
- `GetChunksAsync`：一次查詢取回（`IN` 參數清單），回傳順序與輸入相同，查不到的略過。`TableId` 為 `"t" + excel_tables.id`。
- `GetExcelTableAsync`：解析 `"t{id}"`；格式錯誤或不存在回 null。
- `GetMcpActivitySummariesAsync`：依 `client_name` 分組，取最後一筆 Connected、ToolCall、Error 的時間與最後錯誤 detail、最新 client_version。`mcp_activity` 只保留最近 30 天，`RecordMcpActivityAsync` 時順便清除過舊資料（每天最多清一次即可）。
- `GetStatisticsAsync`：`DatabaseBytes` 為主檔加 `-wal` 檔大小。
- `ClearIndexedDataAsync`：刪除 documents（連帶 chunks、embeddings、tables）與 mcp_activity，重建 FTS（`INSERT INTO chunks_fts(chunks_fts) VALUES('rebuild')`），交易結束後執行 `VACUUM`。保留 folders、exclusions、meta（index_version 照規則加一）。

### 向量序列化

`float[]` ↔ BLOB 用 `MemoryMarshal.AsBytes` / `MemoryMarshal.Cast<byte,float>`，固定 little-endian（在 big-endian 平台拋 `PlatformNotSupportedException`）。放在 `Storage/VectorSerializer.cs`，`internal`。

### JSON

`SourceLocation`、`SpreadsheetTable`、excluded_subfolders 用 `System.Text.Json`，`JsonSerializerOptions` 設 `DefaultIgnoreCondition = WhenWritingNull`、`Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`（中文可讀）。放在 `Storage/StoreJson.cs`。

## 不做

- 向量相似度計算（T11）。
- FTS 查詢字串的組法（T11 負責產生 `ftsQuery` 與 `likeTerms`）。

## 可修改範圍

- `src/Contexo.Core/Storage/**`
- `tests/Contexo.Core.Tests/Storage/**`

## 實作要點與已知陷阱

- `trigram` tokenizer 需要 SQLite 3.34 以上；`Microsoft.Data.Sqlite` 內附的 e_sqlite3 版本足夠。`InitializeAsync` 檢查 `sqlite_version()`，太舊就拋出清楚的例外。
- trigram 對少於 3 個字元的查詢沒有結果，所以才有 `likeTerms`。
- FTS5 external content 的刪除觸發器必須用 `INSERT INTO chunks_fts(chunks_fts, rowid, text) VALUES('delete', old.id, old.text)`。
- cascade 刪除 chunks 也會觸發 FTS 的刪除觸發器，確認這點有測試覆蓋。
- 兩個程序同時存取：讀取者在 WAL 下不會被寫入者擋住；寫入衝突靠 `busy_timeout`。
- `COLLATE NOCASE` 只對 ASCII 不分大小寫，這對 Windows 路徑已足夠。

## 驗收條件

`dotnet test --filter FullyQualifiedName~Storage` 全部通過，測試至少涵蓋：

1. `InitializeAsync` 可重複呼叫；`user_version` 為 1；`journal_mode` 為 wal。
2. 資料夾新增（重複新增回傳同一筆）、排除子資料夾讀寫、移除時連帶刪除文件與 chunks、FTS 也查不到。
3. `ReplaceDocumentAsync` 兩次替換同一路徑後，只剩第二次的 chunks 與向量；`index_version` 遞增。
4. `TableKey` 正確關聯，`GetChunksAsync` 回傳 `TableId`，`GetExcelTableAsync` 取回完整 `SpreadsheetTable`。
5. 向量往返：寫入後 `ReadVectorsAsync` 讀出的值逐位元相同，且只回傳指定 model。
5a. 寫入時部分 chunk 沒有向量 → `ReadChunksMissingVectorAsync` 只回傳那些 chunk 與正確的 `EmbeddingText`；`SaveVectorsAsync` 補上後不再出現；其中混入已刪除的 chunk id 時不拋例外。換一個 model id 查詢時，全部 chunk 都會被列出。
6. `KeywordSearchAsync`：中文 trigram 命中（例如內文「監視系統建置報價」，查「系統建置」）；兩字詞「報價」透過 `likeTerms` 命中；錯誤的 MATCH 語法不拋例外。
7. `MarkDocumentAsync` 兩種模式（`true` 時 chunks 保留且 fingerprint 被更新）；`MoveDocumentAsync` 不影響 chunks。
8. 排除資料夾會刪除其下文件但不影響同名前綴的其他資料夾（`C:\A\報價` 與 `C:\A\報價單`）。
9. MCP 活動摘要正確分組；超過 30 天的紀錄被清除。
10. `ClearIndexedDataAsync` 之後統計為 0、資料夾與排除清單仍在、資料庫檔案變小。
11. 並行：兩個 `SqliteKnowledgeStore` 實例指向同一檔案，一個持續 `ReplaceDocumentAsync`，另一個同時 `ReadVectorsAsync` 與 `KeywordSearchAsync`，持續 3 秒無例外。

## 完成紀錄

**做了什麼**

- `src/Contexo.Core/Storage/SqliteKnowledgeStore.cs`：實作 `IKnowledgeStore` 全部成員（建構式 `(IAppPaths, ILogger<SqliteKnowledgeStore>, TimeProvider? = null)`，`TimeProvider` 供測試控制時間，DI 不需另外註冊）。
- `Storage/StoreSchema.cs`：schema 版本 1 與 migration 清單（依 `PRAGMA user_version` 逐步執行，資料庫版本比程式新時拋出 `InvalidOperationException`）。
- `Storage/VectorSerializer.cs`、`Storage/StoreJson.cs`：依任務檔規格。
- 測試：`tests/Contexo.Core.Tests/Storage/`（`StoreFixture`、`SchemaAndFolderTests`、`DocumentTests`、`VectorAndSearchTests`、`ExclusionActivityMaintenanceTests`、`ConcurrencyTests`）。

**驗收結果（macOS，.NET 10.0.401）**

- `dotnet build Contexo.slnx -warnaserror`：0 警告、0 錯誤。
- `dotnet test --filter FullyQualifiedName~Storage`：通過 50、略過 2、失敗 0。
- `dotnet test`（全部）：Core 209 通過／2 略過、App 1、Mcp 5、Desktop 1，全數通過。
- 驗收 1～11 皆有對應測試：初始化重複呼叫／`user_version=1`／WAL；資料夾新增與排除與移除（FTS 同步清除）；兩次替換與 `index_version`；`TableKey`／`TableId`／`GetExcelTableAsync`；向量逐位元往返與 model 篩選；缺向量補齊（含已刪除 chunk id、換 model）；中文 trigram、`likeTerms`、錯誤 MATCH 語法；`MarkDocumentAsync` 兩種模式與 `MoveDocumentAsync`；`報價` 與 `報價單` 排除；MCP 摘要與 30 天清除；`ClearIndexedDataAsync` 後統計為 0 且檔案變小；兩個 store 實例並行 3 秒（另加兩個寫入者互等的測試）。

**無法在目前環境驗證**

- 兩個標記 `Category=Windows` 的測試在 macOS 略過：磁碟根目錄顯示名稱（`D:\` → `D:`）、`C:\A\報價` 與 `C:/A/報價` 混用分隔符的排除。需在 Windows 確認。macOS 上的排除測試改用 `Path.Combine` 組路徑（`C:\...` 在 macOS 會被 `GetFullPath` 當成相對路徑）。

**與規格不同或規格未明之處**

- 寫入交易使用 `BEGIN IMMEDIATE`（`BeginTransactionAsync` 預設行為），避免 WAL 下讀轉寫升級時立刻 `SQLITE_BUSY`、不等 `busy_timeout`。
- `SetFolderStateAsync(lastScanAt: null)` 表示「不改動最後掃描時間」（契約未寫明）。
- `GetStatisticsAsync().DocumentCount` 計算 documents 全部列（含 Failed、Skipped）；`FailedDocumentCount` 為其中失敗者。
- `index_version` 加一的條件：`ReplaceDocumentAsync`、`AddExclusionAsync`、`ClearIndexedDataAsync` 一律加；`DeleteDocumentAsync`、`RemoveFolderAsync`、`MoveDocumentAsync`（覆蓋了他人的文件時）在確實刪到資料時才加；`SaveVectorsAsync` 在至少寫入一筆時才加。
- `KeywordSearchAsync` 的 FTS 與 LIKE 同時命中同一 chunk 時 rank 相加，結果依 rank 由高到低排序；LIKE 的 rank 為命中詞數。MATCH 語法錯誤的警告日誌不含查詢內容。
- `ClearIndexedDataAsync` 在 `VACUUM` 之後多做 `PRAGMA wal_checkpoint(TRUNCATE)`，讓空間真正釋放到檔案大小。
- `MarkDocumentAsync(keepExistingChunks: false)` 就地清掉 chunks 與 tables 並更新同一列，文件 id 不變。
- 同一個 `DocumentWrite` 內重複的 `TableKey` 只保留第一筆。

**給後續任務的注意事項**

- T11：`ftsQuery` 需自行組成合法 FTS5 表達式（短語請加雙引號）；trigram 對少於 3 字元的詞無結果，請放進 `likeTerms`。`Rank` 越大越好，FTS 為 `-bm25`。
- T10：`ReplaceDocumentAsync` 的 `ModelId` 在有任何向量時必填，否則拋 `ArgumentException`。路徑一律以 `Path.GetFullPath` 正規化並去掉結尾分隔符，呼叫端比對路徑時請使用同樣規則。
- 測試結束時需呼叫 `SqliteConnection.ClearAllPools()` 才能在 Windows 刪除暫存資料庫（`StoreFixture.Dispose` 已處理）。
