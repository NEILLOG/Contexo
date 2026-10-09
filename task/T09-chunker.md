# T09 結構化切塊

- **狀態**：完成
- **波次**：1
- **相依**：T01
- **必讀**：`AGENTS.md`、`plan/02-ingestion.md`（Word 的切塊）、`src/Contexo.Core/Abstractions/Chunking.cs`、`Parsing.cs`

## 目標

實作 `Chunking.StructuredChunker : IChunker`，把解析器產出的區段切成適合向量化的片段，盡量不破壞結構。

## 要做

`Split(documentTitle, sections, options)`，依區段順序輸出，`Ordinal` 從 0 連續編號。

### 依區段種類

| Kind | 規則 |
|---|---|
| `Prose` | 依段落（空行或單一換行）累積到 `MaxChars`；超過就在段落邊界切；單一段落超過 `MaxChars` 時依句子（`。！？；.!?;` 與換行）切，句子仍過長再硬切。相鄰片段重疊 `OverlapChars`（取前一片段結尾的完整句子，不足時取字元）。同一 `HeadingPath` 內短於 `MinChars` 的片段與相鄰片段合併。 |
| `Table` | 整個區段一個片段。超過 `HardMaxChars` 時依 `<tr>` 切，每段重複 `<thead>`（沒有 thead 時重複第一列），每段 ≤ `HardMaxChars`。 |
| `Slide` | 整張一個片段；超過 `HardMaxChars` 時依段落切（不重疊）。 |
| `Notes`、`Diagram` | 同 `Slide`。 |
| `TableSummary` | 永遠一個片段，不切；`TableKey` 帶入。 |
| 任何 `KeepWhole=true` | 不超過 `HardMaxChars` 就不切。 |

### 片段內容

- `Text`：片段本身的文字。
- `EmbeddingText`：`{前綴}\n{Text}`，前綴依序由以下組成（以「 › 」連接，空的略過）：
  1. `documentTitle`
  2. `Location.EmbeddedPath`（內嵌檔名）
  3. `Location.Sheet` 或「第 {Slide} 張投影片」或「第 {Page} 頁」
  4. `Location.HeadingPath`
  5. `Location.Title`（與 HeadingPath 最後一項相同時略過）
- `Location`：沿用區段的 `Location`。
- `Kind`、`TableKey`：沿用區段。

### 長度計算

字元數以 `StringInfo` 的文字元素（text elements）計算，CJK 每字算 1。HTML 標籤也算入長度（它們同樣會占用模型的 token）。

### 其他

- 全部是空白的區段略過。
- 純函式、無 I/O、執行緒安全。

## 不做

- Token 層級的精確切分（模型會自行截斷到 512 tokens；`MaxChars = 500` 已預留空間）。

## 可修改範圍

- `src/Contexo.Core/Chunking/**`
- `tests/Contexo.Core.Tests/Chunking/**`

## 實作要點與已知陷阱

- 重疊文字不能讓片段超過 `MaxChars + OverlapChars`。
- 切 HTML 表格時用簡單的標籤掃描即可（輸入一定是 `HtmlTableRenderer` 的格式：`<table>`、可選 `<caption>`、可選 `<thead>`、`<tbody>`、`<tr>`、`<th>`/`<td>`），不需要 HTML 解析器。先讀 T01 的 `HtmlTableRenderer` 確認輸出格式。
- 不要在 surrogate pair 或組合字元中間切斷。

## 驗收條件

`dotnet test --filter FullyQualifiedName~Chunking` 全部通過，至少涵蓋：

1. 一段 2000 字中文 → 多個片段，每個 ≤ `MaxChars + OverlapChars`，相鄰片段有重疊，所有原文都被涵蓋（去除重疊後串接等於原文）。
2. 切點落在句號或段落邊界（除非單句過長）。
3. 短段落合併；不同 `HeadingPath` 的段落不合併。
4. 小表格一個片段；超大表格切成多段，每段都有表頭，`<table>` 標籤完整。
5. `TableSummary` 永遠不切，`TableKey` 正確。
6. `EmbeddingText` 前綴：Word（標題路徑）、PowerPoint（第 N 張投影片 + 標題）、Excel（工作表）、PDF（第 N 頁）、內嵌檔四種情況正確。
7. `Ordinal` 連續、空白區段略過。
8. 含 emoji 與罕用字（U+20000 以上）的文字不會被切壞。

## 完成紀錄

**分支**：`task/T09-chunker`

**做了什麼**

- `src/Contexo.Core/Chunking/`：
  - `StructuredChunker.cs`：入口。依區段種類分流、短片段合併、重疊、`EmbeddingText` 前綴、`Ordinal` 編號。
  - `ProseSplitter.cs`：段落（單一換行或空行）→ 句子（`。！？；` 一律斷句；`.!?;` 只在後面是空白或結尾時斷句，避免切壞 `3.14`、`v1.2.3`；句末的引號與括號跟著前一句）→ 文字元素硬切。貪婪累積到 `MaxChars`；也提供重疊文字的計算。
  - `HtmlTableSplitter.cs`：以標籤掃描切 `HtmlTableRenderer` 格式的表格。
  - `TextMeasure.cs`：以 `StringInfo` 文字元素計算長度、硬切、取尾端字元（不切斷 surrogate pair、emoji ZWJ 序列、組合字元）。
- `tests/Contexo.Core.Tests/Chunking/StructuredChunkerTests.cs`：42 個測試。

**行為細節**

- Prose：累積到 `MaxChars`；整段放不進目前片段、但單獨放得下時，在段落邊界換片段；整段超過 `MaxChars` 時逐句累積；單句超過就以 `MaxChars` 硬切。段落之間保留原本的分隔（單一換行 `\n` 或空行 `\n\n`）。
- 重疊：下一個片段開頭接上前一片段結尾「放得進 `OverlapChars` 的完整句子」（盡量多句）；連最後一句都放不進時取最後 `OverlapChars` 個字元。重疊與分隔字元一併計入預算，所以片段長度恆 ≤ `MaxChars + OverlapChars`。重疊只發生在同一個區段切出的相鄰片段之間，不跨區段、不跨表格。
- 合併：長度（不含重疊）< `MinChars` 的 Prose 片段，先併入前一個、再併入後一個「位置相同」的片段（`HeadingPath`、`EmbeddedPath`、`Page`、`Slide`、`Sheet`、`Title` 全部相同），且合併後 ≤ `MaxChars`；否則維持原樣。因此 PDF 不同頁、Word 不同標題都不會合併。
- `Table`：≤ `HardMaxChars` 整個一片；超過時依 `<tr>` 切，每片都是完整 `<table>`，重複 `<caption>` 與 `<thead>`（無 thead 時重複第一列），每片 ≤ `HardMaxChars`。單一列本身就放不進時，把該列每個儲存格的文字分段拆成多列（欄位對齊、不切斷 `<br>` 與 `&amp;` 等字元實體、文字不遺失，後續列的 `rowspan`/`colspan` 屬性省略）。表頭本身超過 `HardMaxChars` 的一半時不重複表頭。結構不認得（找不到 `<table>`／`<tr>`）時退回依段落切。
- `Slide`／`Notes`／`Diagram`：≤ `HardMaxChars` 整個一片；超過時依段落（再依句子、字元）切，不重疊。
- `TableSummary`：永遠一片，帶 `TableKey`（即使超過 `HardMaxChars`）。
- `Prose` 且 `KeepWhole=true`：≤ `HardMaxChars` 一片；超過時同 `Slide` 的切法。
- `EmbeddingText` 前綴：`documentTitle`、`EmbeddedPath` 各項、`Sheet`（否則「第 N 張投影片」，否則「第 N 頁」）、`HeadingPath` 各項、`Title`（與 `HeadingPath` 最後一項相同時略過），以「 › 」連接；前綴為空（沒有標題也沒有位置）時 `EmbeddingText` 就是 `Text`。
- 全空白區段略過；`\r\n`、`\r` 正規化為 `\n`；每個片段結尾空白會去掉。

**驗收條件**

- `dotnet test --filter FullyQualifiedName~Chunking`：42 個全數通過。
- 1 長中文：`Long_chinese_text_is_split_with_overlap_and_nothing_is_lost`（約 2000 字、每片 ≤ 580、相鄰有重疊、去重疊後串接等於原文）、無標點版本 `Text_without_any_punctuation_...`、單句過長 `A_single_over_long_sentence_...`。
- 2 切點：`Chunks_end_at_sentence_boundaries`、`Chunks_break_at_paragraph_boundaries`、`Paragraphs_are_accumulated_until_the_limit`。
- 3 合併：`Short_pieces_with_the_same_heading_are_merged`、`Pieces_with_different_heading_paths_are_not_merged`、`Pdf_pages_are_not_merged_even_when_short` 等。
- 4 表格：`A_small_table_is_one_chunk`、`A_huge_table_is_split_into_complete_tables_that_repeat_the_header`（每列恰出現一次、標籤完整）、無 thead、超大單列、無法辨識的標記。
- 5 `A_table_summary_is_never_split_and_keeps_its_key`。
- 6 `Embedding_text_for_word/powerpoint/excel/pdf/an_embedded_file_...`。
- 7 `Ordinals_are_consecutive_and_blank_sections_are_skipped`。
- 8 `Emoji_and_rare_characters_are_never_cut_in_the_middle`（emoji、U+20000、ZWJ 家庭序列、組合字元）、`Rare_cjk_characters_count_as_one_each`。
- `dotnet build Contexo.slnx -warnaserror`：0 警告、0 錯誤。
- `dotnet test`（全方案）：Core 555（略過 14，皆為既有的需要模型或 Windows 的測試）、App 1、Mcp 5、Desktop 1，全數通過。

**無法在目前環境驗證**

- 沒有用真實文件（Word／PowerPoint／PDF 解析器的實際輸出）端對端測試，輸入皆為測試內程式產生的區段；T10 或 T22 接上解析器後建議抽樣看一下切塊結果。

**與規格不同或規格未寫處的決定**

- `HardMaxChars` 小於 `MaxChars` 時，以 `HardMaxChars` 為準（`MaxChars` 被限制在 `HardMaxChars` 以內），因為契約把它定義為絕對上限。`OverlapChars` 限制在 `0..MaxChars-1`。
- 合併短片段以「片段本身」長度（不含重疊）判斷；合併前後都不得超過 `MaxChars`，所以結尾剩下很短的片段（前一片已接近滿）可能仍短於 `MinChars`。
- 空行在 Prose 內以 `\n\n` 保留，不同區段合併時以 `\n` 連接。

**給後續任務的注意事項**

- T10：`StructuredChunker` 是 `internal sealed`、無狀態，已在 DI 註冊為 `IChunker`；`ChunkingOptions` 由 DI 提供預設值。內嵌檔的 `Ordinal` 需接在容器之後（契約註解），T10 要自行處理：把內嵌檔區段接到容器區段後再一起呼叫 `Split`，或自行重編號。`documentTitle` 請傳不含副檔名的檔名。
- Table 切開時每片的 `Location` 相同，`CellRange` 不會縮小到該片範圍。
- 切 HTML 表格時若原表有跨列的 `rowspan`，被切到下一片的列不會帶上該儲存格（罕見，未特別處理）。

