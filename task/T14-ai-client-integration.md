# T14 AI 軟體設定整合與狀態

- **狀態**：完成
- **波次**：2
- **相依**：T02
- **必讀**：`AGENTS.md`、`plan/03-retrieval-and-mcp.md`（加入 AI 軟體、連線狀態偵測）、`src/Contexo.Core/Abstractions/Integrations.cs`、`Storage.cs`（MCP activity）

## 目標

讓使用者按一個按鈕就能把 Contexo 加進 AI 軟體，並依證據強度判斷「有沒有真的接上」。

## 要做

### 1. 各 AI 軟體的整合（`Integrations/*Integration.cs`）

**動手前先查官方文件確認目前的設定檔位置與格式**，在完成紀錄附上參考網址與查詢日期。下表是規劃時的已知資訊：

| 類別 | ClientId | 設定檔（Windows；macOS 見下方） | 根鍵 | 項目格式 |
|---|---|---|---|---|
| `ClaudeDesktopIntegration` | `claude-desktop` | `%APPDATA%\Claude\claude_desktop_config.json` | `mcpServers` | `{ "command": …, "args": [ … ] }` |
| `VsCodeIntegration` | `vscode` | `%APPDATA%\Code\User\mcp.json` | `servers` | `{ "type": "stdio", "command": …, "args": [ … ] }` |
| `CursorIntegration` | `cursor` | `%USERPROFILE%\.cursor\mcp.json` | `mcpServers` | `{ "command": …, "args": [ … ] }` |
| `LmStudioIntegration` | `lm-studio` | `%USERPROFILE%\.lmstudio\mcp.json` | `mcpServers` | `{ "command": …, "args": [ … ] }` |

項目名稱一律為 `"contexo"`。

**macOS（開發驗證用）**：同樣要支援，讓開發者能在 Mac 上用 Claude Desktop 等軟體實際連到 Contexo。已知位置（同樣需查文件確認）：Claude Desktop `~/Library/Application Support/Claude/claude_desktop_config.json`、VS Code `~/Library/Application Support/Code/User/mcp.json`、Cursor `~/.cursor/mcp.json`、LM Studio `~/.lmstudio/mcp.json`。是否已安裝：除設定檔資料夾外，檢查 `/Applications/{名稱}.app`。各整合以 `OperatingSystem.IsWindows()` / `IsMacOS()` 選擇路徑表。

**是否已安裝**（`NotInstalled` 判斷）：設定檔所在資料夾存在，或已知的安裝位置存在（例如 `%LOCALAPPDATA%\AnthropicClaude`、`%LOCALAPPDATA%\Programs\Microsoft VS Code`、`%LOCALAPPDATA%\Programs\cursor`）。查文件時一併確認。

**`GetConfigState`**：

- 未安裝 → `NotInstalled`
- 檔案不存在，或沒有 `contexo` 項目 → `NotConfigured`
- JSON 無法解析、`command` 指向的檔案不存在、或 `command` 與目前的 `IAppPaths.McpExecutablePath` 不同（不分大小寫）→ `Broken`
- 其餘 → `Configured`

**`AddOrRepair`**：

1. 檔案存在但 JSON 無法解析 → **不覆寫**，拋 `InvalidOperationException("設定檔格式有誤，為避免破壞你原本的設定，Contexo 沒有修改它。")`。
2. 用 `System.Text.Json.Nodes` 讀取（`JsonCommentHandling.Skip`、`AllowTrailingCommas`），只新增或取代 `contexo` 項目，其他內容保留。
3. 先複製原檔為 `*.contexo.bak`（覆寫舊的備份），再以暫存檔＋取代的方式寫入，縮排 2 格。
4. 資料夾不存在時建立。

**`Remove`**：只移除 `contexo` 項目；檔案不存在或沒有項目時不做事。

**`BuildManualSnippet`**：輸出可直接貼進該設定檔的完整 JSON 片段（含根鍵）。

**`KnownClientNames`**：各 AI 軟體在 MCP `initialize` 時送出的 `clientInfo.name`（小寫）。規劃時不確定實際值，請查文件或原始碼；查不到時先填最可能的值（例如 Claude Desktop 為 `claude-ai`），並在完成紀錄註明「待 T22 實測確認」。

測試性：路徑根目錄（AppData、LocalAppData、UserProfile）由 `ClientPathOptions` 注入，預設取 `Environment.GetFolderPath`；測試時指向暫存資料夾。

### 2. 狀態服務（`Integrations/AiClientStatusService.cs`）

`GetStatusesAsync`：對每個整合，結合設定檔狀態與 `IKnowledgeStore.GetMcpActivitySummariesAsync`：

| 設定狀態 | 活動紀錄（`ClientName` 小寫後屬於 `KnownClientNames`） | 結果 |
|---|---|---|
| NotInstalled | — | `NotInstalled` |
| NotConfigured | — | `NotAdded` |
| Broken | — | `NeedsRepair`，`Problem` 寫白話原因 |
| Configured | 沒有連線紀錄 | `WaitingForConnection` |
| Configured | 最近一次錯誤晚於最近一次連線 | `NeedsRepair`，`Problem` =「上次連線時發生錯誤」 |
| Configured | 有連線紀錄 | `Connected`，帶 `LastConnectedAt`、`LastQueryAt` |

`KnownClientNames` 對不上任何整合的活動紀錄，依名稱是否包含 `claude`、`code`、`cursor`、`lm studio`／`lmstudio` 歸類；仍對不上就忽略。

`Integrations` 依顯示順序：Claude Desktop、VS Code、Cursor、LM Studio。`CurrentLaunch`：`McpExecutablePath` 與參數 `["--db", DatabasePath]`。

T01 的 `AiClientStatusService` stub 是「可安全執行」版本，請整個取代為正式實作。

## 不做

- UI（T18）。
- 安裝程式或解除安裝時的清理呼叫（T21 會呼叫 `Remove`）。

## 可修改範圍

- `src/Contexo.Core/Integrations/**`
- `tests/Contexo.Core.Tests/Integrations/**`

## 實作要點與已知陷阱

- VS Code 的 `mcp.json` 允許註解；讀寫後註解會消失。這是可接受的取捨（有 `.bak`），在完成紀錄註明。
- 設定檔可能被 AI 軟體正在寫入：寫入失敗（IOException）時重試 3 次，每次間隔 200ms，仍失敗就拋出白話錯誤。
- 不要在 JSON 中寫入使用者看不懂的額外欄位。
- 路徑中的反斜線由 JSON 序列化器處理，不要手動跳脫。

## 驗收條件

`dotnet test --filter FullyQualifiedName~Integrations` 全部通過，至少涵蓋（每個整合都要測）：

1. 未安裝、未設定、已設定、路徑失效、路徑與目前不同、JSON 損壞，`GetConfigState` 正確（Windows 與 macOS 兩套路徑表各測一次，以注入的根目錄模擬）。
2. `AddOrRepair`：原本有其他 server 與其他設定的檔案，加入後全部保留；`.bak` 產生；重複呼叫結果相同。
3. JSON 損壞時 `AddOrRepair` 拋例外且檔案內容不變。
4. `Remove` 只移除 `contexo`。
5. 含註解與尾逗號的 VS Code 設定檔可以讀取並加入。
6. 狀態服務：六種組合的結果正確；名稱模糊比對有效。
7. `BuildManualSnippet` 輸出為合法 JSON，且根鍵正確。

## 完成紀錄

### 做了什麼

- `Integrations/JsonMcpClientIntegration.cs`（新增，internal 抽象基底）：四個整合共用的讀取、判斷、寫入邏輯。寫入流程：`System.Text.Json.Nodes` 讀取（略過註解、允許尾逗號）→ 只新增/取代 `contexo` → 先複製 `*.contexo.bak`（覆寫舊備份）→ 寫 `*.contexo.tmp` 再以 `File.Move(overwrite)` 取代 → 縮排 2 格、中文不跳脫。IOException/UnauthorizedAccessException 重試 3 次（間隔 200ms），仍失敗拋 `InvalidOperationException`（白話訊息）。JSON 損壞或根節點／`mcpServers`（`servers`）型別不對時拋 `InvalidOperationException("設定檔格式有誤，為避免破壞你原本的設定，Contexo 沒有修改它。")`，檔案與備份都不動。
- `ClaudeDesktopIntegration`、`VsCodeIntegration`、`CursorIntegration`、`LmStudioIntegration`：各自的路徑表（Windows、macOS）、根鍵、安裝判斷、`KnownClientNames`。VS Code 項目含 `"type": "stdio"`。
- `ClientPathOptions`（新增，public）：`Platform`、`AppData`、`LocalAppData`、`UserProfile`、`ApplicationsDirectory`，預設取目前系統；測試指向暫存資料夾。整合類別建構式為 `(IAppPaths, ClientPathOptions? = null)`，DI 不需註冊 `ClientPathOptions`（用預設值）。
- `AiClientStatusService`：取代 T01 stub，建構式 `(IAppPaths, IEnumerable<IAiClientIntegration>, IKnowledgeStore)`；依 Claude Desktop、VS Code、Cursor、LM Studio 排序；依規格表產生狀態，同一軟體的多個名稱活動紀錄合併（取最新時間）。
- 測試：刪除 `AiClientStatusServiceStubTests.cs`（stub 專用），新增 `IntegrationHarness.cs`、`ClientIntegrationTests.cs`、`AiClientStatusServiceTests.cs`（共 179 個案例，涵蓋 4 個軟體 x Windows/macOS 兩套路徑表）。全部使用暫存資料夾，不碰真實使用者設定。

### 驗收結果

- `dotnet test --filter FullyQualifiedName~Integrations`：通過 179、失敗 0。
- `dotnet build Contexo.slnx -warnaserror`：0 警告 0 錯誤。
- `dotnet test` 全部：Core 732 通過、App 25、Mcp 5 皆通過；Desktop.Tests 的 `SingleInstanceTests.Can_be_woken_more_than_once` 在整批執行時失敗一次（重跑整個專案仍失敗），單獨跑該類別 7/7 通過；此測試與本任務無關（單一執行個體的喚醒機制，可能與同機其他程序競爭），請派工者留意。

### 官方文件出處（查詢日期 2026-10-09）

- Claude Desktop：https://modelcontextprotocol.io/docs/develop/connect-local-servers — macOS `~/Library/Application Support/Claude/claude_desktop_config.json`、Windows `%APPDATA%\Claude\claude_desktop_config.json`，根鍵 `mcpServers`，項目 `command`/`args`（官方文件已確認）。
- VS Code：https://code.visualstudio.com/docs/copilot/customization/mcp-servers 與 https://code.visualstudio.com/docs/agents/reference/mcp-configuration — 根鍵 `servers`，stdio 項目 `type`/`command`/`args`（官方）；使用者層級檔案以「MCP: Open User Configuration」開啟，**官方頁面沒寫各系統的實際路徑**。路徑 `%APPDATA%\Code\User\mcp.json`、`~/Library/Application Support/Code/User/mcp.json` 來自第三方安裝指南（多個來源一致，另有一份 IBM 文件寫成 globalStorage 下，視為錯誤）。註解是否允許官方未說明（VS Code 的 JSON 檔一般允許），我們一律以略過註解模式讀取。
- Cursor：https://cursor.com/docs/context/mcp（原 docs.cursor.com 轉址）— 全域 `~/.cursor/mcp.json`，根鍵 `mcpServers`，`command`/`args`（官方）；文件範例省略 `type`。
- LM Studio：https://lmstudio.ai/docs/app/mcp — 根鍵 `mcpServers`、沿用 Cursor 記法；**官方文件只說用 App 內「Install > Edit mcp.json」開啟，沒有寫路徑**。`~/.lmstudio/mcp.json`（Windows `%USERPROFILE%\.lmstudio\mcp.json`）依據 LM Studio 官方 bug tracker 的 issue（https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/1371），該 issue 還指出 macOS 實際曾出現 `~/.cache/lm-studio/mcp.json`，待實機確認。

### KnownClientNames 的依據（皆待 T22 實測確認）

找不到任何官方清單列出各軟體 `initialize` 送出的 `clientInfo.name`。
- Claude Desktop：`claude-ai`（規格建議值，第三方程式碼亦可見）、`claude desktop`、`claude-desktop`。
- VS Code：`visual studio code`、`vscode`、`vscode-mcp-client`（推測）。
- Cursor：`cursor-vscode`、`cursor`（推測）。
- LM Studio：`lm-studio`、`lm studio`、`lmstudio`（推測）。
名稱對不上時使用模糊比對（見下）作為保底，所以即使上述猜測有誤，多半仍可歸類。

### 與規格不同或補充之處

- 模糊比對順序為 cursor → lm studio → claude → code。原因：`cursor-vscode` 含 `code`，若先比 `code` 會誤判成 VS Code。另外 `claude code`／`claude-code`（Claude Code 命令列工具，不是 Claude Desktop）刻意**不**歸給 Claude Desktop，直接忽略，否則使用者用 Claude Code 時 Claude Desktop 會被誤顯示為已連線。
- 設定狀態規則補充：只有錯誤紀錄、沒有連線紀錄時，視為「最近一次錯誤晚於最近一次連線」，顯示需要修復。
- `GetConfigState` 的 `Broken` 只依規格判斷（`command` 檔案存在且與目前 `McpExecutablePath` 相同），**不檢查 `args` 裡的 `--db` 路徑**；資料庫位置變更時請呼叫 `AddOrRepair` 重寫。
- 設定檔無法讀取（被鎖住、權限）時 `GetConfigState` 回傳 `Broken`；狀態服務的 `Problem` 另有白話說明。
- 空檔案（只有空白）視為空物件，`AddOrRepair` 可直接寫入。
- `Remove` 遇到格式損壞的檔案會拋 `InvalidOperationException`（不動檔案），呼叫端（例如 T21 解除安裝）需自行 try/catch。
- `Remove` 只在確實有 `contexo` 項目時才寫檔並產生 `.bak`；移除後即使 `mcpServers` 變成空物件也保留。
- 修復時整個 `contexo` 項目會被取代（若使用者手動加過 `env` 等欄位會被移除，此為規格行為）。
- `Other`（非 Windows／macOS，例如 CI 的 Linux）平台一律回報未安裝，`AddOrRepair` 拋例外；Linux 不是產品平台。
- VS Code 設定檔的註解在改寫後會消失（有 `.contexo.bak` 可還原），如任務檔所述。

### 無法在目前環境驗證

- 真實 Windows 路徑（`%APPDATA%`、`%LOCALAPPDATA%` 安裝位置如 `AnthropicClaude`、`Programs\Microsoft VS Code`、`Programs\cursor`、`Programs\LM Studio`）：僅以注入的暫存根目錄模擬，需在 Windows 實機確認。Claude Desktop 新版若改用 MSIX（`%LOCALAPPDATA%\Packages\...`）安裝，設定檔資料夾判斷仍可作為保底。
- 與真實 AI 軟體的實際連線、`clientInfo.name` 真實值、VS Code 與 LM Studio 的實際設定檔位置：待 T22 實測。
- 檔案被其他程式鎖定時的重試，僅用本程序內的 `FileShare.None` 模擬。

### 給後續任務（T18）的注意事項

- 所有 `AddOrRepair`／`Remove` 都是同步呼叫且可能睡眠最多約 400ms（重試），請在背景執行緒呼叫；例外訊息 (`InvalidOperationException.Message`) 已是繁體中文白話，可直接顯示。
- 狀態 `Problem` 已是白話；`NeedsRepair` 時的修復動作就是呼叫 `AddOrRepair(CurrentLaunch)`。
- 加入或修復後應提示「請完全關閉後重新開啟該軟體」（plan 要求，由 UI 負責）。
- 手動設定片段：`BuildManualSnippet(CurrentLaunch)`，輸出為完整 JSON（含根鍵）。
- `ClientPathOptions` 預設不需註冊；若要在 UI 測試中隔離，請在 `AddContexoCore()` 之前註冊自己的 `ClientPathOptions` 單例（整合類別是 DI 建構，會使用已註冊者）。
