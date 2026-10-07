# T10 索引管線與資料夾同步

- **狀態**：待辦
- **波次**：2
- **相依**：T02、T03、T09（解析器 T04～T08 **不是**硬相依）
- **必讀**：`AGENTS.md`（第 9 節安全規則）、`plan/02-ingestion.md`（資料夾同步、初次建立索引）、`src/Contexo.Core/Abstractions/Indexing.cs`、`Storage.cs`、`Parsing.cs`

## 目標

實作 `Indexing.IndexingService : IIndexingService`：在背景持續讓資料庫與使用者的資料夾保持一致——新增、修改、刪除、改名都自動處理，而且**絕不誤刪**。

## 要做

### 1. 掃描範圍（`Indexing/FolderScanner.cs`）

對每個 `WatchedFolder` 列舉檔案，以下一律不納入：

- `FileCategories.IsBuiltInExcludedFile` / `IsBuiltInExcludedDirectory` / 隱藏或系統屬性。
- 資料夾的 `ExcludedSubfolders`。
- `IKnowledgeStore.GetExclusionsAsync` 的檔案與資料夾。
- 副檔名不屬於 `AppSettings.EnabledCategories`，或 `IParserRegistry.Resolve` 為 null。
- 本身也是另一個 `WatchedFolder` 的子資料夾（由那個資料夾負責，避免重複）。

超過 `MaxFileSizeMb` 的檔案仍納入清單，但標記為 `Skipped / TooLarge`，不讀內容。

列舉時遇到個別子資料夾 `UnauthorizedAccessException` 就略過該子資料夾並記錄；**資料夾根目錄**不存在或無法列舉時，該資料夾設為 `FolderState.Unavailable`，本次不做任何刪除。

### 2. 對帳（`Indexing/Reconciler.cs`）

把掃描結果與 `GetDocumentsAsync(folderId)` 比對：

| 情況 | 處理 |
|---|---|
| 新檔案 | 排入佇列 |
| 大小或修改時間不同 | 排入佇列（讀檔時再比對 hash） |
| 相同 | 不處理 |
| 資料庫有、磁碟沒有 | 列為「消失」 |
| 資料庫有、但已不在掃描範圍（類別被關閉、被排除） | 刪除資料（屬於使用者主動操作，不計入大量消失檢查） |
| `Failed/Locked` 且 `NextRetryAt` 已到 | 排入佇列 |

**改名／搬移偵測**：新檔案的大小與某個「消失」檔案相同時，計算新檔 hash；相同就 `MoveDocumentAsync`，不重新解析。

**大量消失保護**：同一資料夾中，「消失」數量 ≥ 20 且 > 該資料夾文件數的 30% 時，不刪除，將資料夾設為 `AwaitingDeletionConfirmation` 並觸發 `MassDeletionPendingRaised`。等 `ResolveMassDeletionAsync` 回覆：`true` 刪除、`false` 保留並回到 `Active`（下次掃描若仍消失，會再次詢問；同一批消失檔案在 24 小時內不重複詢問）。未達門檻的消失檔案直接 `DeleteDocumentAsync`。

### 3. 處理單一檔案（`Indexing/DocumentProcessor.cs`）

1. 以 `FileShare.ReadWrite | FileShare.Delete` 開啟，讀進 `MemoryStream`；計算 SHA-256。
2. 若 hash 與資料庫相同 → `MarkDocumentAsync(…, Indexed, None, keepExistingChunks: true)`（只更新 fingerprint）並結束。
3. 解析：`IParserRegistry.Resolve(ext).ParseAsync`，每個檔案逾時 2 分鐘（連結的 `CancellationTokenSource`）。
4. **內嵌檔案遞迴**：對 `EmbeddedFiles` 依副檔名解析，深度上限 3、單一檔案內嵌總量上限 50 MB。內嵌檔的 `ParseContext.EmbeddedPath` = 外層路徑 + 內嵌檔名。內嵌檔的 `TableKey`（區段與 `SpreadsheetTable` 兩邊）改為 `"{內嵌路徑以 › 連接}#{原 TableKey}"`，確保同一文件內唯一。所有區段依「本體、內嵌 1、內嵌 2…」順序合併。
5. 切塊：`IChunker.Split(檔名不含副檔名, 全部區段, ChunkingOptions)`。
6. 向量：`IEmbeddingService.IsAvailable` 時，以批次對 `EmbeddingText` 計算；不可用時 `Vector = null`（之後由第 6 點補算）。
7. `ReplaceDocumentAsync`。

錯誤對應：

| 例外 | 呼叫 |
|---|---|
| `DocumentParseException` | `MarkDocumentAsync(新 fingerprint, Failed, e.Code, keepExistingChunks: false)` |
| 共用違規 `IOException`（HResult 0x80070020 / 0x80070021） | `MarkDocumentAsync(**舊** fingerprint（沒有時用新的但 hash 留空字串）, Failed, Locked, nextRetryAt: 現在 + 5 分鐘, keepExistingChunks: true)` |
| `UnauthorizedAccessException` | `Failed, AccessDenied, keepExistingChunks: false` |
| 逾時 | `Failed, Timeout, keepExistingChunks: false` |
| `NotImplementedException`（解析器尚未完成） | `Skipped, Unsupported, keepExistingChunks: false` |
| 其他 | `Failed, Unknown`，記錄日誌（只記檔名與例外類型） |
| 檔案在處理途中消失 | 視為「消失」，交給下次對帳 |

### 4. 排程與佇列（`Indexing/IndexingService.cs`）

- `StartAsync`：讀取資料夾 → 全部對帳 → 開始處理佇列；之後：
  - 每個 Active 資料夾掛 `FileSystemWatcher`（含子資料夾），事件去抖動 3 秒後對受影響的路徑做局部對帳；watcher 錯誤（緩衝區溢位）時改做該資料夾的全量對帳。
  - 每 30 分鐘全量對帳一次。
  - `Unavailable` 的資料夾每 1 分鐘檢查是否恢復，恢復就全量對帳。
  - 監聽 `ISettingsStore.Changed`：類別或大小上限改變時全量對帳。
- 佇列優先序：**修改時間新的先處理**；`RequestRetry` 與 watcher 觸發的檔案插到最前面。
- 節流：`FullSpeedOnlyWhenIdle` 為 true 且 `IUserActivityMonitor.IdleTime` < 2 分鐘時，一次處理 1 個檔案，每個檔案之間等 300ms；否則同時處理 2 個檔案。
- `Pause`／`Resume`：暫停時完成目前檔案後停止；狀態 `Paused`。
- `RequestRescan(folderId)`：排入全量對帳（同一資料夾不重複排）。
- `StopAsync`：取消並等待背景工作結束（最多 5 秒）、釋放 watcher。

### 5. 進度快照

- `TotalFiles`／`ProcessedFiles`：本輪需要處理的數量與已完成數量（全部完成後兩者歸零，狀態 `Idle`）。
- `EstimatedRemaining`：處理 ≥ 5 個檔案後，以最近 20 個檔案的平均耗時 × 剩餘數量估算。
- `Folders`：每個資料夾的總數、已讀、失敗、待處理。
- `RecentActivity`：每分鐘彙總一筆（例如「Updated 3」），保留最近 5 筆。
- `SnapshotChanged`：最多每 250ms 觸發一次。

### 6. 模型變更與補算向量

- 啟動時比較 `meta["embedding_model"]` 與 `IEmbeddingService.ModelId`；不同時更新 meta。
- 佇列空閒時，若 `IEmbeddingService.IsAvailable`，以 `ReadChunksMissingVectorAsync(ModelId)` 每批 64 筆計算後 `SaveVectorsAsync`，直到沒有缺漏。此工作同樣受暫停與節流控制，並反映在快照中（`CurrentFile` 顯示「正在更新搜尋資料」）。

## 不做

- 任何 UI（T16、T19）。
- 解析器本身。

## 可修改範圍

- `src/Contexo.Core/Indexing/**`（`AlwaysIdleActivityMonitor` 除外）
- `tests/Contexo.Core.Tests/Indexing/**`

## 實作要點與已知陷阱

- **安全規則**：任何情況下都不寫入、移動、刪除使用者檔案。開檔一律唯讀。
- `FileSystemWatcher` 會漏事件，也可能一次觸發多次，只能當作「提示哪裡要對帳」，不能直接依事件刪資料。
- OneDrive 未下載的雲端檔案（`FileAttributes.RecallOnDataAccess` 或 `Offline`）：第一版**略過**，不觸發下載，也不視為消失。
- 路徑比較不分大小寫（`StringComparer.OrdinalIgnoreCase`）。
- 注入 `TimeProvider` 方便測試（.NET 8 內建），測試中自行實作可手動推進的假時鐘。
- 測試使用：真實的 `SqliteKnowledgeStore`（暫存資料夾）、假的解析器（例如把 `.fake` 檔內容當成一個 Prose 區段）、假的 embedding（依文字 hash 產生固定向量）、真實的 `StructuredChunker`。

## 驗收條件

`dotnet test --filter FullyQualifiedName~Indexing` 全部通過，至少涵蓋：

1. 初次掃描：10 個檔案全部寫入，快照從 Indexing 到 Idle，`ProcessedFiles` 正確。
2. 修改檔案內容 → 重新解析；只改修改時間不改內容 → 不重新解析（解析器呼叫次數不變），fingerprint 更新。
3. 刪除 2 個檔案（共 10 個）→ 資料被刪。
4. **大量消失**：刪除 25 個檔案（共 40 個）→ 不刪除、觸發事件、資料夾狀態變更；回覆 false 後資料仍在；回覆 true 後才刪除。
5. **資料夾不見**：把整個資料夾改名 → 狀態 `Unavailable`、資料完全保留；改回原名後恢復 `Active` 且不重新解析。
6. 改名檔案 → `MoveDocumentAsync`，解析器沒有被再次呼叫。
7. 被鎖定的檔案（測試中用 `FileShare.None` 開著）→ `Locked`、保留舊資料；解鎖並推進時鐘 5 分鐘後被處理。
8. 內建排除（`~$暫存.docx`、`.git` 資料夾、隱藏檔）、`ExcludedSubfolders`、使用者排除清單、關閉的類別，都不會被處理；關閉類別後既有資料被刪除，且不觸發大量消失詢問。
9. 巢狀資料夾：同時加入 `A` 與 `A\B`，`A\B` 底下的檔案只屬於一個資料夾。
10. 內嵌檔：假解析器回傳一個內嵌檔 → 片段包含內嵌內容，`EmbeddedPath` 正確，`TableKey` 有前綴。
11. 解析器逾時（假解析器等待超過時限）→ `Timeout`；拋 `NotImplementedException` → `Skipped/Unsupported`。
12. Embedding 不可用時寫入沒有向量的片段；之後改成可用 → 背景補算完成，`ReadChunksMissingVectorAsync` 為空。
13. 暫停後不再處理新檔案；繼續後完成。
14. `StopAsync` 在 5 秒內完成，沒有殘留背景工作。

## 完成紀錄

（由執行者填寫）
