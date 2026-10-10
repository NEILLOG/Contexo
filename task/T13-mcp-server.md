# T13 MCP Server

- **狀態**：完成
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

**做了什麼**

- `src/Contexo.Mcp/Program.cs`：啟動最前面 `Console.SetOut(Console.Error)`；`AddContexoCore()` 之後 `AddMcpServer(ServerInfo: contexo / AppVersion).WithStdioServerTransport().WithTools<ContexoTools>()`，並用 incoming message filter 記錄連線；啟動時呼叫一次 `InitializeAsync`（失敗不終止程序）；`host.RunAsync()`，stdin 關閉即結束（結束碼 0）。
- `Tools/ContexoTools.cs`：三個工具 `search`、`describe_table`、`query_table`（英文描述，註明用使用者語言回覆；參數 `query`、`top_k`、`table_id`、`sql`、`max_rows` 為 snake_case）。每次呼叫記錄 ToolCall，錯誤再記 Error。
- `Tools/ContexoToolService.cs`：不依賴 MCP SDK 的工具邏輯（方便測試）。參數夾限（top_k 1～20、max_rows 1～500）、白話錯誤、資料庫空的提示、資料庫打不開的提示（每次呼叫重試開啟）、內嵌表格防護（見下）。
- `Tools/ResultFormatter.cs`：搜尋結果純文字、describe 與 query 的 Markdown 表格（儲存格內的 `|` 與換行已跳脫）、1500 字截斷（不切斷代理對）。
- `Tools/LocationText.cs`：位置文字，內容刻意與 `Contexo.App.Search.LocationText` 完全相同（Mcp 不引用 App，所以複製一份）；若日後改其中一份，請同步另一份。
- `Activity/McpActivityRecorder.cs`：寫入 `McpActivity`，逾時 5 秒、任何失敗只寫日誌（只含種類與例外型別）。
- 測試（`tests/Contexo.Mcp.Tests/`）：`Tools/ContexoToolServiceTests.cs`（服務層，真的 SqliteKnowledgeStore、HybridSearchService、TableQueryService、SpreadsheetRegionReader）、`McpEndToEndTests.cs`（以官方 SDK `StdioClientTransport` 啟動子行程）、`McpStartupTests.cs`（原始程序：手寫 JSON-RPC，逐行驗證 stdout）、`Support/`（程式產生的暫存資料庫與文件）。原本的 `McpStartupTests` 因為伺服器現在會等 stdin，改為先關閉 stdin。

**驗收結果（macOS，.NET 10）**

- `dotnet build Contexo.slnx -warnaserror`：0 警告、0 錯誤。
- `dotnet test --filter FullyQualifiedName~Contexo.Mcp.Tests`：49 通過、0 失敗。
- 全方案 `dotnet test`：App 294、Core 926（15 略過）、Mcp 49 通過；Desktop 120 通過，另有 1 個 `SingleInstanceTests.Can_be_woken_more_than_once`（T15，時間相關）第一次失敗，單獨重跑 7/7 通過，與本任務無關。
- 驗收 1：initialize 後 `serverInfo.name == "contexo"`；`tools/list` 為三個工具，schema 屬性 `query/top_k`、`table_id`、`max_rows/sql/table_id`，必填欄位正確。
- 驗收 2：格式含檔名、位置、路徑、內容；大型表格附 `table_id: tNN（…）`；開頭有 `（目前只使用關鍵字比對）`（測試環境沒有模型，`Degraded` 為 true）；超過 1500 字截斷並註明原文字數。
- 驗收 3：`describe_table` 與 `query_table` 正常結果（GROUP BY 合計與 COUNT 與手算一致）；`DELETE`、`DROP`、錯誤欄名、非 SQL 皆為 `isError: true` 且訊息可讀（錯誤欄名會列出可用欄位），之後同一個程序的下一次呼叫仍正常。
- 驗收 4：空資料庫與「不存在的資料庫路徑（連上層資料夾都不存在，會建立空資料庫）」三個工具都回「Contexo 還沒有收錄任何資料…」；路徑是資料夾（SQLite 打不開）時回 `isError` 與「目前無法讀取 Contexo 的資料…」，伺服器不崩潰。
- 驗收 5：`clientInfo` 的名稱與版本記錄為 Connected；每次工具呼叫一筆 ToolCall（只有 ToolName）；錯誤一筆 Error。測試直接讀 `mcp_activity` 資料表與日誌檔，確認沒有查詢文字、SQL、欄位名。
- 驗收 6：原始程序，送 initialize、initialized、tools/list、tools/call，stdout 每一行都是 `jsonrpc: "2.0"` 的合法 JSON；另測沒有任何請求就關閉 stdin 時 stdout 完全為空、結束碼 0。
- 驗收 7：見下方「無法在目前環境驗證」。

**SDK 與 stdout 的確認結果**

- 使用 `ModelContextProtocol` 2.2.0。stdio transport 實際上以原始串流輸出協定，所以 `Console.SetOut(Console.Error)` 之後協定訊息仍正常出現在 stdout（所有 stdio 測試通過即為證明），而誤用 `Console.WriteLine` 的程式碼會被導到 stderr。
- 注意：2.2.0 的 client 預設協商的協定版本是 `2026-07-28`，該版本**沒有 initialize／initialized 握手**，每個請求自己帶 clientInfo。舊版協定（例如 2025-06-18）仍走握手。因此「Connected」的記錄時機是：收到 `notifications/initialized`，或（新協定）第一個不是 initialize／ping 且已知 clientInfo 的請求；工具呼叫時也會補記（每個程序只記一次）。兩條路徑都有測試。

**內嵌表格（已知缺口）實際觀察到的行為**

- T10 把內嵌在 pptx／docx 裡的 Excel 大表登記成可查詢表格（`TableKey` 形如 `內嵌.xlsx#Sheet1!A1:F20`，`FilePath` 是外層檔案）。直接把這種表格交給 `TableQueryService`，它會拿外層檔案當試算表讀，丟出 `TableQueryException`：「無法讀取原始檔案：簡報.pptx（檔案不是有效的試算表。）」，語意誤導（檔案是好的）。外層檔案不存在時則是「原始檔案已移動或刪除」。沒有未處理例外、程序也不會當掉，但訊息對 AI 沒有幫助。
- T13 的處理：呼叫 T12 之前先用 `IKnowledgeStore.GetExcelTableAsync` 判斷（`TableKey` 不等於 `{工作表}!{範圍}`，或檔案副檔名不是 .xlsx／.xlsm／.csv）。是的話 `describe_table`／`query_table` 回 `isError`：「這張表格是內嵌在「簡報.pptx」裡的 Excel 表格，目前無法用 describe_table / query_table 查詢。請改用搜尋結果裡已經顯示的文字內容回答；如果需要完整計算，可以請使用者把內嵌的 Excel 另存成獨立的檔案，放進已加入 Contexo 的資料夾。」。`search` 結果對這類表格也不再提示 `table_id`，改成說明無法查詢，避免 AI 白跑一趟。測試涵蓋服務層與端對端。
- 根本修正（T12 讀得到內嵌檔，或 T10 不登記內嵌表格）不在 T13 範圍。

**無法在目前環境驗證**

- 沒有用真實的 Claude Desktop、VS Code、Cursor 或 MCP Inspector 連線；以官方 SDK client（新協定）與手寫 JSON-RPC（舊協定 2025-06-18）代替。**待確認**：用 Claude Desktop 實際連線一次，確認工具出現、描述讀得懂、`clientInfo.name`（T14 的狀態偵測靠它）與桌面程式顯示一致。
- 沒有 Windows 實測（`Contexo.Mcp.exe` 啟動、路徑含空白或中文、Windows 路徑在輸出中的樣子）。
- 沒有真實 bge 模型：測試都是關鍵字模式（`Degraded` 為 true）。語意模式只是 `HybridSearchService` 的內部行為，T13 沒有分支；第一次搜尋會載入模型與向量，大型資料庫的首次延遲未量測。

**與規格不同或規格未明之處**

- 多了一個 `ContexoToolService` 層（規格只列 `ContexoTools.cs`），為了不靠 MCP 程序就能測。
- 搜尋沒有結果時，若資料庫沒有任何內容（`ChunkCount == 0`）回「還沒有收錄任何資料」，否則回「沒有找到相關內容」。表格工具遇到 `TableQueryException` 且資料庫是空的，也回前者。
- 資料庫打不開時（規格未寫）回白話錯誤，並在下一次呼叫重試。
- `Error` 活動的 `Detail` 為「例外類型：固定的白話句子」或固定分類，不使用例外訊息（SQLite 的訊息可能引用 SQL 片段）。日誌也只記錄例外類型。
- 搜尋結果開頭多一行「找到 N 筆相關內容：」（規格範例有）；`Degraded` 為 true 時再在最前面加一行。沒有結果時不加降級提示，維持規格的固定句子。

**給後續任務的注意事項**

- T21（安裝／打包）：Mcp 啟動需要 `Contexo.Mcp.runtimeconfig.json` 與全部相依 DLL 在同一資料夾；模型資料夾由 `--models` 或 `CONTEXO_MODELS_DIR` 指定，預設為 `{AppContext.BaseDirectory}\models`。
- T22（品質評估）：`search` 的文字格式在 `ResultFormatter.Search`；每筆最多 1500 字。需要看 `Score` 或 `MatchedBy` 的話目前不輸出（AI 不需要）。
- T14／T18：Connected 的 `ClientName` 是 `clientInfo.name` 原樣；用戶端沒有名稱時記為 `unknown`。
- 內嵌表格的缺口見上；修好之後把 `ContexoToolService.IsEmbedded` 的防護拿掉即可，測試 `An_embedded_table_cannot_be_queried_and_the_message_says_so` 需要同步更新。

