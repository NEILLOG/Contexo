# T05 Word 解析器

- **狀態**：完成
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

**分支**：`task/T05-word-parser`

**做了什麼**

- `WordParser`（`src/Contexo.Core/Parsing/Word/WordParser.cs`）：兩階段處理。第一階段依序讀出內文所有區塊（段落、表格，展開內容控制項、略過自動目錄），第二階段決定標題階層並組成區段。
- `WordStyleResolver.cs`：解析 `StyleDefinitionsPart`，沿 `basedOn` 繼承取得標題層級（`outlineLvl` 優先，其次樣式名稱「heading N」／「標題 N」，名稱與 id 都會比對）、清單、字級、粗體、目錄樣式；沒指定樣式時用預設段落樣式與 docDefaults。
- `WordTextExtractor.cs`：走訪 `w:r` 取字（`w:t`、`w:tab`、`w:br`、`w:cr`、`w:sym`、`w:noBreakHyphen`），不用 `InnerText`；接受修訂（保留 `ins`／`moveTo`，略過 `del`／`moveFrom`）；欄位碼與顯示結果分開（`begin/separate/end`，含巢狀、跨段落）；文字方塊（`txbxContent`，`mc:AlternateContent` 只讀第一個 Choice，避免和 VML Fallback 重複）；註腳、尾註（「〔註〕」）。每個文字方塊、註腳、表格儲存格各用獨立的欄位狀態。
- `WordBlocks.cs`：目錄內容控制項判斷、表格列／儲存格列舉（略過被修訂刪除的列、支援包在 sdt 內的儲存格）、`TableModel` 轉換（`gridSpan`→ColSpan、`vMerge`→RowSpan、`tblHeader` 連續列數否則 1、`gridBefore`）。
- `WordContext.cs`：整份文件共用的狀態（樣式、註腳尾註、圖片／內嵌檔的關聯 id 對照）。
- 測試：`tests/Contexo.Core.Tests/Parsing/Word/DocxBuilder.cs`（測試用 docx 產生器）與 `WordParserTests.cs`（27 個測試）。

**驗收條件**

`dotnet test --filter FullyQualifiedName~Parsing.Word`：通過 27、失敗 0。逐項對應：

1. 三層標題樣式、中文樣式名（「標題 1」）、`outlineLvl` 與樣式繼承 → `Three_level_heading_styles_give_heading_paths`、`Chinese_style_names_are_headings`、`Outline_level_and_style_inheritance_are_resolved`。
2. 粗體大字標題辨識、普通粗體短句不誤判 → `Bold_large_short_paragraphs_are_headings_by_size_rank`、`Ordinary_bold_short_sentence_is_not_a_heading`。
3. 修訂 → `Tracked_changes_are_read_as_accepted`。
4. 橫向／縱向合併、表頭列、巢狀表格 → `Table_with_merged_cells_keeps_colspan_and_rowspan` 等三項。
5. 文字方塊（含重複的 VML Fallback 只出現一次、純 VML）→ 兩項。
6. 目錄（內容控制項與 TOC 欄位）、頁首頁尾、`HYPERLINK`／`PAGE`／`DATE` 欄位碼不出現而顯示文字有出現 → `Toc_header_and_footer_are_skipped_and_field_codes_do_not_leak`。
7. 註腳、尾註、註解 → `Footnotes_endnotes_and_comments_are_included`、`Comments_go_into_a_new_section_when_the_document_ends_with_a_table`。
8. 內嵌 xlsx（`EmbeddedFiles` 一筆、副檔名 `.xlsx`）與圖片 → `Embedded_workbook_and_image_are_extracted_with_heading_path`、`Unreferenced_embedded_workbook_is_still_returned`。
9. 隨機位元組、空檔、非 docx 的 zip → `Corrupted`；OLE 檔頭 → `PasswordProtected`。
10. 1000 段落解析遠低於 2 秒（測試以 2 秒為上限）。

另有字元上限、取消、串流位置、清單項目前綴、tab／換行／分頁／符號等測試。`dotnet build Contexo.slnx -warnaserror` 成功（0 警告 0 錯誤）；`dotnet test` 全部通過（Core 186、App 1、Mcp 5、Desktop 1）。

**無法在目前環境驗證**

- 只用程式產生的 docx 測試，沒有用真實 Word／WPS 存出的檔案驗證（AGENTS.md 禁止提交真實文件）。真實檔案的 XML 變化較多，建議 T22 的測試語料加入 Word 另存的樣本。

**與規格不同或規格沒寫到的決定**

- 加密判斷：只看檔頭 `D0 CF 11 E0`（OLE 容器）→ `PasswordProtected`；其餘任何開啟失敗（含隨機資料）→ `Corrupted`。因為驗收條件要求隨機資料是 `Corrupted`，所以不用 `OpenXmlPackageException` 型別本身判斷。
- 沒有內文的標題（後面緊接同層或更高層標題、或文件結尾）會輸出一個只含標題文字的 `Prose` 區段（`HeadingPath` 為其上層路徑），避免標題文字消失。
- 第一個標題之前的內文，`HeadingPath` 是空陣列（不是 null）。
- 沒樣式標題的判斷：「下一段是非標題內文」允許後面接著另一個同樣條件的候選（例如總標題後緊接小標題），整串最後要接內文或表格才成立；字級取段落內最大字級，眾數以字元數加權，同數量取較小字級。
- 註解（comments）取註解檔內全部註解，附在最後一個 `Prose` 區段；最後一個區段是表格時（避免破壞 HTML）改成新增一個 `Prose` 區段。
- 樣式名稱為 `toc N`／`目錄 N` 的段落也視為自動目錄略過。
- 清單項目只加「- 」，不依層級縮排。
- 圖片的 `ContextText` 取所在段落文字，段落沒有文字時取前一個有文字的段落（最多 500 字）。內嵌檔與圖片的位置依「在內文被引用的位置」帶 `HeadingPath`；沒被引用的帶空陣列。`ParseContext.EmbeddedPath` 會複製到所有輸出位置。
- 超過 `MaxExtractedChars` 時停止讀取後續內容並加一則警告。

**給後續任務的注意事項**

- T09 切塊：Word 的 `Title` 一律為 null；表格區段 `KeepWhole = true`，`Text` 是 `HtmlTableRenderer` 的 HTML；`HeadingPath` 可能是空陣列。表格內的換行以 `<br>` 呈現。
- T10：內嵌檔遞迴解析時，把 `EmbeddedFile.FileName` 加到 `ParseContext.EmbeddedPath`。
- 沒有改動 `Abstractions/`、`Directory.Packages.props` 或範圍外檔案。
