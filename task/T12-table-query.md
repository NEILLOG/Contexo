# T12 Excel 表格查詢

- **狀態**：待辦
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

（由執行者填寫）
