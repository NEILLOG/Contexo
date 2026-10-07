# T05 Word 解析器

- **狀態**：待辦
- **波次**：1
- **相依**：T01
- **必讀**：`AGENTS.md`、`plan/02-ingestion.md`（Word）、`src/Contexo.Core/Abstractions/Parsing.cs`、`Tables.cs`

## 目標

實作 `Parsing.Word.WordParser`（`.docx`），保留章節結構與表格，輸出依閱讀順序排列的區段。

## 要做

用 `DocumentFormat.OpenXml` 以唯讀方式開啟（`WordprocessingDocument.Open(stream, false)`）。

1. **章節**：
   - 段落樣式為內建標題（`Heading1`～`Heading9`，或樣式定義中 `outlineLvl` 0～8，或中文樣式名「標題 1」等）時視為標題；需要從 `StyleDefinitionsPart` 解析樣式繼承（`basedOn`）。
   - 沒用樣式的標題：同時滿足「整段粗體」、「字數 ≤ 40」、「字級大於內文的眾數字級」、「下一段是非標題內文」時視為標題，層級依字級由大到小排序推定。
   - 每遇到標題就結束前一個區段；區段的 `HeadingPath` 為目前的標題階層。
2. **內文**：段落文字依序串接，段落之間換行；清單項目前加「- 」；分頁符與分節不產生文字。
3. **修訂追蹤**：取「接受所有修訂」後的結果：保留 `w:ins` 內容，略過 `w:del` / `w:delText`、`w:moveFrom`。
4. **表格**：轉成 `TableModel`，`gridSpan` → ColSpan，`vMerge`（`restart` 開始、續接者省略）→ RowSpan；巢狀表格把內層表格的文字放進外層儲存格。表格前第一列若被標記為 `tblHeader`，`HeaderRowCount` = 連續的表頭列數；否則為 1（第一列視為表頭）。每個表格一個 `Table` 區段（`KeepWhole=true`），沿用目前的 `HeadingPath`。
5. **文字方塊與圖形**：抓取 `w:txbxContent` 與 `wps:txbx` 裡的段落，接在所在段落之後。
6. **註腳、尾註、註解**：註腳與尾註附在該段之後，格式「〔註〕內容」；註解（comments）附在文件最後一個區段，格式「〔註解〕內容」。
7. **略過**：自動目錄（`w:sdt` 且 `docPartGallery` 為 Table of Contents，或 TOC 欄位碼範圍內的段落）、頁首、頁尾。
8. **內嵌檔案與圖片**：用 `OfficeEmbeddedContent.ExtractEmbeddedFiles` / `ExtractImages`（來源為 `MainDocumentPart`），位置帶目前的 `HeadingPath`。
9. **錯誤**：受密碼保護（OLE 加密容器，`OpenXmlPackageException` 或檔頭為 `D0 CF 11 E0`）→ `PasswordProtected`；其他無法開啟 → `Corrupted`。
10. 文件標題：`Title` 屬性留空（檔名由切塊階段加上）。

## 不做

- 舊版 `.doc`。
- 頁碼（docx 沒有可靠頁碼）。
- 圖片內容辨識。

## 可修改範圍

- `src/Contexo.Core/Parsing/Word/**`
- `tests/Contexo.Core.Tests/Parsing/Word/**`

## 實作要點與已知陷阱

- 不要依賴 `InnerText`：它會把 `w:delText`、欄位碼（`w:instrText`）一起帶出來。逐一走訪 `w:r`/`w:t`，並處理 `w:tab`（→ `\t`）、`w:br`、`w:cr`（→ 換行）、`w:sym`。
- 欄位：`w:fldChar begin … separate … end` 之間，`separate` 之前是欄位碼，之後才是顯示結果。只取顯示結果。
- `vMerge` 沒有 `val` 屬性代表「續接」。
- 測試檔一律在測試中用 OpenXml SDK 產生（建立一個 `DocxBuilder` 測試輔助類別，放在測試資料夾內）。

## 驗收條件

`dotnet test --filter FullyQualifiedName~Parsing.Word` 全部通過，至少涵蓋：

1. 三層標題樣式 → 區段的 `HeadingPath` 正確；中文樣式名的標題也能辨識。
2. 沒有樣式、只有粗體大字的標題能被辨識；普通的粗體短句（字級與內文相同）不會被誤判。
3. 修訂：插入的文字出現、刪除的文字不出現。
4. 表格：橫向與縱向合併輸出正確的 `colspan` / `rowspan`；巢狀表格文字不遺失。
5. 文字方塊內容被取出。
6. 目錄、頁首頁尾不出現在結果中；欄位碼（如 `PAGE`、`HYPERLINK`）不出現，顯示文字有出現。
7. 註腳與註解內容有出現。
8. 內嵌一個 xlsx 時，`EmbeddedFiles` 有一筆，`FileName` 副檔名為 `.xlsx`。
9. 非 docx 的位元組（例如隨機資料）拋 `DocumentParseException(Corrupted)`；OLE 檔頭拋 `PasswordProtected`。
10. 1000 段落的文件解析在 2 秒內完成。

## 完成紀錄

（由執行者填寫）
