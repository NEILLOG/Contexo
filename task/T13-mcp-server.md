# T13 MCP Server

- **狀態**：待辦
- **波次**：3
- **相依**：T11、T12（以及 T02、T03）
- **必讀**：`AGENTS.md`（尤其 stdout 規則）、`plan/01-architecture.md`（程序與生命週期、stdio）、`plan/03-retrieval-and-mcp.md`、`src/Contexo.Core/Abstractions/Search.cs`、`Storage.cs`

## 目標

完成 `Contexo.Mcp`：由 AI 軟體以 stdio 啟動，提供 `search`、`describe_table`、`query_table` 三個工具，並記錄連線與查詢活動。

## 要做

### 1. 程式進入點（`src/Contexo.Mcp/Program.cs`，T01 已建骨架）

- 參數：`--db <path>`、`--models <path>`（皆可省略，預設用 `IAppPaths`）。
- `services.AddContexoCore()`，再用官方 SDK：`AddMcpServer()`、`WithStdioServerTransport()`、註冊工具類別。
- 伺服器資訊：name `contexo`、version 取 `AppVersion`。
- 啟動時呼叫 `IKnowledgeStore.InitializeAsync`。資料庫檔不存在時**不要建立空資料庫以外的東西**，工具呼叫時回傳白話提示（見下）。
- **stdout 保護**：先確認 SDK 的 stdio transport 是透過 `Console.OpenStandardOutput()` 取得原始串流（查看 SDK 原始碼或文件），確認後在啟動最前面執行 `Console.SetOut(Console.Error)`，防止任何程式碼意外用 `Console.WriteLine` 破壞協定。若 SDK 實際上使用 `Console.Out`，則不要重導，改為在完成紀錄說明並以測試確保沒有雜訊輸出。

### 2. 工具（`src/Contexo.Mcp/Tools/ContexoTools.cs`）

工具描述要讓 AI 知道**什麼時候該用**。描述用英文撰寫（各家模型都能理解），並註明回覆時使用使用者的語言。

**`search`**

- 參數：`query`（string，必填）、`top_k`（int，預設 8，範圍 1～20）。
- 描述重點：搜尋使用者電腦上由 Contexo 收錄的文件（Word、PowerPoint、Excel、PDF、文字檔）；當使用者問到自己的檔案、公司資料、報價、規格、會議紀錄等內容時使用；結果附有來源，回答時要引用檔名與位置；若結果含 `table_id`，可用 `describe_table` 與 `query_table` 做計算。
- 回傳：純文字，格式如下（AI 最容易閱讀與引用）：

```
找到 3 筆相關內容：

[1] 2025_台中案_報價單.xlsx — 工作表「報價明細」A1:F24
路徑：C:\Users\…\2025_台中案_報價單.xlsx
內容：
品名：監視系統建置；數量：1 式；…

[2] …
table_id: t42（這是大型表格，可用 describe_table / query_table 查詢完整資料）
```

  位置描述依 `SourceLocation`：第 N 頁、第 N 張投影片「標題」、工作表「名稱」範圍、章節路徑、內嵌檔路徑。單筆內容超過 1500 字時截斷並註明。
- `Degraded` 為 true 時在開頭加一行「（目前只使用關鍵字比對）」。
- 沒有結果：「沒有找到相關內容。可以換個說法，或確認檔案所在的資料夾已加入 Contexo。」

**`describe_table`**

- 參數：`table_id`（string）。
- 回傳：檔名、工作表、範圍、列數、欄位表（SQL 名稱、原欄名、型別）、前 5 列範例，以及一行提示「查詢時資料表名稱為 t，欄名請用雙引號」。

**`query_table`**

- 參數：`table_id`、`sql`、`max_rows`（預設 100，上限 500）。
- 描述註明：只允許單一 SELECT，資料表名稱固定為 `t`。
- 回傳：Markdown 表格；`Truncated` 時註明只顯示前 N 列。

**錯誤處理**：`TableQueryException` 與預期錯誤以工具結果（`isError: true`）回傳白話訊息，不讓整個請求失敗。資料庫不存在或沒有任何資料時：「Contexo 還沒有收錄任何資料。請先開啟 Contexo，加入要讓 AI 讀取的資料夾。」

### 3. 活動紀錄

- 初始化完成後（能取得 `clientInfo` 的時機，依 SDK 提供的 API：例如伺服器物件的 `ClientInfo` 屬性或初始化事件），記錄 `McpActivity(Connected, name, version)`。若 SDK 沒有初始化事件，就在第一次工具呼叫時補記錄 Connected。
- 每次工具呼叫記錄 `ToolCall`（`ToolName`，**不記錄查詢內容**）；例外記錄 `Error`（`Detail` 為例外類型與白話訊息，不含查詢內容）。
- 紀錄失敗（例如資料庫被鎖住太久）不得影響工具結果，只寫日誌。

### 4. 資源使用

- Embedding 模型延遲載入（T03 已實作），第一次 `search` 才載入。
- 向量快取由 `HybridSearchService` 管理。

## 不做

- HTTP transport。
- 寫入或索引功能（Mcp 只讀，唯一的寫入是活動紀錄）。

## 可修改範圍

- `src/Contexo.Mcp/**`
- `tests/Contexo.Mcp.Tests/**`

## 實作要點與已知陷阱

- `ModelContextProtocol` SDK 仍在演進，**以 T01 鎖定版本的 README 與範例為準**，不要照網路上的舊範例。
- 工具方法參數名稱會成為 JSON schema 的欄位名，使用 `snake_case`（用屬性或參數命名設定）。
- 測試：在 `Contexo.Mcp.Tests` 用 SDK 的 client（`StdioClientTransport`）啟動建置出來的 `Contexo.Mcp`（`dotnet <dll路徑>` 或 exe），資料庫用測試前以 `SqliteKnowledgeStore` 寫入的暫存檔，embedding 用不存在的模型路徑（測試關鍵字路徑）。

## 驗收條件

`dotnet test --filter FullyQualifiedName~Contexo.Mcp.Tests` 全部通過，至少涵蓋：

1. 啟動並完成 initialize；`tools/list` 列出三個工具，參數 schema 正確（`top_k`、`table_id`、`max_rows` 為 snake_case）。
2. `search` 回傳格式符合規格，含檔名、位置、路徑；大型表格結果附 `table_id`。
3. `describe_table`、`query_table` 正常結果；不合法 SQL 回傳 `isError` 與可讀訊息，伺服器繼續可用。
4. 空資料庫與不存在的資料庫路徑都回傳白話提示，不崩潰。
5. 活動紀錄：測試 client 的 `clientInfo.name` 與版本被記錄為 Connected；每次工具呼叫有 ToolCall 紀錄；紀錄中沒有查詢文字。
6. **stdout 純淨**：以原始程序方式啟動 `Contexo.Mcp`，送出 initialize 後讀取 stdout，每一行都必須是合法 JSON-RPC 訊息。
7. 在完成紀錄寫下：用 Claude Desktop（或 MCP Inspector）實際連線的結果，若環境無法執行則列為待確認項目。

## 完成紀錄

（由執行者填寫）
