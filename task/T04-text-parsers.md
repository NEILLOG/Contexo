# T04 文字類解析器

- **狀態**：完成
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

**做了什麼**

- `PlainTextParser`：`.txt .md .markdown .json .xml .log`。編碼一律走 `TextDecoder`。Markdown 以 ATX 標題（`#`～`######`，`#` 後須有空白）切段並累積 `HeadingPath`，``` 與 `~~~` 圍欄內不算標題；只有標題沒有內文的區段會被省略（標題已在子區段的 `HeadingPath` 內），但整份文件只有標題時仍保留。`.json` 用寬鬆選項（允許註解、結尾逗號）解析後縮排輸出且不跳脫中文，失敗改當純文字；`.xml` 禁用 DTD、只取文字節點與 CDATA、以換行分隔，失敗改當純文字；`.log` 超過 `MaxExtractedChars` 時保留最後部分並加警告，其餘格式超過時保留前面並加警告。
- `HtmlParser`：先看 BOM，其次 `<meta charset>`（含 http-equiv 形式，前 4096 位元組），都沒有再用 `TextDecoder`。實際處理在 `HtmlContentExtractor`（HtmlAgilityPack）。
- `RtfParser`：以 Latin-1 讀入，檢查 `{\rtf` 開頭（否則 `Corrupted`），用 `RtfPipe.Rtf.ToHtml` 轉成 HTML 後交給 `HtmlContentExtractor`；RtfPipe 例外轉成 `DocumentParseException(Corrupted)`。
- `HtmlContentExtractor`（HTML 與 RTF 共用）：移除 `script style noscript nav footer header`（另含 `head template svg iframe object`）；`h1`～`h6` 切段並建立 `HeadingPath`（標題文字同時是該段第一行）；區塊元素以換行分隔、`<br>` 換行、`<pre>` 保留換行；實體解碼、`&nbsp;` 轉空白；`table` 轉 `TableModel`（rowspan／colspan 以佔位表處理、`thead` 或開頭全為 `th` 的列視為表頭、`caption` 轉為表格標題、巢狀表格攤平成儲存格文字）後用 `HtmlTableRenderer` 輸出為 `Table` 區段（`KeepWhole=true`），表格前後的文字各自成為 `Prose` 區段並保留同一個 `HeadingPath`。遞迴深度超過 400 層時改取純文字，避免堆疊溢位。
- `ParserSupport`：三個解析器共用的小工具（取副檔名、讀位元組、`EmbeddedPath` 帶入、`MaxExtractedChars` 截斷）。
- 所有區段的 `Location.EmbeddedPath` 都複製 `ParseContext.EmbeddedPath`；不 dispose `ParseContext.Content`；空檔／只有空白一律回傳 `ParsedDocument.Empty`。

**修改的檔案**

- `src/Contexo.Core/Parsing/Text/`：`PlainTextParser.cs`、`HtmlParser.cs`、`RtfParser.cs`、`HtmlContentExtractor.cs`（新）、`ParserSupport.cs`（新）
- `tests/Contexo.Core.Tests/Parsing/Text/`：`PlainTextParserTests.cs`、`HtmlParserTests.cs`、`RtfParserTests.cs`、`TextParserTestHelper.cs`
- `task/T04-text-parsers.md`（狀態與本紀錄）

**驗收條件結果**

- `dotnet test --filter FullyQualifiedName~Parsing.Text`：50 項全部通過。
- `dotnet build Contexo.slnx -warnaserror`：0 警告、0 錯誤。`dotnet test`（全方案）：App 1、Core 209、Mcp 5、Desktop 1，全部通過。
- 對照任務檔七項：(1) UTF-8 有／無 BOM、UTF-16 LE BOM、Big5 的 .txt 皆還原繁中；(2) Markdown 多層標題與程式碼區塊內的 `#`；(3) HTML 的 script/style 移除、rowspan／colspan 表格輸出、標題切分、`&nbsp;`／`&amp;`；(4) RTF 以 `\uN?` 產生含中文與表格並取出；(5) JSON／XML 正確與損壞皆不拋例外；(6) 空檔與只有空白；(7) `EmbeddedPath` 帶入——都有對應測試。測試資料皆在測試內以程式產生，沒有提交任何 Fixtures 檔案。

**無法在目前環境驗證**

- 無（所有項目與平台無關，在 macOS 上驗證）。

**與規格不同或需說明之處**

- 測試命名空間用 `Contexo.Core.Tests.Parsing.TextParsers`（資料夾仍是 `Parsing/Text/`）。原因：命名空間 `...Tests.Parsing.Text` 會讓範圍外既有的 `OfficeEmbeddedContentTests.cs` 裡的 `new Text(...)`（OpenXml 型別）解析成命名空間而編譯失敗。`--filter FullyQualifiedName~Parsing.Text` 仍會匹配到這些測試。
- 標題本身的文字也包含在該區段 `Text` 的第一行（Markdown 保留原始標題行含 `#`；HTML 為純標題文字），讓只搜尋標題字樣也能命中；`HeadingPath` 另外照規格提供。
- 未支援 Setext 標題（`===`／`---` 底線式）與 Markdown 的 HTML 區塊，視為一般內文。

**給後續任務的注意事項**

- T09（切塊）：Markdown／HTML 的 `Prose` 區段文字開頭就是標題行；`Table` 區段是完整 HTML 且 `KeepWhole=true`。
- T10：`RtfParser` 對非 RTF 內容拋 `DocumentParseException(Corrupted)`；其餘解析器對損壞的 JSON／XML 不拋例外。
- 內嵌的 HTML／RTF 若由 T05～T07 遞迴交給這些解析器，記得傳入 `ParseContext.EmbeddedPath`。
