# T09 結構化切塊

- **狀態**：待辦
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

（由執行者填寫）
