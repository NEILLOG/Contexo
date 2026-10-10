# Contexo 第一版工作細項

範圍依 [`plan/README.md`](../plan/README.md) 的「第一版範圍」。第二階段項目不在這裡。

開始任何任務前，代理必須先讀 [`AGENTS.md`](../AGENTS.md)。

## 任務總覽

| 編號 | 任務 | 波次 | 相依 | 主要產出 |
|---|---|---|---|---|
| [T00](T00-ime-poc.md) | 中文輸入 POC（**人工**，✅ 已完成） | 前置 | — | Avalonia 注音輸入、字型、主題、字級的實測結果 |
| [T01](T01-solution-skeleton.md) | 方案骨架與共用基礎（✅ 已完成） | 0 | — | sln、專案、套件、stub、DI、共用工具、CI |
| [T02](T02-sqlite-store.md) | SQLite 儲存層（✅ 已完成） | 1 | T01 | `SqliteKnowledgeStore` |
| [T03](T03-onnx-embedding.md) | 本機 ONNX Embedding（✅ 已完成） | 1 | T01 | `OnnxEmbeddingService`、模型下載腳本 |
| [T04](T04-text-parsers.md) | 文字類解析器（✅ 已完成） | 1 | T01 | txt / md / json / xml / log / html / rtf |
| [T05](T05-word-parser.md) | Word 解析器（✅ 已完成） | 1 | T01 | `WordParser` |
| [T06](T06-powerpoint-parser.md) | PowerPoint 解析器（✅ 已完成） | 1 | T01 | `PowerPointParser` |
| [T07](T07-pdf-parser.md) | PDF 解析器（✅ 已完成） | 1 | T01 | `PdfParser` |
| [T08](T08-spreadsheet-parser.md) | Excel / CSV 解析器（✅ 已完成） | 1 | T01 | `SpreadsheetParser`、`SpreadsheetRegionReader` |
| [T09](T09-chunker.md) | 結構化切塊（✅ 已完成） | 1 | T01 | `StructuredChunker` |
| [T15](T15-desktop-shell.md) | 桌面外殼（Avalonia）、主題、系統匣（✅ 已完成，macOS 畫面操作與 Windows 待人工確認） | 1 | T01 | 主視窗、導覽、主題、字級、系統匣、狀態列 |
| [T10](T10-indexing-pipeline.md) | 索引管線與資料夾同步（✅ 已完成） | 2 | T02、T03、T09 | `IndexingService` |
| [T11](T11-hybrid-search.md) | Hybrid 檢索（✅ 已完成） | 2 | T02、T03 | `HybridSearchService` |
| [T12](T12-table-query.md) | Excel 表格查詢（✅ 已完成） | 2 | T02、T08 | `TableQueryService` |
| [T14](T14-ai-client-integration.md) | AI 軟體設定整合與狀態（✅ 已完成） | 2 | T02 | `Integrations.*` |
| [T13](T13-mcp-server.md) | MCP Server（✅ 已完成，需用真實 AI 軟體連線確認） | 3 | T11、T12 | `Contexo.Mcp` |
| [T16](T16-ui-folders.md) | 介面：資料夾頁與首次啟動精靈（✅ 已完成，實機操作待人工確認） | 3 | T10、T15 | 資料夾頁、子資料夾視窗、移除確認、精靈 |
| [T17](T17-ui-search.md) | 介面：試試看搜尋（✅ 已完成，實機操作待人工確認） | 3 | T11、T15 | 搜尋頁 |
| [T18](T18-ui-ai-clients.md) | 介面：AI 軟體頁（✅ 已完成，實機操作待人工確認） | 3 | T14、T15 | AI 軟體頁 |
| [T19](T19-ui-settings.md) | 介面：設定頁（✅ 已完成，實機操作待人工確認） | 3 | T10、T15 | 設定頁、開機啟動 |
| [T20](T20-about-diagnostics.md) | 關於與問題回報（✅ 已完成，實機操作待人工確認） | 3 | T02、T15 | `DiagnosticsExporter`、關於頁 |
| [T21](T21-packaging.md) | 打包與安裝程式（⏸ 保留，待 Windows 環境） | 4 | T13、T16～T20 | 發布設定、安裝程式、CI 產出 |
| [T22](T22-end-to-end.md) | 端對端驗證（✅ 已完成，發現缺陷見下方） | 4 | T10、T13 | 測試語料產生器、E2E 測試、驗收報告 |

## 執行順序

```
前置   T00 ✅（使用者判定通過）
波次 0  T01 ✅
         │
波次 1  T02✅ T03✅ T04✅ T05✅ T06✅ T07✅ T08✅ T09✅ T15✅     ← 全部可平行
         │
波次 2  T10✅ T11✅ T12✅ T14✅
         │
波次 3  T13✅ T16✅ T17✅ T18✅ T19✅ T20✅
         │
波次 4  T21【保留，待 Windows】  T22✅
```

- 同一波次的任務**檔案範圍互不重疊**，可同時交給不同子代理。
- 解析器（T04～T08）不是 T10 的硬相依：T10 透過 `IParserRegistry` 取用，解析器未完成時該格式會被記為「不支援」，T10 的測試用假的解析器。
- T01 必須先合併，其他任務才能開始。

## 待決定與待人工驗證

> 更新於 2026-10-10，內容來自 T01～T20 各任務完成紀錄。決定或驗證完成後，請在這裡劃掉或刪除該列。

### A. 需要你決定

| # | 事項 | 來源 | 我的建議 |
|---|---|---|---|
| 1 | **內嵌在其他檔案裡的 Excel 大表查不到。** T10 會登記它們（`TableKey` 形如 `內嵌.xlsx#Sheet1!A1:F20`），但 T12 只能用磁碟路徑讀表格。T13 已加防護：AI 查到這類表格時，會收到白話說明（目前無法查詢，請改用搜尋結果裡的文字），不會當機；但這只是止血。 | T10、T12、T13 | 決定根本處理方式：(A) 第一版不登記內嵌大表、改當一般文字讀取（最省事，我的建議）、(B) 查詢時先從母檔案抽出內嵌 Excel 再讀、(C) 維持現狀。選 A 後要把 `ContexoToolService.IsEmbedded` 防護與對應測試 `An_embedded_table_cannot_be_queried_and_the_message_says_so` 拿掉。 |
| 2 | **資料夾根目錄的「不要讓 AI 讀這個資料夾」**目前是清空資料但資料夾仍留在清單，不是移除資料夾。 | T16 | 如果你預期它等於移除，要改成呼叫移除流程。 |
| 3 | **檢索沒有相似度下限**：不相關的內容可能以「語意命中」出現在結果尾端。要不要加門檻或 reranker。T22 量過：有答案題目與無關題目的最高語意相似度有重疊，預設模型要擋掉全部無關題會同時擋掉 6/42 有答案的題（base 模型 17/42），固定門檻分不乾淨。 | T11、T22 | 不建議用固定門檻；若要做，改看「與第一名的差距」或 reranker。 |
| 4 | **選擇器起始位置**：匯出問題回報的資料夾選擇器不一定從桌面開始，要設 `SuggestedStartLocation` 需改範圍外的 `Platform/Common/AvaloniaServices.cs`。 | T20 | 小改動，建議直接改。 |
| 5 | **啟動錯誤畫面的接法**：現在由畫面自己從 `App.Services` 取匯出流程；改 `App.axaml.cs` 為 `new StartupErrorViewModel(about.CreateExportFlow())` 會更乾淨。 | T20 | 建議順手改。 |
| 6 | **淺色主題強調色不對**：`ToggleSwitch`、`CheckBox` 在淺色顯示 Fluent 預設藍 `#0078D4`，不是 `Brush.Accent`（深色正確）。`Themes/Colors.axaml` 的覆寫在淺色似乎沒生效。 | T19 發現，屬 T15 | 建議修。 |
| 7 | **對話框寬度上限 440** 讓子資料夾視窗、無法讀取的檔案視窗偏窄。限制在 `MainWindow.axaml`。 | T16，屬 T15 | 視實機看起來再決定。 |
| 8 | **測試用的無參數建構式要不要統一清掉。** T16～T20 為了不改範圍外的 `TestShell.cs`、`ShellViewModelTests.cs`、`ViewLocatorTests.cs`，各頁 ViewModel 都留了無參數建構式。 | T16～T20 | 一次改這三個檔改用假服務，再刪掉那些建構式。 |
| 9 | **「已提示過縮到系統匣」旗標**存在資料庫 meta（`ui.tray_hint_shown`），因為 `AppSettings` 是共用契約不能加欄位。 | T15 | 不影響使用，除非你想放進設定檔（要改契約）。 |
| 10 | **兩項偶發失敗的測試要不要派人修穩定性：** `SingleInstanceTests.Can_be_woken_more_than_once`（T15，同時有別的桌面程式在跑時會壞）、`ConcurrencyTests`（T02）、`VectorIndexTests` 效能門檻（T11）、`AiClientsViewModelTests.Showing_the_page_reads_at_once_and_then_every_ten_seconds`（T18，三次全方案跑裡失敗兩次，看起來是測試本身的競態：第一次讀取完成後就推進假時鐘，但計時器可能還沒開始等待，時間被漏掉）。全都是單獨跑會過、多專案同時跑偶爾失敗。 | T11、T14～T20 | CI 若再出現就修；目前不影響合併。 |
| 11 | ~~`tests/manual/CHECKLIST.md` 尚未建立~~ **已由 T22 建立**，含「T21 待辦（需 Windows）」區塊。 | T22 | 已完成；本區塊 B、C 的項目之後可以以它為準。 |
| 12 | **T06 連接線方向**：兩端都沒箭頭或兩端都有箭頭時，一律輸出 `起點 --> 終點`（照規格字面）。 | T06 | 想要無方向的線再說。 |
| 13 | **換 embedding 模型後舊向量不會清除**，目前只有設定頁的「清除全部資料」會清掉。 | T10 | 第一版可接受。 |
| 14 | **`LocationText` 有兩份複本**：`Contexo.App/Search/LocationText.cs`（搜尋頁）與 `Contexo.Mcp/Tools/LocationText.cs`（MCP 輸出）。因為 Mcp 不能引用 App，目前是完全複製，改一份要同步另一份。 | T13、T17 | 若不想雙份維護，可把它移到 `Contexo.Core`（需改兩邊專案範圍）。第一版先接受也可以。 |
| 15 | **MCP 不輸出 `Score` 與 `MatchedBy`** 給 AI 軟體。 | T13 | T22 評估品質時再看要不要加。 |

### B. 需要人工測試（macOS，可在目前這台機器做）

子代理的環境沒有螢幕擷取權限或視窗伺服器，下列都只用 Headless 截圖與自動測試替代，沒有人實際看過。

| # | 項目 | 來源 |
|---|---|---|
| 1 | 視窗外觀、明暗主題跟隨系統、三段字級即時切換 | T15 |
| 2 | 系統匣（選單列）圖示與選單、第一次關閉視窗的提示 | T15 |
| 3 | 注音輸入實測（T00 POC 項目 1～4） | T15 |
| 4 | 資料夾頁對照 `plan/ui-mockup.html`；**拖放資料夾**到資料夾頁與精靈第 1 步；子資料夾視窗；移除確認；首次啟動精靈三步驟 | T16 |
| 5 | 搜尋頁對照 mockup；**注音選字時按 Enter 不應觸發搜尋**；「開啟原檔」與「在 Finder 中顯示」 | T17 |
| 6 | AI 軟體頁：按「加入」→ 重開 Claude Desktop → 提問 → 狀態變「已連線」。**這會改到你真實的 Claude Desktop 設定檔**（有 `.contexo.bak` 備份，只動 `contexo` 這一筆），建議先自行備份。 | T18 |
| 7 | 設定頁：主題與字級即時切換；開機啟動開啟後登出再登入，確認以選單列圖示啟動（寫入 `~/Library/LaunchAgents/tw.contexo.desktop.plist`）；「清除全部資料」的真實流程 | T19 |
| 8 | 關於頁：「匯出問題回報…」、系統的資料夾選擇器、「開啟所在資料夾」、啟動錯誤畫面的匯出按鈕；打開產生的 zip 檢查內容是否沒有你的使用者名稱與完整路徑 | T20 |
| 9 | **MCP 實際連線：** 用 Claude Desktop 連一次，確認工具（`search`、`describe_table`、`query_table`）有出現、描述讀得懂、AI 軟體頁的狀態變「已連線」、`clientInfo.name` 與桌面程式顯示一致。T13 只用官方 SDK client 與手寫 JSON-RPC 測過，沒用真實 AI 軟體。 | T13、T14 |
| 10 | 手動執行時請設定獨立的 `CONTEXO_DATA_DIR`，不要動到你真正的資料 | 全部 |

### C. 需要人工測試（Windows，T21 前統一確認）

| # | 項目 | 來源 |
|---|---|---|
| 1 | 系統匣右下角圖示、左鍵開啟、右鍵選單、第一次關閉的提示、第二次啟動只喚起既有視窗、閒置時間偵測 | T15 |
| 2 | 微軟注音（新舊版）輸入，T00 POC 項目 1～4 與 10，字型 `Microsoft JhengHei UI` | T15 |
| 3 | 開機啟動：Run 登錄值寫入與刪除、工作管理員「啟動」分頁看得到、重開機後以系統匣啟動、程式搬移後自動修正、群組原則禁止寫入時開關彈回 | T19 |
| 4 | 拖放（檔案總管）、OneDrive 與 OneDriveCommercial 環境變數內容、「⋯」選單在 125% 字級是否被裁切、隱藏與系統屬性資料夾被略過 | T16 |
| 5 | 搜尋頁開啟原檔與在檔案總管顯示；路徑比對（`C:\A\報價`，`\` 與 `/` 視為相同） | T17、T11 |
| 6 | AI 軟體：Windows 的 `%APPDATA%`、`%LOCALAPPDATA%` 實際路徑；VS Code 與 LM Studio 設定檔位置（來自第三方資料，非官方）；各軟體實際回報的 `clientInfo.name`（目前是猜測） | T14、T18 |
| 7 | 問題回報：顯示卡名稱（登錄檔）、檔案總管選取 zip、日誌正被寫入時仍可複製、`C:\Users\<名稱>` 的遮罩效果 | T20 |
| 8 | 檔案共用違規（被別的程式鎖住的檔案）、隱藏屬性、OneDrive 雲端佔位檔、FileSystemWatcher 實際行為 | T10 |
| 9 | 真實中文 PDF：段落切分、閱讀順序（含雙欄）、「第 N 頁」頁尾移除、PDF 內嵌附件、JPEG 掃描檔 | T07 |
| 10 | `Contexo.Mcp.exe` 啟動：路徑含空白或中文、Windows 路徑在輸出裡的樣子、用真實 AI 軟體連線 | T13 |

### D. 留給 T22（端對端）的驗證

- 所有解析器與切塊都只用程式產生的測試檔驗證，沒有用真實 Word、PowerPoint、Excel、PDF 端對端跑過。T22 的測試語料請加入真實軟體另存的樣本（T05、T06、T08、T10）。
- 用真實的 bge 模型做端到端搜尋，評估品質（T11）；切塊品質要看真實解析器輸出（T09）。
- 確認 AI 軟體實際回報的 `clientInfo.name`（T14）。
- 十萬筆向量約 200 MB 記憶體與第一次搜尋的載入時間（T11、T13）；T13 的測試都是關鍵字模式，沒用真實模型，MCP 第一次搜尋的延遲沒量測。
- MCP 協定版本：SDK 2.2.0 的 client 預設用沒有握手的新版協定，各 AI 軟體實際用哪個版本、「已連線」記錄是否都能觸發，要用真實軟體確認（T13、T14）。

### E. T22 發現的缺陷（尚未指派）

T22 用 40 個程式產生的檔案與 42 個查詢跑完整條路徑。預設模型（bge-small-zh int8）Recall@3 = 0.90，高於門檻 0.8；只用關鍵字則為 0.74。詳細重現步驟與品質報告見 `task/T22-end-to-end.md` 的完成紀錄。題數很小（42 題），數字只當方向。

| # | 嚴重度 | 缺陷 | 影響的任務 | 建議 |
|---|---|---|---|---|
| E1 | 高 | **PDF 文字層的部首字元沒有正規化。** 真實 Chrome 產生的中文 PDF 讀成 `三⽇`、`領⽤`、`⼆千五百元`（康熙部首區 U+2F00～U+2FDF，看起來一樣但編碼不同）；`項⺫`（U+2EEB）連 NFKC 都轉不回來，需要自己的對照表；連字 `ﬁ` 也有。用一般字查詢時，正規化版的排名較前，有一題原樣版完全找不到。 | T07 | 先在 Windows 確認 Word 另存的 PDF 是否也會這樣，再決定要不要修。是靜默失敗，使用者不會察覺。 |
| E2 | 中 | **真實中文 PDF 版面還原不佳**：頁首與第一行正文黏成一行且沒被移除、雙欄閱讀順序錯亂、表格儲存格黏在一起、標題與下一段黏在一起。 | T07 | 等有真實文件的回饋再決定。 |
| E3 | 中 | **關鍵字模式對整句中文問句幾乎找不到**（整句只拆成 3 字元子字串）。 | T11 | 只在沒有模型時才影響使用者；安裝檔會帶模型，實際影響小。 |
| E4 | 中 | **大表摘要片段對分析型問句召回很差**：「哪個客戶去年下單金額最高」預設模型前 10 名找不到銷售明細，說出檔名的問法則排第 1。 | T08、T09 | 之後可考慮在摘要中加入更多欄位語意描述。 |
| E5 | 已知 | **內嵌 Excel 大表**：登記了 `TableId`，但摘要片段的檔名是 `package.xlsx`；摘要只有前 5 列可搜尋，其餘列既搜不到也查不到。直接呼叫 `DescribeAsync` 會丟誤導訊息（T13 的防護讓 MCP 回白話說明）。 | T08、T10、T12 | 同 A-1。 |
| E6 | 低 | 英文切塊的重疊會在單字中間切開（片段開頭 `ning on the content…`）。 | T09 | 中文為主的使用情境影響小。 |
| E7 | 資訊 | Word 與 PDF 解析器的例外訊息是英文（`WordParser.cs` 第 33、43、54、78 行；`PdfParser.cs` 第 52、53、138 行），其他解析器是中文。目前沒有任何畫面會顯示 `DocumentRecord.ErrorMessage`。 | T05、T07 | 之後若畫面要顯示再統一。 |

另外 T22 也確認了正確運作的行為：資料夾監看器 3.8 秒內反映修改、刪除、改名（改名保留同一個文件）；資料夾消失再出現時資料不動、不重建；壞檔逐一標失敗、不影響其他檔案；大量刪除詢問兩條路都對；Big5、修訂追蹤、註腳、內嵌 docx、連接線、SmartArt、圖表與備忘稿、兩層表頭、公式快取值都正確；母片文字沒有外洩。

### F. 保留的任務

- **T21（打包與安裝程式）**：維運者決定保留，等搬到 Windows 環境再做。目前已在 macOS 上用 `win-x64` 自包含模式交叉發布 `Contexo.Desktop` 與 `Contexo.Mcp`，確認可以編譯、產物是 Windows 執行檔且原生檔齊全（Skia、HarfBuzz、ONNX Runtime、SQLite）；ReadyToRun 要在 Windows 上驗證。搬到 Windows 後建議先跑 `dotnet build Contexo.slnx -warnaserror` 與 `dotnet test`。

## 給派工者的提醒

- 派工時給子代理：儲存庫、分支基準（`main`）、任務檔路徑，並要求先讀 `AGENTS.md`。
- 桌面程式用 Avalonia，**macOS 可以實際執行與操作**（只用於開發驗證，不發布 Mac 版）。畫面任務（T15～T20）以 Headless 自動測試加 Mac 實際操作驗收；Windows 專屬項目（登錄檔、系統匣位置、安裝程式）彙整到 `tests/manual/CHECKLIST.md`，在 T21 前於 Windows 統一確認。
- T00 已判定通過，但 POC 尚未逐項實測：T01 會先編譯 POC，T15 與 Windows 檢查表會補上注音輸入的確認。發現阻擋性問題時停止畫面任務並回報。
- 需要網路的步驟：NuGet 還原、模型下載（huggingface.co）、查閱 AI 軟體設定格式文件。環境無法連線時子代理會停下來回報。
- 每個任務完成後檢查任務檔頂端「狀態」與「完成紀錄」。

## 派工指南

### 進度追蹤

每個任務的狀態寫在任務檔頂端（待辦／進行中／完成）。派工前確認相依任務都是「完成」且已合併到 `main`。

### 建議順序

1. **T01**：單獨一個子代理。完成並合併後才進入下一步。
2. **波次 1**：T02～T09、T15 共 9 個，可同時派給 9 個子代理（或依可用數量分批）。建議優先：T02、T03、T09（波次 2 的關鍵路徑）、T15（所有畫面任務的前提）。
3. **波次 2**：T10、T11、T12、T14。
4. **波次 3**：T13、T16～T20。
5. **波次 4**：T21（需 Windows）、T22。

關鍵路徑：T01 → T02／T03／T09 → T10 → T16／T19 → T21。

### 執行環境需求

- .NET 10 SDK、可連線 `api.nuget.org`。
- T03、T22 需要連線 `huggingface.co` 下載模型。
- T14 需要查閱各 AI 軟體官方文件（網路搜尋）。
- 畫面任務（T15～T20）建議在 macOS 上執行，以便實際操作；T21 需要 Windows。

### 派工提示範本

```
你負責 Contexo 專案的任務 {Txx}。

儲存庫：https://github.com/NEILLOG/Contexo（以 main 為基準）

請依序：
1. 完整閱讀 AGENTS.md。
2. 閱讀 task/README.md 與 task/{Txx 檔名}.md，以及任務檔「必讀」列出的文件。
3. 確認相依任務的狀態都是「完成」；不是的話停止並回報。
4. 開分支 task/{Txx}-{簡短英文描述}，只修改任務檔「可修改範圍」內的檔案。
5. 完成所有驗收條件，執行 dotnet build Contexo.slnx -warnaserror 與 dotnet test 確認通過。
6. 把任務檔狀態改為「完成」並填寫完成紀錄，提交並推送分支（不開 PR，由維運者檢查後直接合併到 main）。

遇到以下情況請停止並回報，不要自行繞過：
- 需要修改共用契約（src/Contexo.Core/Abstractions/）或範圍外的檔案
- 需要新增 AGENTS.md 技術棧以外的套件
- 無法連線 NuGet 或 Hugging Face
- 規格有矛盾或不清楚
```

## 任務檔格式

每個任務檔包含：狀態、相依、目標、必讀、要做、不做、可修改範圍、實作要點與已知陷阱、驗收條件、完成紀錄。「可修改範圍」以外的檔案一律不動。

## 第二階段（尚未拆分）

圖片 OCR 與視覺模型、掃描型 PDF、Outlook .msg / .eml、Visio、ODF、壓縮檔、舊版 Office 格式、公司伺服器模式（匯入設定、模式切換警語）、Reranker。
