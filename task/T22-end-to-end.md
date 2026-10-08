# T22 端對端驗證

- **狀態**：待辦
- **波次**：4
- **相依**：T10、T13（以及全部解析器 T04～T08、T11、T12）
- **必讀**：`AGENTS.md`、`plan/05-operations.md`（品質驗證）、`plan/06-open-questions.md`

## 目標

用接近真實的辦公室文件，驗證「解析 → 切塊 → 向量 → 檢索 → MCP」整條路徑，並產出一份檢索品質報告，作為選定 embedding 模型與調整參數的依據。

## 要做

### 1. 測試語料產生器（`tools/Contexo.CorpusGen/`，net10.0 主控台程式，加入 sln）

以程式產生一套**虛構公司**的繁體中文文件（約 40 個檔案），內容需貼近台灣辦公室實況，且每份文件都有可被查詢的明確事實：

| 類型 | 數量 | 內容示例 |
|---|---|---|
| docx | 10 | 採購規範（多層標題、驗收表格）、會議紀錄（含修訂追蹤）、合約範本（含註腳）、內嵌 xlsx 的提案書 |
| pptx | 8 | 流程圖（申請→審核→採購，含連接線）、組織架構 SmartArt、年度簡報（含圖表與備忘稿）、內嵌 docx 的簡報 |
| xlsx | 10 | 報價單（表單型）、客戶清單（500 列）、銷售明細（3,000 列，含日期與金額，供 SQL 查詢）、一頁兩表、兩層表頭 |
| pdf | 5 | 3～10 頁的規章，含頁首頁尾（若無可用中文字型，以英文內容產生並註明） |
| txt / md / html / csv | 7 | Big5 編碼的舊文字檔、Markdown 手冊、HTML 公告、CSV 匯出 |

另產生 `queries.json`：約 30 個查詢，每個標註「預期命中的檔案」與（可選）「預期位置」，例如：

- 「監視系統的報價金額」→ `2025_台中案_報價單.xlsx`
- 「採購驗收的標準是什麼」→ `採購規範.docx`，章節「驗收」
- 「申請之後要給誰審核」→ `請購流程.pptx`（只有連接線關係能回答）
- 「哪個客戶去年下單金額最高」→ `銷售明細.xlsx`（需要 table_id）
- 兩字查詢「報價」、型號查詢「ABC-123」、英文查詢等。

產生器以固定亂數種子執行，結果可重現。輸出到指定資料夾，**不提交產生的檔案**。

### 2. 端對端測試（`tests/Contexo.Core.Tests/EndToEnd/`，標記 `[Trait("Category","EndToEnd")]`）

1. 以產生器建立語料到暫存資料夾；暫存的資料目錄（`CONTEXO_DATA_DIR`）。
2. 用 `AddContexoCore()` 建立完整服務，加入資料夾，啟動 `IndexingService` 直到 `Idle`（逾時 10 分鐘）。
3. 驗證：無 `Failed` 文件（Big5、內嵌等都成功）；各類型都有片段。
4. 對 `queries.json` 執行 `ISearchService`，計算 **Recall@3**（預期檔案出現在前 3 名的比例）與 **MRR**。
   - 有模型時：Recall@3 ≥ 0.8 才通過。
   - 沒有模型時（`Skip.If` 不跳過整個測試，只改用關鍵字模式）：只記錄數字，不設門檻。
5. 對需要表格的查詢，驗證命中結果有 `TableId`，並以 `ITableQueryService` 執行一個 SUM 查詢，結果與產生器記錄的正確值相符。
6. 修改一個檔案、刪除一個檔案、改名一個檔案，等待同步後驗證結果反映變更。

### 3. MCP 端對端（`tests/Contexo.Mcp.Tests/EndToEnd/`）

使用第 2 步建立好的資料庫，以 SDK client 啟動 `Contexo.Mcp`：`search` → 取得 `table_id` → `describe_table` → `query_table`，全部成功；活動紀錄中出現 Connected 與 ToolCall。

### 4. 品質報告（`tools/eval-retrieval/`，或併入 CorpusGen 的子命令）

`dotnet run --project tools/Contexo.CorpusGen -- eval --models <資料夾>`：對資料夾中每個模型各建一次索引（T03 第一版只支援 WordPiece 分詞的模型，例如 bge-small-zh-v1.5、bge-base-zh-v1.5；multilingual-e5 這類 SentencePiece 模型需先擴充分詞器，不在本任務範圍）並計算 Recall@1/3/5、MRR、索引耗時、每個片段平均嵌入時間，輸出 Markdown 表格到標準輸出。結果貼進完成紀錄。

### 5. 手動驗收清單

`tests/manual/CHECKLIST.md`：彙整 T00 的 POC 結果與 T15～T21 完成紀錄中「需在 Windows 確認」的項目，加上以下情境，作為發布前檢查表：

- 檔案正在 Excel 中開啟時被修改。
- OneDrive 登出後再登入。
- 外接硬碟拔除後重新插入（資料保留、不重建）。
- 一次刪除大量檔案（出現詢問）。
- Claude Desktop 實際查詢，確認 `KnownClientNames`（T14）是否正確，把實測的 `clientInfo.name` 記錄下來。

## 不做

- 用真實公司文件測試（由使用者另行進行）。

## 可修改範圍

- `tools/Contexo.CorpusGen/**`、`tools/eval-retrieval/**`
- `Contexo.slnx`（只新增 CorpusGen 專案）
- `tests/Contexo.Core.Tests/EndToEnd/**`、`tests/Contexo.Mcp.Tests/EndToEnd/**`
- `tests/manual/CHECKLIST.md`
- `.github/workflows/ci.yml`（只新增一個執行 `Category=EndToEnd` 的步驟，CI 中不下載模型）

## 實作要點與已知陷阱

- 產生器重用 T05／T06／T08 測試中的 Builder 輔助類別的做法，但放在 tools 專案中（不要讓 tools 引用測試專案）。
- 端對端測試耗時較長，預設 `dotnet test` 時以 trait 篩選可略過；CI 中另一步驟執行。
- 若發現其他任務的缺陷，**不要直接修改**它們的檔案，在完成紀錄列出問題（檔案、重現方式、預期與實際），交由派工者處理。

## 驗收條件

1. `dotnet run --project tools/Contexo.CorpusGen -- generate <dir>` 產生全部檔案，重跑結果相同（比對檔案 hash）。
2. `dotnet test --filter Category=EndToEnd` 通過（有模型時含 Recall@3 門檻）。
3. MCP 端對端通過。
4. 完成紀錄包含：品質報告表格、發現的缺陷清單、`CHECKLIST.md` 已建立。

## 完成紀錄

（由執行者填寫）
