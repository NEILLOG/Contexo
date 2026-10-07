# T04 文字類解析器

- **狀態**：待辦
- **波次**：1
- **相依**：T01
- **必讀**：`AGENTS.md`、`plan/02-ingestion.md`（其他格式）、`src/Contexo.Core/Abstractions/Parsing.cs`

## 目標

實作純文字、HTML、RTF 三個解析器。

## 要做

### 1. 編碼偵測

使用 T01 提供的 `Common/TextDecoder`（BOM → 嚴格 UTF-8 → UTF.Unknown → Big5）。不要自行另寫一套。

### 2. `PlainTextParser`（`.txt .md .markdown .json .xml .log`）

- `.md` / `.markdown`：以 `#` 標題切成多個 `Prose` 區段，`HeadingPath` 依標題層級累積；程式碼區塊不拆。
- `.txt` / `.log`：一個 `Prose` 區段（切塊交給 T09）。`.log` 超過 `MaxExtractedChars` 時只保留**最後**的部分並加警告（日誌重點通常在後面）。
- `.json`：格式正確時以縮排後的文字輸出；錯誤時當純文字。
- `.xml`：去除標籤，只保留文字節點（以換行分隔）；格式錯誤時當純文字。
- 空檔案回傳 `ParsedDocument.Empty`。

### 3. `HtmlParser`（`.html .htm`）

用 HtmlAgilityPack：

- 移除 `script`、`style`、`noscript`、`nav`、`footer`、`header`。
- `h1`～`h6` 建立 `HeadingPath` 並切分區段。
- `table` 轉成 `TableModel`（處理 `rowspan`、`colspan`、`th`）再用 `HtmlTableRenderer` 輸出，成為 `Table` 區段（`KeepWhole=true`）。
- 其餘區塊元素以換行分隔，HTML 實體解碼。
- 編碼：先看 `<meta charset>`，沒有再用 `TextDecoder`。

### 4. `RtfParser`（`.rtf`）

用 RtfPipe 轉成 HTML，再交給 `HtmlParser` 相同的處理流程（抽成共用的 internal 方法，不要複製程式碼）。RtfPipe 失敗時拋 `DocumentParseException(Corrupted)`。

## 不做

- `.mht` / `.mhtml`（第二階段）。
- `.csv`（屬於 T08）。

## 可修改範圍

- `src/Contexo.Core/Parsing/Text/**`
- `tests/Contexo.Core.Tests/Parsing/Text/**`、`tests/Contexo.Core.Tests/Fixtures/Text/**`

## 實作要點與已知陷阱

- `CodePagesEncodingProvider` 已在 `AddContexoCore()` 註冊；單元測試若直接建立解析器，要在測試的靜態建構式自行註冊一次。
- Big5 測試檔在測試中用 `Encoding.GetEncoding(950).GetBytes(...)` 產生，不要提交二進位檔。
- 所有區段的 `Location.EmbeddedPath` 要複製 `ParseContext.EmbeddedPath`。
- 不要 dispose `ParseContext.Content`。

## 驗收條件

`dotnet test --filter FullyQualifiedName~Parsing.Text` 全部通過，至少涵蓋：

1. UTF-8（有／無 BOM）、UTF-16 LE BOM、Big5 三種編碼的 .txt 都能正確還原繁中文字（驗證解析器有正確使用 `TextDecoder`）。
2. Markdown 多層標題的 `HeadingPath` 正確；程式碼區塊內的 `#` 不被當成標題。
3. HTML：script/style 被移除；含 `rowspan`/`colspan` 的表格輸出正確的 HTML；標題切分正確；`&nbsp;`、`&amp;` 解碼。
4. RTF：產生一段含中文（`\uN?` 跳脫）與表格的 RTF，能取出文字與表格。
5. JSON / XML 正確與損壞兩種情況都不拋例外。
6. 空檔、只有空白的檔案回傳空結果。
7. `EmbeddedPath` 有被帶入。

## 完成紀錄

（由執行者填寫）
