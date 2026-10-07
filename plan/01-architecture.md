# 01 架構

## 程序與專案組成

```
Contexo.Core   類別庫：解析、切塊、embedding、SQLite 存取、檢索
Contexo.Wpf    主程式：資料夾管理、背景同步與索引、設定、系統匣常駐
Contexo.Mcp    小型 console exe：stdio MCP server，只負責查詢
```

- 三者共用 `Contexo.Core` 與**同一個 SQLite 檔**。SQLite 開 WAL 模式，WPF 寫入與 MCP 讀取可以同時進行。
- 使用者只看到一個 App（一個安裝檔、一個桌面圖示）。`Contexo.Mcp.exe` 只是安裝目錄裡的附屬檔案。
- 如果查詢端是自己的程式（例如自家 WPF agent），直接引用 `Contexo.Core`，不必經過 MCP。

### 各程序的生命週期

| 程序 | 誰啟動 | 何時存在 |
|---|---|---|
| Contexo.Wpf | 使用者或開機自動啟動 | 可縮小到系統匣常駐，負責持續同步資料夾 |
| Contexo.Mcp | AI 軟體依其 MCP 設定檔啟動 | 跟著 AI 軟體開關，不是系統常駐程式、不是 Windows 服務 |

- WPF 沒開時，AI 仍然查得到既有資料（Mcp 直接讀 SQLite），但新檔案不會被收錄。因此建議預設「開機時自動啟動」並縮小到系統匣。
- Contexo.Mcp 的 ONNX 模型採 **lazy load**：第一次有查詢才載入，AI 軟體開著但沒查資料時幾乎不佔資源。

## 為什麼選 stdio 而不是 HTTP

- stdio：AI 軟體把 exe 當子程序啟動，透過 stdin/stdout 傳 JSON-RPC。WPF 不用開著也能查。
- HTTP：WPF 必須一直開著 AI 才查得到，對非技術使用者較難理解。
- stdio 的限制：只能由同一台機器上的 AI 軟體啟動。若未來有遠端呼叫需求，再評估加開 HTTP（Streamable HTTP）。
- 實作注意：stdout 只能輸出 JSON-RPC，所有 log 寫到 stderr 或檔案。

## Embedding

- 本機：`Microsoft.ML.OnnxRuntime` + `Microsoft.ML.Tokenizers`，模型以 ONNX 格式隨程式發布或首次啟動下載。
- 模型候選：`multilingual-e5-small` / `bge-small-zh`（輕量，CPU 可用）；`bge-m3`（中文品質較好但大且慢）。預設用小模型的 int8 量化版。
- 有顯卡時可用 DirectML 加速，沒有就用 CPU，自動判斷。
- 以 `Microsoft.Extensions.AI` 的 `IEmbeddingGenerator` 做抽象，實作「本機 ONNX」與「遠端 API」兩種。

## 儲存結構（SQLite，單一檔案）

| 資料表 | 內容 |
|---|---|
| `meta` | schema 版本、目前使用的 embedding 模型名稱與向量維度 |
| `folders` | 使用者加入的資料夾、排除的子資料夾 |
| `documents` | 來源檔案：路徑、大小、修改時間、內容 hash、狀態、錯誤原因 |
| `chunks` | 切好的片段：原文、所屬文件、位置資訊（頁碼、投影片、工作表、標題路徑） |
| `embeddings` | chunk_id、model、vector（BLOB） |
| `chunks_fts` | FTS5 全文索引 |
| `excel_tables` | 大型 Excel 表的登記：來源檔、工作表、範圍、欄位、表格說明 |
| `exclusions` | 使用者用右鍵排除的檔案與資料夾 |
| `mcp_activity` | MCP 連線紀錄（clientInfo、時間）與查詢紀錄 |

設計原則：

- **原文和向量放同一個檔**：一個交易就能新增、替換或刪除一份文件的所有資料，不會出現有片段沒向量的情況；使用者複製一個檔就能備份。
- **解析與向量化分離**：`chunks` 和 `embeddings` 分開存，`embeddings` 帶 model 欄位。換模型時只重算向量，不必重跑解析、OCR、視覺模型。
- **記錄模型與維度**：偵測到模型不一致時提示重建，避免混用不相容的向量。
- 資料量在數萬到十幾萬 chunk 內，向量檢索用記憶體暴力比對即可；再大時可加入 `sqlite-vec`。
- 刪除大量資料後執行 VACUUM 釋放磁碟空間。

## 運算來源：本機與公司伺服器

兩種模式可以切換：

| | 只用這台電腦 | 使用公司伺服器 |
|---|---|---|
| 解析與 embedding | 本機 ONNX | 公司伺服器（GPU） |
| 資料存放 | 本機 SQLite | 伺服器的向量庫與資料庫（例如 Milvus） |
| 網路 | 不需要 | 需要 |

- 伺服器模式下，向量庫與資料庫可能都在遠端，所以**兩種模式的資料各自獨立**，切換不會共用資料，也不會在斷線時自動退回本機。
- 切換時一定要出警語，說明資料不共用、檔案內容會傳到伺服器、斷線時無法查詢等影響，確認後才切換。
- 切換後原本模式的資料保留，切回來不需要重建。
- 伺服器設定**只能匯入設定檔**，不提供手動填寫。匯入後唯讀顯示，要修改就重新匯入。設定檔內容包含：embedding 端點與模型、向量庫、資料庫、金鑰、管理者聯絡方式等。
- 金鑰以 Windows DPAPI 加密保存。
- 地端伺服器的 embedding 服務可用 TEI 或 vLLM 部署，掛在 AI gateway 之後，提供 OpenAI 相容的 `/v1/embeddings`。

## 生成（LLM）

Contexo 不做生成。回答由 AI 軟體負責；若要整合公司的 AI gateway，也是由 AI 軟體或上層應用串接。本機 LLM（例如 LLamaSharp + GGUF）不在規劃內。
