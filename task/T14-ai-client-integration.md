# T14 AI 軟體設定整合與狀態

- **狀態**：待辦
- **波次**：2
- **相依**：T02
- **必讀**：`AGENTS.md`、`plan/03-retrieval-and-mcp.md`（加入 AI 軟體、連線狀態偵測）、`src/Contexo.Core/Abstractions/Integrations.cs`、`Storage.cs`（MCP activity）

## 目標

讓使用者按一個按鈕就能把 Contexo 加進 AI 軟體，並依證據強度判斷「有沒有真的接上」。

## 要做

### 1. 各 AI 軟體的整合（`Integrations/*Integration.cs`）

**動手前先查官方文件確認目前的設定檔位置與格式**，在完成紀錄附上參考網址與查詢日期。下表是規劃時的已知資訊：

| 類別 | ClientId | 設定檔（Windows） | 根鍵 | 項目格式 |
|---|---|---|---|---|
| `ClaudeDesktopIntegration` | `claude-desktop` | `%APPDATA%\Claude\claude_desktop_config.json` | `mcpServers` | `{ "command": …, "args": [ … ] }` |
| `VsCodeIntegration` | `vscode` | `%APPDATA%\Code\User\mcp.json` | `servers` | `{ "type": "stdio", "command": …, "args": [ … ] }` |
| `CursorIntegration` | `cursor` | `%USERPROFILE%\.cursor\mcp.json` | `mcpServers` | `{ "command": …, "args": [ … ] }` |
| `LmStudioIntegration` | `lm-studio` | `%USERPROFILE%\.lmstudio\mcp.json` | `mcpServers` | `{ "command": …, "args": [ … ] }` |

項目名稱一律為 `"contexo"`。

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

1. 未安裝、未設定、已設定、路徑失效、路徑與目前不同、JSON 損壞，`GetConfigState` 正確。
2. `AddOrRepair`：原本有其他 server 與其他設定的檔案，加入後全部保留；`.bak` 產生；重複呼叫結果相同。
3. JSON 損壞時 `AddOrRepair` 拋例外且檔案內容不變。
4. `Remove` 只移除 `contexo`。
5. 含註解與尾逗號的 VS Code 設定檔可以讀取並加入。
6. 狀態服務：六種組合的結果正確；名稱模糊比對有效。
7. `BuildManualSnippet` 輸出為合法 JSON，且根鍵正確。

## 完成紀錄

（由執行者填寫：各軟體的參考文件網址與日期、`KnownClientNames` 的依據）
