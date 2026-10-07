# T07 PDF 解析器

- **狀態**：待辦
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

（由執行者填寫）
