# T07 PDF 解析器

- **狀態**：完成
- **波次**：1
- **相依**：T01
- **必讀**：`AGENTS.md`、`plan/02-ingestion.md`（PDF）、`src/Contexo.Core/Abstractions/Parsing.cs`

## 目標

實作 `Parsing.Pdf.PdfParser`（`.pdf`），抽出文字型 PDF 的文字，每頁一個區段並帶頁碼。

## 要做

使用 UglyToad.PdfPig：

1. 每頁一個 `Prose` 區段，`Location.Page` 為 1 起算頁碼。
2. 文字擷取：使用 PdfPig 的版面分析（`NearestNeighbourWordExtractor` + `DocstrumBoundingBoxes` 或該版本建議的方式）組出段落，段落間換行；不要直接用 `page.Text`（常常沒有空白與換行）。
3. 頁首頁尾去除：統計每頁最上方與最下方 1 行文字，出現在超過一半頁面（且頁數 ≥ 3）的相同文字（去除數字後比較，以排除頁碼）視為頁首頁尾並移除。
4. 中文斷行修正：一行結尾與下一行開頭都是 CJK 字元時，直接接起來不加空白。
5. **掃描型偵測**：整份 PDF 抽出的非空白字元數 / 頁數 < 20 時，視為掃描檔：回傳空的 `Sections`，加入警告 `"scanned-pdf"`，並收集每頁的圖片到 `Images`（給第二階段 OCR）。只收集前 50 頁，避免記憶體爆量。
6. 密碼保護（PdfPig 拋出加密相關例外）→ `PasswordProtected`；無法開啟 → `Corrupted`。
7. PDF 附件（embedded files）：若 PdfPig 提供 API 就取出到 `EmbeddedFiles`；沒有則略過。

## 不做

- OCR（第二階段）。
- 表格結構還原（第一版 PDF 表格當文字處理）。

## 可修改範圍

- `src/Contexo.Core/Parsing/Pdf/**`
- `tests/Contexo.Core.Tests/Parsing/Pdf/**`、`tests/Contexo.Core.Tests/Fixtures/Pdf/**`

## 實作要點與已知陷阱

- 測試用 PDF：用 PdfPig 的 `PdfDocumentBuilder` 在測試中產生。中文字型需要嵌入 TrueType 字型檔；若沒有可用且授權允許提交的中文字型，中文擷取測試改用英文與數字驗證邏輯，並在完成紀錄說明需在 Windows 以真實中文 PDF 人工驗證。
- 不要提交第三方 PDF 或字型，除非授權明確允許（例如 SIL OFL 的 Noto Sans TC 子集可以，需附授權檔）。
- 大型 PDF：逐頁處理，處理完就釋放，受 `MaxExtractedChars` 限制。

## 驗收條件

`dotnet test --filter FullyQualifiedName~Parsing.Pdf` 全部通過，至少涵蓋：

1. 3 頁 PDF 產生 3 個區段，頁碼正確，文字有空白與換行。
2. 每頁相同的頁首與「第 N 頁」頁尾被移除；只出現在少數頁的文字保留。
3. 沒有文字層的 PDF（只有一張圖）被判定為掃描檔，`Sections` 為空、警告含 `scanned-pdf`。
4. 加密 PDF（測試中產生；若 PdfPig 無法產生加密檔，以模擬例外的方式測試對應邏輯）→ `PasswordProtected`。
5. 隨機位元組 → `Corrupted`。
6. CJK 斷行合併的邏輯以單元測試（純字串輸入）驗證。

## 完成紀錄

**做了什麼**

- `src/Contexo.Core/Parsing/Pdf/PdfParser.cs`：實作 `PdfParser`（`.pdf`）。PdfPig 逐頁處理：`NearestNeighbourWordExtractor` 取字 → `DocstrumBoundingBoxes` 分段 → `UnsupervisedReadingOrderDetector` 排閱讀順序；Docstrum 失敗時退回「依垂直位置分列」的備援。每頁一個 `Prose` 區段，`Location.Page` 為 1 起算頁碼，`EmbeddedPath` 沿用 `ParseContext`。
- `src/Contexo.Core/Parsing/Pdf/PdfTextLayout.cs`：不含 PdfPig 型別的純邏輯（CJK 斷行合併、頁首頁尾偵測、頁面文字組裝），可單元測試。
- 測試：`tests/Contexo.Core.Tests/Parsing/Pdf/`（`PdfTestFiles` 產生測試檔、`PdfParserTests`、`PdfTextLayoutTests`）。沒有提交任何 PDF 或字型檔，全部由程式產生。

**行為細節**

- 段落（Docstrum 區塊）之間以空白行（`"\n\n"`）分隔；同一段落內的換行保留為 `"\n"`，只有「前一行最後一字與下一行第一字都是 CJK 字元（漢字、假名、諺文、注音）」時直接接起來。標點（例如「。」「，」）不算 CJK 字元，因此句末換行會保留。
- 頁首頁尾：頁數 ≥ 3 才處理；每頁取最上方一列與最下方一列（同一垂直範圍內的多個文字片段合成一列），比較時去掉數字與空白、轉小寫；同樣的 key 出現在「超過一半」頁面（`count * 2 > 頁數`，剛好一半不算）才移除。頁首與頁尾分開統計。
- 掃描型偵測：以頁面抽出的非空白字元數（頁首頁尾移除前）÷ 總頁數 < 20 判定。掃描檔回傳空 `Sections`、警告 `scanned-pdf`，並收集前 50 頁的圖片（PdfPig 能轉 PNG 就用 PNG，否則若是 JPEG 位元組則原樣輸出，其他格式略過）；超過 50 頁多一個警告 `scanned-pdf-images-limited`。
- 例外對應：`PdfDocumentEncryptedException` → `PasswordProtected`；其他開啟失敗（含隨機位元組、空檔、截斷檔）→ `Corrupted`。單頁讀取失敗不整份失敗，加警告 `pdf-page-unreadable:N`；全部頁都失敗才 `Corrupted`。
- 超過 `MaxExtractedChars`：停止讀頁並截斷，警告 `pdf-text-truncated`。
- PDF 附件：用 `document.Advanced.TryGetEmbeddedFiles` 取出到 `EmbeddedFiles`（只取檔名部分）；失敗時略過。
- 不會 dispose 傳入的 Stream；解析在 `Task.Run` 內執行，頁與頁之間檢查取消。

**驗收結果（macOS，.NET 10.0.401）**

- `dotnet build Contexo.slnx -warnaserror`：0 警告、0 錯誤。
- `dotnet test --filter FullyQualifiedName~Parsing.Pdf`：通過 40、略過 0、失敗 0。
- `dotnet test`（全部）：Core 199、App 1、Mcp 5、Desktop 1，全數通過。
- 驗收 1：3 頁 PDF → 3 個區段、頁碼 1/2/3，文字含空白與換行，距離遠的段落以空白行分隔（`ThreePagePdf_...`、`ParagraphsFarApart_...`）。
- 驗收 2：5 頁 PDF，重複頁首與「Page N of 5」頁尾被移除；只出現一次的頁尾「Rare footnote」與只出現一次的頂端橫幅被保留（`RepeatedHeaderAndPageNumberFooter_...`，另有以合成版面資料測試「剛好一半不移除」「少於 3 頁不處理」「同列多片段」）。
- 驗收 3：只有圖片的 PDF → `Sections` 為空、警告含 `scanned-pdf`、圖片帶頁碼；52 頁只收 50 張；文字極少（每頁 5 字）也判為掃描。
- 驗收 4：測試中手寫一個帶 `/Encrypt` 字典且空密碼驗證必失敗的最小 PDF（PdfPig 無法產生加密檔），實際走完整解析 → `PasswordProtected`；另以 `PdfDocumentEncryptedException` 直接測試例外對應函式。
- 驗收 5：隨機位元組、空檔、截斷檔 → `Corrupted`。
- 驗收 6：CJK 斷行合併以純字串輸入測試（CJK+CJK 直接接、CJK+拉丁保留換行、標點不算、surrogate pair 擴充字集）。

**無法在目前環境驗證**

- **中文 PDF 的實際擷取**：測試用 PDF 只能用 Helvetica 標準字型（`PdfDocumentBuilder` 中文需嵌入 TrueType 字型，儲存庫內沒有可提交的授權字型），所以版面分析、頁首頁尾、掃描判定等都用英文驗證；CJK 斷行僅以純字串單元測試。請在 Windows 以真實中文 PDF（含直書或雙欄、有中文頁首頁尾「第 N 頁」）人工確認：段落切分、閱讀順序、頁首頁尾移除、`NearestNeighbourWordExtractor` 對沒有空白的中文是否合理。
- **PDF 附件（embedded files）**：程式已實作，但 `PdfDocumentBuilder` 無法產生附件，沒有自動化測試。
- **JPEG 掃描檔圖片**：JPEG 位元組輸出的路徑沒有測試（測試只涵蓋 PNG）。

**與規格不同或規格未明之處**

- 任務檔說「段落間換行」：實作為段落間一個空白行（`"\n\n"`），讓 T09 切塊可以依空白行切段；段落內換行為 `"\n"`。
- 規格建議的 `DocstrumBoundingBoxes` 之外，另用 `UnsupervisedReadingOrderDetector` 排序區塊（Docstrum 本身不保證閱讀順序）；未在多欄實際文件上驗證。
- 掃描判定使用頁首頁尾移除前的字元數。

**留給後續任務的注意事項**

- 新增的公開警告代碼（字串）：`scanned-pdf`、`scanned-pdf-images-limited`、`pdf-text-truncated`、`pdf-page-unreadable:N`；T10 若要把警告轉成白話，只有 `scanned-pdf` 是預期給使用者看的（「這份 PDF 是掃描檔，這個版本還讀不到文字」）。
- 擷取出的 PDF 附件由 T10 依 `EmbeddedFiles` 遞迴處理。
- Docstrum 對頁面字數很多時成本較高；T10 的逾時（`Timeout`）保護請照常套用。

