# T12 Excel 表格查詢

- **狀態**：完成
- **波次**：2
- **相依**：T02、T08
- **必讀**：`AGENTS.md`、`plan/02-ingestion.md`（Excel 大表的查詢方式）、`src/Contexo.Core/Abstractions/Search.cs`（`ITableQueryService`）、`Parsing.cs`（`ISpreadsheetRegionReader`）

## 目標

實作 `Tables.TableQueryService : ITableQueryService`。AI 透過 MCP 的 `describe_table` / `query_table` 對 Excel 大表做加總、排序、篩選。資料在**查詢當下**從原始檔讀入記憶體 SQLite，查完即丟。

## 要做

### 1. 載入（`Tables/TableLoader.cs`）

1. `IKnowledgeStore.GetExcelTableAsync(tableId)`；不存在 → `TableQueryException("找不到這個表格，可能已被移除。請重新搜尋。")`。
2. 原始檔不存在 → `TableQueryException("原始檔案已移動或刪除：{檔名}")`。
3. `ISpreadsheetRegionReader.ReadAsync(filePath, Sheet, CellRange, HeaderRowCount)`。
4. 建立 `Data Source=:memory:` 連線，資料表名稱固定為 `t`。
5. 欄名轉 SQL 安全名稱：保留中文與英數，其餘字元換成 `_`，開頭是數字時加 `c_`，重複加尾碼。以雙引號包住。`TableColumn.Header` 保留原欄名。
6. 型別推斷（每欄看全部非空值）：全部可解析為數字（允許千分位、`%`、前後空白、貨幣符號 `$ NT$ ￥`）→ `REAL`；全部可解析為 `yyyy/MM/dd` 或 `yyyy-MM-dd`（可含時間）→ 以 `TEXT` 存 ISO 格式 `yyyy-MM-dd HH:mm:ss`、`InferredType = "date"`；否則 `TEXT`。空字串存 NULL。
7. 批次插入（單一交易、預備語句）。

### 2. 快取

同一 `tableId` 在 60 秒內、且原始檔修改時間未變時，重用已載入的記憶體資料庫。最多快取 4 張表（LRU），釋放時關閉連線。

### 3. `DescribeAsync`

回傳欄位（SQL 名稱、原欄名、型別）、總列數、前 `sampleRows` 列（最多 20）。

### 4. `QueryAsync`（唯讀保護）

1. 去掉前後空白與結尾分號後，只允許**一個**以 `SELECT` 或 `WITH` 開頭的語句；含第二個語句（分號後還有內容）→ 拒絕。
2. 連線開啟後設定 `PRAGMA query_only = ON`，即使語法檢查有漏洞也無法寫入。
3. 禁止 `ATTACH`、`DETACH`、`PRAGMA`、`load_extension`（關鍵字檢查，不分大小寫）。
4. 執行逾時 5 秒（`SqliteCommand.CommandTimeout` 加上 `CancellationToken`）。
5. 最多回傳 `maxRows`（上限 500）列，超過時 `Truncated = true`。
6. 值轉成字串（REAL 去除多餘小數位，例如 `1280000` 而非 `1280000.0`）。
7. SQL 錯誤 → `TableQueryException`，訊息包含 SQLite 的錯誤說明與可用欄位清單，讓 AI 可以自行修正。

## 不做

- 預先把表格存入主資料庫（第一版採查詢時載入）。
- 跨表 JOIN。

## 可修改範圍

- `src/Contexo.Core/Tables/**`
- `tests/Contexo.Core.Tests/Tables/**`

## 實作要點與已知陷阱

- 每個記憶體資料庫都是獨立連線；快取中的連線不可同時被兩個查詢使用（每張表一把 `SemaphoreSlim`）。
- 中文欄名在 SQLite 中以雙引號包住即可使用。
- 測試使用 T08 的 `SpreadsheetRegionReader` 與測試中產生的 xlsx，或使用假的 `ISpreadsheetRegionReader`。

## 驗收條件

`dotnet test --filter FullyQualifiedName~Tables` 全部通過，至少涵蓋：

1. 描述：欄位名稱、型別推斷（數字含千分位與 `%`、日期、文字）、列數、範例列正確。
2. 查詢：`SELECT "客戶", SUM("金額") FROM t GROUP BY "客戶" ORDER BY 2 DESC` 結果正確。
3. 拒絕：`DELETE FROM t`、`DROP TABLE t`、`SELECT 1; DELETE FROM t`、`ATTACH DATABASE …`、`PRAGMA …`；全部拋 `TableQueryException`，且資料未改變。
4. `maxRows` 截斷與 `Truncated`。
5. 錯誤欄名 → 訊息含可用欄位清單。
6. 不存在的 tableId、原始檔被刪除 → 對應訊息。
7. 快取：60 秒內第二次查詢不重新讀檔（以假的 reader 計數）；原始檔修改後重新讀取。
8. 10 萬列 × 10 欄的表首次載入 < 5 秒、之後查詢 < 500ms（完成紀錄寫下實測）。

## 完成紀錄

**做了什麼**

- `src/Contexo.Core/Tables/` 實作 `TableQueryService`（`ITableQueryService`，同時實作 `IDisposable`，DI 關閉時釋放所有記憶體資料庫）。未改動共用契約與範圍外檔案；DI 註冊原本就有。
- 檔案分工：`TableLoader.cs`（讀原始檔、建 `t` 表、型別推斷、批次插入、`LoadedTable`、`FileStamp`）、`TableValueParser.cs`（數字／日期辨識、SQL 欄名）、`SqlGuard.cs`（唯讀 SQL 檢查）、`TableQueryService.cs`（快取、Describe、Query）。
- 測試在 `tests/Contexo.Core.Tests/Tables/`：`TableQueryServiceTests`（假的 store／reader，含 10 萬列效能）、`TableValueParserTests`、`TableQueryFromXlsxTests`（真的 `SpreadsheetParser` + `SpreadsheetRegionReader` 讀 xlsx 與 csv）、`TableTestSupport`。

**驗收結果**

- `dotnet build Contexo.slnx -warnaserror`：0 警告、0 錯誤。
- `dotnet test --filter FullyQualifiedName~Tables`：102 個全數通過。全方案 `dotnet test`：Core 654 通過（14 略過，為需要模型的既有測試）、Mcp 5、App 25 通過；Desktop 有 1 個失敗 `SingleInstanceTests.Can_be_woken_more_than_once`，在乾淨的 4454bd0 上同樣失敗（T15 既有問題，與本任務無關）。
- 逐項：1 描述（欄位、型別、列數、範例列、千分位／`%`／貨幣／括號負數／日期／文字）；2 GROUP BY + SUM + ORDER BY 結果正確（假資料與真 xlsx 各一）；3 拒絕 `DELETE`、`DROP`、`SELECT 1; DELETE`、`ATTACH`、`PRAGMA`、`load_extension`、INSERT／UPDATE、未結束的引號、空白 SQL，皆拋 `TableQueryException`，之後 `COUNT(*)` 不變；另測 `WITH x AS (SELECT 1) DELETE FROM t` 通過關鍵字檢查但被 `PRAGMA query_only` 擋下；4 `maxRows` 截斷、剛好等於時不截斷、上限 500；5 錯誤欄名訊息含 `no such column` 與可用欄位清單（含型別）；6 不存在的 tableId、原始檔刪除；7 快取（60 秒內不重讀、第 60 秒後重讀、改檔案修改時間後重讀、最多 4 張 LRU）；8 效能。
- 實測效能（macOS，Debug 組態，10 萬列 × 10 欄，含千分位數字、百分比、日期、文字欄）：首次載入（含讀取、推斷、插入）約 370～430 ms；之後 GROUP BY 查詢約 20 ms。
- 失控查詢（無限遞迴 CTE）在 5021 ms 停止，丟 `TableQueryException`（訊息含「5 秒」），連線之後仍可使用；取消權杖也能中斷查詢。

**無法在此環境驗證**

- 全部在 macOS 執行；沒有 Windows 專屬程式碼，但未在 Windows 實測。

**與規格不同的地方及理由**

1. `CommandTimeout` 在 Microsoft.Data.Sqlite 只管等鎖，不會中止跑太久的查詢。因此除了設定 `CommandTimeout = 5` 外，另用 `CancellationTokenSource`（5 秒）＋ `sqlite3_interrupt` 實際中斷。
2. SQL 關鍵字檢查前，先把字串常值、引號識別字（`"..."`、`[...]`、反引號）與註解遮蔽，所以 `SELECT 'pragma'`、欄名叫 `"pragma"` 不會被誤擋；分號判斷同理（字串裡的分號可用）。
3. 型別推斷細節（規格未寫）：百分比存「顯示的數字」（`25.6%` 存 25.6，不除以 100，AI 看到的範例值與 Excel 畫面一致）；有前導零的整數（`007`、`0912345678`）視為文字（編號、電話）；`(1,200)` 視為 -1200；逗號分組必須是標準三位一組；`1e5` 不視為數字；全空欄位為文字。`InferredType` 的值為 `number`／`date`／`text`。
4. 額外防護：連線設 `SQLITE_LIMIT_LENGTH` 16 MB，避免 `zeroblob(2000000000)` 耗盡記憶體；欄名空白時命名 `column_{序號}`。
5. 快取的 60 秒從載入時算起（不是從最後使用算起），且每次呼叫都會向資料庫重新取得表格記錄（可偵測表格被移除或範圍改變）；快取中若 `FilePath／Sheet／CellRange／HeaderRowCount` 與記錄不同也視為失效。過期的拷貝在下一次任何呼叫時才釋放（沒有計時器）。
6. 例外對應：reader 丟 `FileNotFoundException`／`DirectoryNotFoundException` → 「原始檔案已移動或刪除：{檔名}」；`InvalidOperationException`（範圍或工作表不存在）→ 「檔案內容已經改變…請重新搜尋」；`DocumentParseException` 與 IO／權限錯誤 → 各有白話訊息。取消則照常丟 `OperationCanceledException`。

**留給後續任務的注意事項（T13）**

- `TableQueryService` 是 `internal sealed`，由 DI 以 `ITableQueryService` 取得。建構式需要 `IKnowledgeStore`、`ISpreadsheetRegionReader`、`ILogger<TableQueryService>`（`TimeProvider` 選用）。MCP 程序也要有 `AddContexoCore()` 註冊的這些服務。
- 可直接把 `TableQueryException.Message` 回給 AI 客戶端（已是白話，且 SQL 錯誤訊息含可用欄位清單）。其他例外（例如資料庫無法開啟）不是 `TableQueryException`，T13 應自行轉成通用錯誤。
- `describe_table` 的 `Columns` 提供 `SqlName`（查詢時要用，已安全，建議仍以雙引號包住）、`Header`（原欄名）、`InferredType`（`number`／`date`／`text`）。日期欄以 `yyyy-MM-dd HH:mm:ss` 文字儲存，可直接用 `BETWEEN`、`strftime`、字串比較。
- 所有輸出值都是字串或 null；`maxRows` 上限 500，`sampleRows` 上限 20。第一次查詢大表要讀整個範圍（10 萬列約 0.4 秒），之後 60 秒內很快。
- 日誌只記錄 tableId、檔名、列數、欄數、耗時，不含 SQL 與資料內容；T13 的 MCP 活動紀錄（`McpActivity.Detail`）請同樣不要放 SQL 全文。
