# T08 Excel / CSV 解析器

- **狀態**：完成
- **波次**：1
- **相依**：T01
- **必讀**：`AGENTS.md`、`plan/02-ingestion.md`（Excel，整段必讀）、`src/Contexo.Core/Abstractions/Parsing.cs`、`Tables.cs`

## 目標

實作 `Parsing.Spreadsheet.SpreadsheetParser`（`.xlsx .xlsm .csv`）與 `Parsing.Spreadsheet.SpreadsheetRegionReader`。

核心策略：**依大小分流，不判斷用途**。小表與表單整張轉 HTML；大表只產生「表格說明」做向量，內容之後由 T12 用 SQL 查詢。

## 要做

### 1. 讀取儲存格（`.xlsx` / `.xlsm`）

用 `SpreadsheetDocument.Open(stream, false)` 與 SAX 方式（`OpenXmlReader`）讀取工作表，避免大檔把 DOM 全部載入記憶體。

- 依 `workbook.xml` 的順序處理工作表。**隱藏工作表**（`state="hidden"` 或 `"veryHidden"`）第一版略過，並加一筆警告 `hidden-sheet:{名稱}`。
- 儲存格顯示字串：
  - 共用字串、行內字串、布林（TRUE/FALSE）、錯誤值（原樣，如 `#N/A`）。
  - 公式：取快取值 `<v>`，不取公式本身。
  - 數字：依儲存格樣式的 `numFmtId` / 自訂格式判斷：日期時間格式（內建 14～22、45～47，或自訂格式含 `y m d h s` 且不在引號中）→ OADate 轉成 `yyyy/MM/dd`（有時間時加 `HH:mm`）；百分比格式 → 乘 100 加 `%`；其餘用 `G15` 一般格式（不加千分位）。
- 合併儲存格（`mergeCells`）：記錄合併範圍。
- 圖表、圖片、內嵌檔案：用 `OfficeEmbeddedContent` 對各 `WorksheetPart` 取出（`Location.Sheet` 設好）。

### 2. 讀取 `.csv`

- 用 `TextDecoder` 解碼；依前 20 行判斷分隔符（`,`、`;`、`\t`，取欄數最一致者）。
- 依 RFC 4180 處理引號、跳脫與欄位內換行。
- 整份 CSV 視為工作表名稱 `csv` 的單一區域。

### 3. 區域偵測（每個工作表）

1. 建立格子：非空儲存格為 true；合併範圍內所有格子都視為非空。
2. 以 **4 方向相鄰**的 flood fill 找出連通區域，再把邊界框互相重疊的區域合併。
3. 單一儲存格、且位於另一區域正上方 1～2 列內的區域，視為該區域的**標題**（`Caption`），不單獨成區。

### 4. 分流

- **整張工作表**非空格子數 ≤ `ParserOptions.SmallTableMaxCells`：把整個已使用範圍（含各區域與中間空白）轉成一個 `TableModel` → HTML，輸出一個 `Table` 區段（`KeepWhole=true`，`Location.Sheet`、`CellRange`、`Title` = 工作表名稱）。表單、報價單走這條。
- 否則逐區域：
  - 區域面積（列 × 欄）≤ `SmallTableMaxCells` → 同上，一個區域一個 `Table` 區段。
  - 較大的區域 → **大表**：
    1. 偵測表頭（見第 5 點）。
    2. 產生 `SpreadsheetTable`：`TableKey = "{Sheet}!{CellRange}"`、`Columns`、`HeaderRowCount`、`DataRowCount`、`SampleRows`（前 `TableSampleRows` 列）、`Description`。
    3. 輸出一個 `TableSummary` 區段，`Text` = `Description`，`TableKey` 相同，`KeepWhole=true`。

`Description` 格式（給向量與 AI 閱讀，繁體中文）：

```
檔案：{FileName}
工作表：{Sheet}（範圍 {CellRange}，共 {DataRowCount} 筆資料）
表格標題：{Caption}            ← 有才輸出
欄位：{欄1}、{欄2}、…
範例資料：
{欄1}={值}；{欄2}={值}；…
{欄1}={值}；{欄2}={值}；…
（完整內容請使用 query_table 查詢）
```

### 5. 表頭偵測

在區域頂端最多看 4 列：

1. 跳過**裝飾列**：只有 1 個非空格子、且它是合併儲存格或寬度跨越區域一半以上 → 成為 `Caption`。
2. 某列「非空格子中文字（非數字、非日期）比例 ≥ 0.6」且「下一列的文字比例較低，或下一列非空格子多為數字/日期」→ 為表頭列。粗體或有填滿色的列加分（比例門檻降到 0.4）。
3. 連續的表頭列都算（多層表頭）；先把合併儲存格的值水平填滿，再把同一欄各層以 `_` 串接（相同的相鄰層只保留一次）。
4. 找不到表頭時 `HeaderRowCount = 0`，欄名用 `欄A`、`欄B`…（Excel 欄字母）。
5. 欄名空白 → `欄{字母}`；重複 → 加 `_2`、`_3`。

把表頭邏輯寫成獨立的 `internal static class HeaderDetector`，讓 `SpreadsheetRegionReader` 重複使用。

### 6. `SpreadsheetRegionReader : ISpreadsheetRegionReader`

- 重新開啟檔案（`FileShare.ReadWrite | FileShare.Delete`，因為使用者可能正開著 Excel），讀取指定工作表與範圍。
- 套用與解析時相同的 `HeaderDetector` 欄名邏輯（以傳入的 `headerRowCount` 為準），回傳 `SpreadsheetRegion`（不含表頭列，每列欄數與 `Columns` 相同）。
- 檔案不存在 → `FileNotFoundException`；工作表不存在或範圍超出 → `InvalidOperationException`，訊息寫明原因。

### 7. 錯誤

密碼保護 → `PasswordProtected`；無法開啟 → `Corrupted`。

## 不做

- 舊版 `.xls`、`.ods`。
- SQL 查詢本身（T12）。
- 樞紐分析表定義（只取顯示結果）。

## 可修改範圍

- `src/Contexo.Core/Parsing/Spreadsheet/**`
- `tests/Contexo.Core.Tests/Parsing/Spreadsheet/**`

## 實作要點與已知陷阱

- 儲存格參照 `r="AB12"` 要自己解析成欄列索引；有些產生器會省略 `r`，此時依順序遞增。
- 樣式索引 `s` 對應 `cellXfs`，再對應 `numFmtId`；自訂格式在 `numFmts`。
- 1900 日期系統的 OADate 用 `DateTime.FromOADate`；`workbookPr date1904="1"` 時要加 1462 天。
- 大型工作表（10 萬列）只需要掃描一次就能決定區域與表頭；不要為了區域偵測把所有字串長期保存，取樣本列即可。
- 測試檔在測試中用 OpenXml SDK 產生（建立 `XlsxBuilder` 輔助類別），包含合併儲存格、日期格式、公式快取值。
- 參考做法：Docling 的 flood fill、xldetect 的表頭判斷。不要引入 Python 套件。

## 驗收條件

`dotnet test --filter FullyQualifiedName~Parsing.Spreadsheet` 全部通過，至少涵蓋：

1. **報價單表單**（約 15 列 × 6 欄，含合併的標題與分散的欄位）→ 整張一個 `Table` 區段，HTML 含正確 `colspan`。
2. **清單型大表**（標題列 + 500 列資料）→ 一個 `TableSummary` 區段與一筆 `SpreadsheetTable`；欄名正確、`DataRowCount` = 500、範例 5 列、`Description` 符合格式。
3. **一頁兩表**（中間隔兩列空白，各 300 列）→ 兩個大表，範圍正確。
4. **裝飾標題列**（第一列合併 A1:F1 寫「2025 年度銷售」）→ 成為 Caption，不當欄名。
5. **兩層表頭**（「第一季」合併跨三欄，下層「一月、二月、三月」）→ 欄名「第一季_一月」等。
6. 日期格式、百分比、公式快取值、布林、錯誤值顯示正確；date1904 正確。
7. 隱藏工作表被略過並有警告。
8. CSV：逗號、分號、Tab 分隔；引號內含逗號與換行；Big5 編碼。
9. `SpreadsheetRegionReader` 讀回第 2、5 項的區域，欄名與解析時一致、資料列數正確；檔案被另一個 `FileStream` 以讀寫鎖開著時仍可讀。
10. 10 萬列 × 10 欄的大表解析在 10 秒內完成，記憶體峰值合理（不保留全部列）。

## 完成紀錄

**做了什麼**

- `src/Contexo.Core/Parsing/Spreadsheet/` 實作 `SpreadsheetParser`（`.xlsx .xlsm .csv`）與 `SpreadsheetRegionReader`，未改動共用契約與範圍外檔案。
- 檔案分工：
  - `CellAddress.cs`（欄列轉換、`CellRect`）、`SheetModels.cs`（`SheetCell`、`ISheetCells`、`RectData`）。
  - `XlsxWorkbook.cs`（開檔、工作表清單、共用字串、密碼偵測）、`XlsxStyles.cs`（樣式、日期／百分比／粗體／填色判斷與數字顯示）、`XlsxSheetCells.cs`（逐格串流讀取）。
  - `CsvSheetCells.cs`（RFC 4180、分隔符偵測、型別猜測）。
  - `SheetScan.cs`（只記錄「哪些格子非空」，每列幾個整數，十萬列也不佔記憶體）、`RegionDetector.cs`（flood fill、邊界框合併、標題格）。
  - `HeaderDetector.cs`（獨立的 `internal static class`，供解析與 `SpreadsheetRegionReader` 共用）、`SheetReading.cs`（一次掃描取多個矩形、串流讀整個範圍）、`SmallTableBuilder.cs`（`TableModel`、合併儲存格轉 colspan／rowspan）。
- 測試在 `tests/Contexo.Core.Tests/Parsing/Spreadsheet/`（`XlsxBuilder` 以 OpenXml SDK 產生測試檔）。

**驗收結果**

- `dotnet build Contexo.slnx -warnaserror`：0 警告、0 錯誤。
- `dotnet test --filter FullyQualifiedName~Parsing.Spreadsheet`：106 個全數通過；全方案 `dotnet test` 全數通過（Core 265、Mcp 5、App 1、Desktop 1）。
- 逐項對應：1 報價單（colspan）、2 清單大表（500 列，Description 逐行比對）、3 一頁兩表、4 裝飾標題列與獨立單格標題、5 兩層表頭（`第一季_一月`）、6 日期／日期時間／時間／百分比／公式快取值／布林／錯誤值／date1904、7 隱藏工作表警告 `hidden-sheet:{名稱}`、8 CSV（逗號、分號、Tab、引號內逗號與換行、Big5、UTF-8 BOM）、9 `SpreadsheetRegionReader`（第 2、5 項區域、CSV、檔案被 `FileStream` 以 `FileShare.ReadWrite` 開著仍可讀、檔案不存在／工作表不存在／範圍超出的例外）、10 十萬列 × 10 欄解析約 1 秒（含測試檔產生，整個測試 2 秒內），遠低於 10 秒。
- 另外涵蓋：密碼保護（`PasswordProtected`）、損毀檔（`Corrupted`）、`.xlsm`、行內字串與省略 `r` 的儲存格、內嵌檔與圖片（`OfficeEmbeddedContent`）、`MaxExtractedChars` 截斷、取消。

**無法在此環境驗證**

- 沒有真實 Excel 產生的檔案；測試檔全由 OpenXml SDK 產生。Excel 實際輸出的邊角情形（例如樣式繼承、特殊自訂格式）需用真實檔案再看一次。
- 所有測試在 macOS 執行；無 Windows 專屬程式碼。

**與規格不同的地方及理由**

1. 讀取工作表用 `System.Xml.XmlReader`（對 `WorksheetPart` 的串流）而不是 `OpenXmlReader`：同樣是 SAX、不載入 DOM，但速度明顯較快，十萬列 × 十欄需要。工作簿、樣式等小檔案仍用 OpenXml SDK 物件模型。
2. 合併儲存格只有「左上角儲存格有值」才把範圍內所有格子視為非空；空白的合併區域（純排版）不會把原本分開的區域連在一起。
3. 裝飾列判斷：單一非空格子且為合併儲存格，且合併寬度不少於區域寬度一半才算（規格寫「合併儲存格或寬度跨越一半以上」）。原因是「第一季」這類只佔表頭上層一部分欄位的群組標題不應被當成標題。另外「單一非空格子後面緊接空白列」（CSV 開頭的標題行）也算裝飾列，並跳過其後的空白列。
4. 表頭偵測：多層表頭（兩列以上）要有結構證據（區塊內有合併儲存格，或上層列的非空格子比下層少）才成立，避免把全文字資料列誤判成表頭；找不到文字內容線索時，若首列粗體或有填色且第二列沒有，視為單層表頭（規格「粗體或有填滿色加分」的延伸）。
5. 內建數字格式除規格列出的 14～22、45～47 外，也把東亞日期／時間 id（27～36、50～58）視為日期；`[h]:mm` 之類經過時間格式顯示為 `HH:mm`（可超過 24 小時）；`numFmtId` 46 同。民國年（`e`）格式仍以西元顯示。
6. 整張工作表很小、但格子散落很遠（例如只有 A1 與 Z500）時，轉 HTML 會略過完全空白的列與欄（面積超過 `SmallTableMaxCells` 的 4 倍才啟用），避免輸出幾百列空白；`CellRange` 仍是完整範圍。
7. 小表區域（面積 ≤ `SmallTableMaxCells`）一樣套用表頭偵測：有表頭時輸出 `<thead>`，有標題時放進 `<caption>`；整張工作表走小表時（表單）不做表頭偵測。
8. 大表的 `CellRange` 不含裝飾標題列（`HeaderRowCount` 是 `CellRange` 頂端的表頭列數，所以標題不能在範圍內）；標題文字放在 `Description` 的「表格標題」。
9. 單一儲存格的 `CellRange` 寫成 `A1`（不是 `A1:A1`）；`SpreadsheetRegionReader` 兩種寫法都接受。
10. 圖表（`ChartPart`）未取出內容，只處理 `OfficeEmbeddedContent` 能取的內嵌檔案與圖片（工作表本身與其 `DrawingsPart`）。
11. 測試命名空間用 `Contexo.Core.Tests.Parsing.SpreadsheetTests`（而不是 `...Parsing.Spreadsheet`），因為既有的 `OfficeEmbeddedContentTests` 在 `Contexo.Core.Tests.Parsing` 下以別名 `Spreadsheet` 指向 OpenXml 命名空間，同名子命名空間會讓它編譯失敗。測試檔仍在 `Parsing/Spreadsheet/` 資料夾，`--filter FullyQualifiedName~Parsing.Spreadsheet` 可涵蓋。

**留給後續任務的注意事項**

- T12：`SpreadsheetRegionReader` 回傳的 `Columns` 與解析時 `SpreadsheetTable.Columns` 完全一致（同一個 `HeaderDetector.BuildColumnNames`），列資料是顯示字串（日期 `yyyy/MM/dd`、百分比 `25.6%`、數字 `G15`），空儲存格為 `null`。範圍超出現有資料、工作表不存在丟 `InvalidOperationException`，檔案不存在丟 `FileNotFoundException`，檔案損毀或有密碼丟 `DocumentParseException`。`TableKey` 為 `{工作表}!{CellRange}`，CSV 的工作表名稱固定是 `csv`。
- T09：`TableSummary` 與 `Table` 區段都設 `KeepWhole=true`；`Table` 的 HTML 由 `HtmlTableRenderer` 產生；大表說明文字可能略長（欄位多時），但範例值每格最多 100 字。
- T10：解析器不保留 `context.Content`，也不關閉它；`ParseAsync` 內部用 `Task.Run`，大檔不會卡住呼叫端執行緒。
- `.xls`、`.ods` 不支援（`.xls` 副檔名不在 `SupportedExtensions`）。
