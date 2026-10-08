# 03 檢索與 MCP 工具

## Hybrid search

1. 查詢句轉成向量，與 chunk 向量做 cosine 相似度比對。
2. 同時以 SQLite FTS5 做關鍵字檢索，補足專有名詞、型號、人名等純向量抓不到的內容。
3. 兩邊結果合併排序（例如 RRF）。
4. 第二階段可加 reranker（例如 bge-reranker）對前 30 筆重新排序；建議放在公司伺服器的 GPU 上跑。

查詢延遲預期：小模型在 CPU 上算查詢向量約數十毫秒，數萬到十幾萬向量的暴力比對加 FTS5 也是數十毫秒等級，使用者幾乎感覺不到。

降級：向量無法計算時（例如伺服器模式斷線、模型重建中），退回只用 FTS5，並提示目前為降級模式。

## MCP 工具（Contexo.Mcp）

| 工具 | 用途 |
|---|---|
| `search` | 語意 + 關鍵字檢索，回傳片段、來源檔案與位置 |
| `describe_table` | 查看某張 Excel 大表的欄位與前幾列原始資料 |
| `query_table` | 對 Excel 大表執行**唯讀** SQL（查詢時載入記憶體 SQLite） |

- 實作使用官方 C# SDK `ModelContextProtocol`，`WithStdioServerTransport()`。
- 回傳結果都帶來源位置，讓 AI 能引用「出自哪個檔案、第幾頁 / 哪張投影片 / 哪個工作表」。
- `query_table` 只允許 SELECT。

## 加入 AI 軟體

「加入」的實際動作是把 `Contexo.Mcp.exe` 的路徑寫進該 AI 軟體自己的 MCP 設定檔。以 Claude Desktop 為例：

```json
{
  "mcpServers": {
    "contexo": {
      "command": "C:\\Program Files\\Contexo\\Contexo.Mcp.exe",
      "args": ["--db", "C:\\Users\\xxx\\AppData\\Local\\Contexo\\contexo.db"]
    }
  }
}
```

- 設定檔位置：`%APPDATA%\Claude\claude_desktop_config.json`。
- 每個 AI 軟體的設定檔位置與格式不同，需要各自實作寫入邏輯（VS Code、Cursor、LM Studio 等）。
- 寫入時保留使用者原有設定，只增減自己的項目；寫完提示「請完全關閉後重新開啟該軟體」。
- 程式更新或安裝路徑改變時，自動修正設定中的路徑。
- 清單上沒有的 AI 軟體，提供「複製設定內容」讓 IT 手動貼上。
- 按鈕文字以使用者角度描述，例如「加入到 Claude Desktop」，避免用「連接」造成誤解。

## 連線狀態偵測

無法百分之百確認 AI 軟體是否正在使用 Contexo，因此依**證據強度**分級顯示：

| 狀態 | 依據 |
|---|---|
| 尚未加入 | 偵測到軟體已安裝，但設定檔沒有 Contexo |
| 已設定，等待連線 | 設定檔有 Contexo 且路徑正確，但尚未收到連線 |
| **已連線** | Contexo.Mcp 收到 `initialize` 請求並記錄其 `clientInfo`（軟體名稱、版本）與時間 |
| 設定有問題 | 設定項目消失、路徑失效、或連線時發生錯誤；提供「修復」 |

- 每次 `search` 呼叫記錄時間，畫面顯示「最後連線」與「最後查詢」。
- 偵測不到的情況：使用者在 AI 軟體內停用了該 MCP server、設定放在專案層級或由政策統一管理、軟體改版變更設定格式、模型自行決定不呼叫工具。
- 畫面提供驗證方式：「在 AI 軟體裡問『用 Contexo 找報價單』，這裡的最後查詢時間就會更新」。

## 「試試看搜尋」

桌面程式內建的搜尋框，使用與 MCP 相同的檢索流程，但**不經過 LLM**，只列出找到的片段、來源位置、相關度，以及是語意或關鍵字命中。用途是讓使用者確認資料已被讀入，也是開發時檢查檢索品質的工具。
