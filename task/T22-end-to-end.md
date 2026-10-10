# T22 端對端驗證

- **狀態**：完成
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

**分支**：`task/T22-end-to-end`（基準 `c3d4b8d`）。T21（打包與安裝）依維運者決定保留，不在本次範圍；`tests/manual/CHECKLIST.md` 第 5 節列出「T21 待辦（需 Windows）」。

### 做了什麼

- **語料產生器** `tools/Contexo.CorpusGen/`（net10.0 主控台，已加入 `Contexo.slnx`，套件全是 `Directory.Packages.props` 既有的 OpenXml、PdfPig、DI）。固定種子 `20251010`，產生虛構公司「曜陽科技」的 40 個檔案：docx 10（採購規範含驗收表格、會議紀錄含修訂追蹤、合約範本含註腳、內嵌 60 項報價 xlsx 的提案書…）、pptx 8（流程圖連接線、SmartArt 組織圖、圖表加備忘稿、內嵌 docx、投影片表格…）、xlsx 10（報價單表單、客戶清單 500 列、銷售明細 3,000 列、一頁兩表、兩層表頭、公式快取值…）、pdf 5（3～10 頁，頁首與「Page N of M」頁尾）、txt／md／html／csv 7（2 個 Big5 txt、2 個 csv，其中 200 列訂單匯出是大表）。另寫出 `queries.json`（42 個計入門檻的題目＋4 個只觀察的題目，標註預期檔案、位置、是否需要 `TableId`）、`expected.json`（銷售合計、金額最高客戶、客戶清單北部家數、已出貨筆數，由產生器自己算）與 `MANIFEST.txt`（每個檔案的 SHA-256）。Builder 輔助類別（`Office/DocxBuilder`、`PptxBuilder`、`XlsxBuilder`）複製 T05／T06／T08 測試的寫法並放在 tools 專案內，沒有讓 tools 引用測試專案。Open XML 套件預設含現在時間與隨機關聯 id，所以 `ZipNormalizer` 把時間固定並把關聯 id 依序編號、`PdfNormalizer` 把 PDF 的隨機 `/ID` 換成常數，重跑結果才相同。
- **子命令**：`generate <資料夾>`、`eval [--models <資料夾>] [--corpus <資料夾>] [--keep]`（品質報告：對每個模型各建一次索引，輸出 Recall@1/3/5、MRR、索引耗時、每片段平均嵌入時間、逐題結果，以及「有答案／無關題目的最高語意相似度」分布）、`parse <檔案...>`（用正式解析器與切塊器把片段印出來，檢查真實軟體另存的檔案用）。
- **真實軟體樣本** `tools/Contexo.CorpusGen/real-samples/`（macOS：`textutil` 寫 docx、Google Chrome 列印中文 PDF）：補「自己組出來的檔案不像真的」這個缺口，缺陷 1、2 就是這樣發現的。
- **端對端測試**（`Category=EndToEnd`）：`tests/Contexo.Core.Tests/EndToEnd/`（`CorpusEndToEndTests` 5 項、`FolderSyncEndToEndTests` 2 項、`BrokenFilesEndToEndTests` 1 項、`MassDeletionKeepEndToEndTests`／`MassDeletionConfirmedEndToEndTests` 各 1 項）與 `tests/Contexo.Mcp.Tests/EndToEnd/`（2 項）。涵蓋：40 個檔案無 Failed 且各類型有片段；修訂追蹤、註腳、Big5、內嵌檔、備忘稿、圖表、連接線、PDF 頁首頁尾、母片文字不外洩；Recall@3／MRR；需要表格的查詢有 `TableId` 且 SQL 的 SUM／GROUP BY／COUNT 與產生器的值相符；修改、刪除、改名檔案後的同步（先給資料夾監看器 20 秒，沒反應才要求掃描，並記錄是哪一種）；整個資料夾搬走再搬回（資料保留、不重建）；壞檔逐一標失敗而不影響其他檔案；一次刪 21 個檔案出現詢問，回答「保留」與「刪除」兩條路；MCP 的 `search` → `table_id` → `describe_table` → `query_table` 全部成功，數字與產生器相符，活動紀錄出現 Connected 與 ToolCall。
- `tests/manual/CHECKLIST.md`（發布前檢查表）、`.github/workflows/ci.yml`（Windows 工作只新增一個步驟 `dotnet test Contexo.slnx --no-build --filter "Category=EndToEnd"`；CI 不下載模型，所以在關鍵字模式執行）、`Contexo.slnx`（只新增 CorpusGen）。

### 驗收結果（macOS，Apple 晶片，.NET 10.0.401）

1. `dotnet run --project tools/Contexo.CorpusGen -- generate <dir>` 產生 40 個檔案；連跑兩次（不同資料夾）`MANIFEST.txt` 的 42 行 SHA-256 完全相同。限制：壓縮輸出只保證同一台機器、同一版 .NET 相同。
2. `dotnet test --filter Category=EndToEnd`：Core 10 項、Mcp 2 項全過，連跑 5 次無不穩定。**有模型**（Recall@3 ≥ 0.8 門檻生效，實測 0.90）與**無模型**（把 `models/` 移走，只記錄數字）兩條路徑都跑過且通過。耗時：Core 約 5 秒，Mcp 約 15～25 秒（每項各自啟動 `Contexo.Mcp` 子行程並建索引）。
3. MCP 端對端通過（有模型與無模型都通過）。第一次 MCP 搜尋 87 毫秒、第二次 6 毫秒，但資料只有約 244 個片段，大資料量的延遲仍未量測。
4. 本紀錄含品質報告、缺陷清單；`CHECKLIST.md` 已建立。
5. `dotnet build Contexo.slnx -warnaserror`：0 警告 0 錯誤。`dotnet test`（全方案）：App 294、Desktop 121、Core 948（3 略過，皆為既有需要 Windows 的測試）、Mcp 51，全數通過。過程中有一次全方案執行失敗 2 項，皆是 README 待決定 A-10 列的時間相關測試（`VectorIndexTests.Search_50kVectorsOf512Dimensions_FinishesUnder100Ms`、`SingleInstanceTests.Can_be_woken_more_than_once`），單獨重跑都通過。

### 檢索品質報告

42 個計入門檻的題目（`bge-small-zh-v1.5 (int8)` 是 `tools/download-models.sh` 下載的預設模型；fp32 與 base 版另從 Hugging Face 的 Xenova 匯出下載到暫存資料夾比較，沒有進版控）。Recall@k 以「搜尋結果（每筆是一個片段，同檔最多 3 筆）前 k 名內有預期檔案」計算，一次要求 10 筆。耗時為 Apple 晶片 CPU，Windows 一般文書機會慢很多。

| 模型 | 向量 | 檔案 | 失敗 | 片段 | Recall@1 | Recall@3 | Recall@5 | MRR | 索引耗時 | 每片段嵌入 |
|---|---|---|---|---|---|---|---|---|---|---|
| bge-base-zh-v1.5 (int8) | 768 維 | 40 | 0 | 244 | 0.81 | 0.93 | 0.95 | 0.878 | 6.6 秒 | 26.3 ms |
| bge-small-zh-v1.5 (fp32) | 512 維 | 40 | 0 | 244 | 0.76 | 0.95 | 0.98 | 0.866 | 2.1 秒 | 5.0 ms |
| **bge-small-zh-v1.5 (int8，預設)** | 512 維 | 40 | 0 | 244 | 0.69 | 0.90 | 0.98 | 0.808 | 1.8 秒 | 5.2 ms |
| 無模型，只用關鍵字 | - | 40 | 0 | 244 | 0.64 | 0.74 | 0.76 | 0.695 | 0.8 秒 | - |

給待決定 A-3（要不要加相似度門檻）的資料：「查詢與最相近片段的內積」，有答案的 42 題 vs 10 個無關題目（天氣、咖哩飯、股價…）：

| 模型 | 有答案的題目（最低 / 中位 / 最高） | 無關的題目（最低 / 中位 / 最高） | 有答案但不高於無關題最高值的題數 |
|---|---|---|---|
| bge-base-zh-v1.5 (int8) | 0.268 / 0.519 / 0.682 | 0.241 / 0.334 / 0.463 | 17 |
| bge-small-zh-v1.5 (fp32) | 0.398 / 0.623 / 0.774 | 0.311 / 0.437 / 0.504 | 7 |
| bge-small-zh-v1.5 (int8) | 0.425 / 0.640 / 0.787 | 0.340 / 0.443 / 0.515 | 6 |

判讀：單一固定門檻分不乾淨（預設模型要擋掉全部無關題目，需要約 0.52 的門檻，同時會擋掉 6／42＝14% 有答案的題目；base 模型 17／42）。若要做，建議用「與第一名的差距」或 reranker，而不是固定值。題數小（42＋10），數字只能當方向；int8 與 fp32 的 Recall@3 只差 2 題，不宜據此下結論。

**這份報告缺少的部分**：沒有 multilingual-e5 這類 SentencePiece 模型（T03 第一版不支援，不在任務範圍）；沒有 bge-m3；沒有 Windows 機器上的耗時；沒有真實公司文件，也沒有真實 Office 另存的檔案（見「無法驗證」）；語料的 PDF 是英文（任務允許），中文 PDF 的品質只能從缺陷 1、2 的 macOS 樣本看。

逐題結果用 `eval` 命令可再產生。預設模型沒進前 3 的題目：q03（流程圖「申請之後要給誰審核」第 5 名）、q16（SmartArt「資訊部歸誰管」第 6 名）、q27（兩層表頭小表「北區第三季營收」第 9 名）、q42（小 csv「資訊部值班手機」第 4 名）；共同點是片段很短或是表格 HTML，語意模型抓不準。

### 發現的缺陷（沒有修改任何其他任務的檔案）

**1. 〔高〕T07 PDF：文字層的「部首」字元沒有正規化，用一般字搜尋找不到。**
- 檔案：`src/Contexo.Core/Parsing/Pdf/PdfParser.cs`、`PdfTextLayout.cs`（也可放在切塊前的共用位置）。
- 重現：`bash tools/Contexo.CorpusGen/real-samples/make-real-samples.sh <dir>`，再 `dotnet run --project tools/Contexo.CorpusGen -- parse <dir>/chrome-手冊.pdf`（Chrome 列印的中文 PDF）。
- 預期：「出差前三日」「領用」「員工」「二千五百元」。實際：`出差前三⽇`、`領⽤`、`員⼯`、`⼆千五百元`；`⼀ ⼆ ⼗ ⽇ ⽤ ⼯ ⾄ ⽐ ⾞ ⾃ ⾏ ⼈` 是 Kangxi 部首區（U+2F00～U+2FDF）的碼位，不是一般漢字；另有 `項⺫`（目，U+2EEB，屬 CJK 部首補充區，**NFKC 也轉不回來**，需要自己的對照表）與連字 `ﬁ`（U+FB01）。
- 影響：同一份抽出的文字，一份原樣、一份經 NFKC 正規化，各自建索引後用一般字查詢：關鍵字模式 4 題中 4 題正規化版排得較前，其中「採購金額逾五十萬元怎麼辦」原樣版完全找不到；語意模式 4 題中 3 題正規化版較前。這是 macOS 加 Chrome 的輸出；Windows 的 Word 另存 PDF 是否也會發生要在 Windows 確認（CHECKLIST 2.8）。
- 建議：解析後對文字做 `Normalize(NormalizationForm.FormKC)`，再加一張小對照表處理 U+2E80～U+2EFF。

**2. 〔中〕T07 PDF：真實中文 PDF 的版面還原不佳。**重現同上，檔案 `chrome-手冊.pdf`（4 頁）。(a) Chrome 的頁首（日期、時間）與頁面第一行正文落在同一條水平線上，變成 `2026/10/10 ⼀、出差申請 下午2:58` 並留在內容裡，重複的頁首沒有被移除；(b) 雙欄頁面閱讀順序亂掉：「右欄第一段」排在「左欄第二段」前面；(c) 表格儲存格黏在一起且順序錯：`項⺫ 抽樣標準外觀 10% 不良率 2% 以下功能 100% 全數通過`；(d) 標題與下一段黏在一起：`三、驗收驗收應於到貨後…`。另外只有 1～2 頁的 PDF 不會移除頁首頁尾（`RemoveHeadersAndFooters` 少於 3 頁不處理，是設計，但小型 PDF 會留下「file:///…」「1/2」這類雜訊，見 `chrome-規章.pdf`）。對應 README C-9「真實中文 PDF」。

**3. 〔中〕T11 關鍵字模式：整句中文問句幾乎找不到東西。**
- 檔案：`src/Contexo.Core/Search/KeywordQueryBuilder.cs`。
- 重現：移走 `models/`，執行 `dotnet test --filter Category=EndToEnd` 看 `Retrieval_quality_on_the_generated_queries` 的輸出；或 `eval` 的最後一列。
- 預期：沒有模型（或模型載入失敗而降級）時，至少抓得到句子裡的詞。實際：Recall@3 只有 0.74（有模型 0.90）；「尾牙在哪裡舉行」「什麼時候停電」「申請之後要給誰審核」「客戶報修多久內要到場」完全沒有結果。原因：沒有標點的整句是一個詞，被拆成它所有的 3 字元子字串，文件裡沒有完全相同的 3 字元片段就沒結果；2 字元的 LIKE 路徑只對本來就只有 2 字的詞有用。降級時 MCP 會加「目前只使用關鍵字比對」，但使用者無從補救。
- 建議：長詞同時產生 2 字元的 LIKE 詞（上限內取最有區別力的），或用常見虛詞（的、是、在、哪、什麼）斷詞。

**4. 〔中〕T08／T09 大表摘要片段對「分析型問句」幾乎無法召回。**
- 重現：`eval` 的 q04「哪個客戶去年下單金額最高」（任務檔指定的例子）：預設模型第 10 名內沒有 `銷售明細.xlsx`（fp32 第 8 名；base 模型進前 3），第 1 名是 `採購規範.docx`。換成「銷售明細表」（q43，說出檔名）則第 1 名。摘要片段只有檔名、欄名與前 5 列，問句沒提這些就不容易命中。因此 q04 在 `queries.json` 標為只觀察（q43 負責 `TableId`／SQL 驗證）。
- 建議：摘要加一句白話說明（例如「這是一張 N 筆的銷售明細表，可以用 SQL 統計金額、客戶…」），或對欄名補同義詞。

**5. 〔已知，待決定 A-1〕內嵌在其他檔案裡的 Excel 大表。**實際觀察（`CorpusEndToEndTests.A_table_embedded_in_another_file_is_found_but_does_not_crash_the_table_service`、MCP 端對端的對應測試）：(a) 提案書內嵌的 60 列試算表被登記成大表（`TableId` 有發出），摘要片段的檔名是 `package.xlsx`（產生器用 Open XML SDK 建立，真實 Word 通常叫 `Microsoft_Excel_Worksheet.xlsx`）；(b) 搜尋只能靠摘要裡的前 5 列：「PTZ-2000 球型攝影機的單價」剛好在第 2 列所以找得到（o01），其餘 55 列既搜尋不到也查詢不到；(c) 直接呼叫 `ITableQueryService.DescribeAsync` 丟 `TableQueryException`「無法讀取原始檔案：智慧監控提案書.docx（無法讀取試算表內容。）」，語意誤導（docx 本身沒壞）；T13 的防護讓 MCP 回白話說明，不會當機。

**6. 〔低〕T09 英文切塊的重疊在單字中間切開。**檔案 `src/Contexo.Core/Chunking/StructuredChunker.cs`。重現：索引 `Visitor_Management_Policy.pdf`，第 2 頁的一個片段開頭是 `ning on the content of this document…`（單字 Training 的前半被切掉）。預期在字邊界切；實際切在字中間。中文不受影響，英文與混合內容的可讀性與關鍵字比對受影響。

**7. 〔資訊〕**Word 與 PDF 解析器的 `DocumentParseException` 訊息是英文（`WordParser.cs` 第 33、43、54、78 行；`PdfParser.cs` 第 52、53、138 行），其他解析器是繁體中文。目前 `DocumentRecord.ErrorMessage` 沒有任何畫面或匯出會顯示（UI 用 `ErrorCode`），所以使用者看不到，但 AGENTS.md 第 8 節要求使用者看得到的文字用繁體中文，之後若顯示出來要一併改。

沒有發現問題、值得記下的正面結果：資料夾監看器 3.8 秒內反映修改、刪除、改名（改名保留同一個文件 id 與片段）；整個資料夾消失再出現時資料不動、不重建；壞檔（不是 zip、空檔、截斷的 xlsx／pptx）都逐一標成 `Failed／Corrupted`，其他檔案不受影響；大量刪除詢問兩條路都對；Big5、修訂追蹤、註腳、內嵌 docx、連接線、SmartArt、圖表與備忘稿、兩層表頭、公式快取值都正確；母片與版面配置文字沒有外洩；`1,285,000` 這種有千分位的查詢找得到（解析器存成 `1285000`，數字子字串仍比對得到，觀察題 o02）。

### 無法在目前環境驗證

- **Windows 的一切**：`Contexo.Mcp.exe`、檔案共用違規、OneDrive 佔位檔、`FileSystemWatcher` 行為、登錄檔、系統匣等（見 CHECKLIST）。`A_folder_that_cannot_be_read_keeps_its_data_and_recovers_when_it_returns` 在 Windows 會略過：Windows 不允許改名被監看的資料夾（CI 的 Windows 工作會略過它，「外接硬碟拔除」只能在 Windows 手動確認，CHECKLIST 4.3）。
- **真實 Office 另存的檔案**：沒有 Word、Excel、PowerPoint；README D 要求「加入真實軟體另存的樣本」只做到 macOS 內建 `textutil`（docx）與 Chrome（PDF）。`textutil` 寫出的 docx 沒有真正的表格，所以 Word 表格仍只用產生的檔案驗證。
- **Claude Desktop 等真實 AI 軟體**的 `clientInfo.name`、協定版本、工具描述是否讀得懂（CHECKLIST 4.5 附記錄表）。
- **大資料量**：十萬筆向量的記憶體與第一次搜尋的延遲（測試資料只有 244 個片段）。
- 只有 macOS／Apple 晶片的耗時；Windows 一般文書機的索引耗時要另測。

### 與規格不同的地方及理由

- 任務檔要求測試以產生器建立語料，但可修改範圍不含 `*.csproj`，無法讓測試專案引用 CorpusGen。改為：測試以子行程執行已建置的 `Contexo.CorpusGen.dll`（找不到時退回 `dotnet run --project`）。因此單獨對測試專案跑 `dotnet test` 前要先建置方案（`dotnet build Contexo.slnx`，CI 本來就是）。
- 查詢題數是 42＋4 個只觀察，比「約 30 個」多；`q04`（任務檔點名的例子）標成只觀察，改用 `q43` 驗證 `TableId`／SQL，理由見缺陷 4。
- PDF 用英文與 Helvetica（任務允許；中文字型不一定存在，也避免輸出隨平台不同）。
- Mcp 端對端測試沒有重用 Core 測試建好的資料庫（兩個測試專案無法共用狀態），而是各自產生語料、在測試行程內建索引後啟動 `Contexo.Mcp`。
- 端對端測試沒有被預設排除：全方案 `dotnet test` 也會跑（約多 20 秒），CI 另有一個明確的 `Category=EndToEnd` 步驟（任務檔要求只新增一步，所以沒有改既有步驟）。
- 額外做了任務檔沒要求的：資料夾搬走再搬回、壞檔、大量刪除、監看器計時、相似度分布、`parse` 子命令、`real-samples`。
- `eval` 的「每片段嵌入時間」是用查詢結果中最多 200 個不重複片段單獨量的（含預熱），不含解析與寫入；「索引耗時」是整個語料從開始到閒置的牆鐘時間。

### 留給後續任務的注意事項

- 缺陷 1、2 建議派給 T07；缺陷 3 派給 T11；缺陷 4、6 派給 T09（缺陷 4 可能牽涉 T08 的 `Description`）；缺陷 5 等維運者決定 A-1。
- 修正缺陷 1 後，用 `real-samples` 的兩個 PDF 再跑 `parse` 確認，並可補一題觀察題。
- 修正 A-1 後，把 `ContexoToolService.IsEmbedded` 防護與測試一併更新；`CorpusEndToEndTests.A_table_embedded_in_another_file_...` 與 `CorpusMcpEndToEndTests.A_table_embedded_...` 目前只觀察不斷言，不需要改。
- 換 embedding 模型或調整切塊參數時，執行 `dotnet run --project tools/Contexo.CorpusGen -- eval --models <資料夾>` 比較；CI 沒有模型，只會跑關鍵字模式。
- T21 打包時 `tools/` 不應進安裝檔（CorpusGen 不是產品的一部分）。
