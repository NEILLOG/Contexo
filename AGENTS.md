# AGENTS.md — Contexo 開發代理指南

這份文件寫給接手 Contexo 工作的 AI 代理與新進開發者。開始任何工作前請完整讀過一次。

## 1. 產品是什麼

**文脈 Contexo**：Windows 桌面程式。使用者指定資料夾，Contexo 把裡面的辦公室文件（Word、PowerPoint、Excel、PDF、文字檔）解析、切塊、向量化，存進本機 SQLite；使用者已經在用的 AI 軟體（Claude Desktop、VS Code、Cursor…）透過 **MCP（stdio）** 啟動 `Contexo.Mcp.exe` 來查詢這些內容。

- 目標使用者**完全不懂技術**，電腦是一般文書機。
- 使用者電腦是**乾淨環境**：不能要求安裝 Ollama、Python、Node、Docker、Office 或任何外部軟體。所有功能必須在 .NET 程式內完成。
- Contexo **不做生成**（不跑 LLM），只負責解析、索引、檢索。
- 完整規劃在 [`plan/`](plan/README.md)，工作細項在 [`task/`](task/README.md)。

## 2. 開始工作的流程

1. 讀本文件。
2. 讀 [`task/README.md`](task/README.md) 了解任務相依與波次。
3. 讀你被指派的任務檔 `task/Txx-*.md`，以及它「必讀」列出的文件。
4. 確認相依任務都已合併（任務檔頂端的「狀態」為「完成」）。沒合併就停下來回報，不要自己補做別人的任務。
5. 開分支 `task/Txx-簡短英文描述`，例如 `task/T05-word-parser`。
6. 只修改任務檔「可修改範圍」列出的檔案。需要動到範圍外的檔案時，停下來回報，不要直接改。
7. 完成驗收條件、把任務檔頂端狀態改為「完成」、填寫「完成紀錄」，再提交。

## 3. 儲存庫結構

```
AGENTS.md                 本文件
CLAUDE.md                 Claude Code 入口，匯入本文件
plan/                     產品計畫（唯讀參考，除非任務要求）
task/                     工作細項，每個任務一個檔
src/
  Contexo.Core/           net8.0 類別庫：契約、解析、切塊、embedding、儲存、檢索、索引、整合、診斷
    Abstractions/         共用契約（介面與資料型別）← 所有任務共用，見第 6 節
  Contexo.Mcp/            net8.0 主控台程式：stdio MCP server
  Contexo.App/            net8.0 類別庫：ViewModel 與畫面邏輯（可在任何 OS 測試）
  Contexo.Wpf/            net8.0-windows：WPF 視窗、XAML、Windows 專屬功能
tests/
  Contexo.Core.Tests/
  Contexo.App.Tests/
  Contexo.Mcp.Tests/
tools/                    下載模型、產生測試資料等腳本
models/                   embedding 模型（不進版控，由 tools/ 腳本下載）
```

## 4. 技術棧

| 項目 | 選擇 |
|---|---|
| 語言 / 執行環境 | C# 12、.NET 8（LTS） |
| UI | WPF（.NET 8，無第三方 UI 框架），MVVM 用 CommunityToolkit.Mvvm |
| 系統匣 | WinForms `NotifyIcon`（`<UseWindowsForms>true</UseWindowsForms>`），不用第三方套件 |
| DI / Host | Microsoft.Extensions.Hosting |
| 日誌 | Serilog（檔案，每日輪替，存 `{DataDirectory}\logs`） |
| 資料庫 | SQLite（Microsoft.Data.Sqlite），WAL 模式，FTS5 使用 **trigram** tokenizer |
| Office 解析 | DocumentFormat.OpenXml |
| PDF | UglyToad.PdfPig |
| HTML / RTF | HtmlAgilityPack、RtfPipe |
| 文字編碼偵測 | UTF.Unknown（Big5 等），並註冊 `CodePagesEncodingProvider` |
| Embedding | Microsoft.ML.OnnxRuntime + Microsoft.ML.Tokenizers；預設模型 bge-small-zh-v1.5（int8） |
| MCP | 官方 C# SDK `ModelContextProtocol`，stdio transport |
| 版本號 | MinVer（git tag 決定） |
| 測試 | xUnit、Xunit.SkippableFact。**不使用** FluentAssertions（v8 起為商業授權） |

**禁止引入**：iText（AGPL）、Aspose / Syncfusion 等付費元件、任何需要使用者另外安裝的執行環境（Python、Node、Java、Office、LibreOffice）。要新增上表以外的 NuGet 套件，先回報並說明理由與授權。

所有 NuGet 版本集中在 `Directory.Packages.props`（Central Package Management）。任務**不要**自行改版本或新增套件，除非任務檔明確允許。

## 5. 建置與測試

| 指令 | Windows | macOS / Linux |
|---|---|---|
| `dotnet build Contexo.sln` | 全部 | 全部（WPF 專案靠 `EnableWindowsTargeting` 可編譯，但不能執行） |
| `dotnet test` | 全部 | 全部 |
| 執行 WPF | `dotnet run --project src/Contexo.Wpf` | **不可**。只能編譯，畫面需在 Windows 人工驗證 |
| 執行 MCP server | `dotnet run --project src/Contexo.Mcp -- --db <path>` | 同左 |

- 下載 embedding 模型：`pwsh tools/download-models.ps1` 或 `bash tools/download-models.sh`。需要模型的測試在模型不存在時以 `Skip.If` 略過，**不可**因此失敗。
- 若環境無法連到 api.nuget.org 或 huggingface.co（例如部分雲端沙箱），**停下來回報**，不要嘗試繞過網路政策、不要手寫替代套件。
- CI（GitHub Actions）在 `windows-latest` 建置並測試全部專案。

## 6. 共用契約（最重要）

`src/Contexo.Core/Abstractions/` 是所有任務之間的介面約定。各任務平行開發，彼此只透過這些型別溝通。

- **不可修改**契約的既有成員（名稱、參數、語意）。
- 真的需要調整時：停下來，在回報中寫明「要改什麼、為什麼、影響哪些任務」，由人決定。
- 實作類別的名稱與命名空間已由 T01 建立成 stub（見第 7 節），請在 stub 檔案內實作，不要改類別名稱。
- 契約上的 XML 註解就是規格。行為有疑問時，以註解為準；註解沒寫到的，以 `plan/` 為準；都沒有就回報。

## 7. 實作類別與擁有權

T01 會建立下列 stub（方法拋出 `NotImplementedException`），並在 DI 註冊好。各任務只要填內容：

| 契約 | 實作類別（命名空間 `Contexo.Core.*`） | 擁有任務 |
|---|---|---|
| `IKnowledgeStore` | `Storage.SqliteKnowledgeStore` | T02 |
| `IEmbeddingService` | `Embedding.OnnxEmbeddingService` | T03 |
| `IDocumentParser`（文字類） | `Parsing.Text.PlainTextParser`、`Parsing.Text.HtmlParser`、`Parsing.Text.RtfParser` | T04 |
| `IDocumentParser`（Word） | `Parsing.Word.WordParser` | T05 |
| `IDocumentParser`（PowerPoint） | `Parsing.PowerPoint.PowerPointParser` | T06 |
| `IDocumentParser`（PDF） | `Parsing.Pdf.PdfParser` | T07 |
| `IDocumentParser`（Excel/CSV）、`ISpreadsheetRegionReader` | `Parsing.Spreadsheet.SpreadsheetParser`、`Parsing.Spreadsheet.SpreadsheetRegionReader` | T08 |
| `IChunker` | `Chunking.StructuredChunker` | T09 |
| `IIndexingService` | `Indexing.IndexingService` | T10 |
| `ISearchService` | `Search.HybridSearchService` | T11 |
| `ITableQueryService` | `Tables.TableQueryService` | T12 |
| `IAiClientIntegration`、`IAiClientStatusService` | `Integrations.*` | T14 |
| `IDiagnosticsExporter` | `Diagnostics.DiagnosticsExporter` | T20 |

T01 已完成、所有任務可直接使用：`HtmlTableRenderer`、`TextDecoder`、`FileCategories`、`AppPaths`、`JsonSettingsStore`、`ParserRegistry`、`OfficeEmbeddedContent`、`AppVersion`、`services.AddContexoCore()`。

## 8. 程式慣例

- 啟用 `Nullable`、`ImplicitUsings`、`TreatWarningsAsErrors`。
- 所有 I/O 都是 async 並接受 `CancellationToken`，一路往下傳。
- 類別預設 `sealed`；只在需要時 `public`，其餘 `internal`（測試專案已設定 `InternalsVisibleTo`）。
- 不使用靜態可變狀態。服務透過建構式注入。
- 日誌用 `ILogger<T>`。**不得記錄文件內容、查詢全文或任何金鑰**，只記錄檔名、長度、耗時、錯誤碼。
- `Contexo.Mcp` 的 **stdout 只能輸出 MCP 協定訊息**。任何 `Console.WriteLine` 都會破壞協定；日誌寫檔案與 stderr。
- 程式碼識別字、註解、commit 訊息用英文或中文皆可，同一檔案內保持一致。**使用者看得到的文字一律繁體中文（台灣用語）**，白話、不用技術詞（例如說「讀取」不說「索引」、說「資料」不說「向量」）。
- 錯誤以 `DocumentErrorCode` / 專屬例外表達，UI 再轉成白話。

## 9. 安全與隱私規則（不可違反）

1. **永遠不刪除、修改、移動使用者的原始檔案。** Contexo 只刪除自己資料庫中的資料。
2. 資料夾整個讀不到時（`Directory.Exists` 為 false 或列舉失敗），**不得**刪除該資料夾的資料，只能標成 `FolderState.Unavailable`。
3. 金鑰一律以 Windows DPAPI 加密保存；匯出問題回報時必定遮罩。
4. 測試資料必須由程式產生（例如用 OpenXml 建立 docx），**不得**提交真實公司文件。
5. 檔案內容不會離開本機（第一版沒有任何網路呼叫，除了開發用的模型下載腳本）。

## 10. 測試要求

- 每個任務都要有自動化測試，放在對應的 `tests/` 專案、以任務資料夾分子目錄，例如 `tests/Contexo.Core.Tests/Parsing/Word/`。
- 測試用文件在測試內以程式產生，或放在 `tests/Contexo.Core.Tests/Fixtures/<任務>/`（只能是程式產生或自行撰寫的內容）。
- 需要 Windows 才能跑的測試標記 `[Trait("Category", "Windows")]` 並在非 Windows 時 `Skip.IfNot(OperatingSystem.IsWindows())`。
- 驗收前執行：`dotnet build Contexo.sln -warnaserror` 與 `dotnet test`，兩者都必須成功。

## 10a. WPF 介面慣例

- 只用 `DynamicResource` 引用顏色與字型（主題要能即時切換）。資源鍵由 T15 定義：
  `Brush.Background`、`Brush.Window`、`Brush.Panel`、`Brush.Line`、`Brush.Foreground`、`Brush.Muted`、`Brush.Accent`、`Brush.AccentSoft`、`Brush.OnAccent`、`Brush.Ok`、`Brush.OkSoft`、`Brush.Warn`、`Brush.WarnSoft`、`Brush.Bad`、`Brush.BadSoft`；樣式 `Text.H1`、`Text.H2`、`Text.Body`、`Text.Caption`、`Button.Primary`、`Button.Secondary`、`Button.Ghost`、`Button.Danger`、`Chip.Ok`、`Chip.Running`、`Chip.Warn`、`Chip.Off`。
- 畫面行為以 [`plan/ui-mockup.html`](plan/ui-mockup.html) 為準（用瀏覽器開啟，右上角可顯示設計註記）。
- ViewModel 放 `Contexo.App`（不可引用 WPF 型別），View 放 `Contexo.Wpf`。需要 Windows API 時，在 `Contexo.App` 定義介面，`Contexo.Wpf/Platform/` 實作。
- 確認、警語一律做在視窗內（對話視窗或內嵌區塊），不用 `MessageBox`。

## 11. Git 與提交

- 一個任務一個分支、一個 PR，PR 標題 `Txx 任務名稱`。
- Commit 訊息第一行簡述，必要時空一行寫細節。
- 不要提交 `models/`、`bin/`、`obj/`、`*.db`、使用者資料。
- 不要 force push 到 `main`。

## 12. 完成時回報

在任務檔的「完成紀錄」填寫，並在回覆中同樣交代：

- 做了什麼、改了哪些檔案
- 驗收條件逐項結果（附指令輸出摘要）
- 無法在目前環境驗證的項目（例如 WPF 畫面需 Windows 人工確認）
- 與規格不同的地方及理由
- 留給後續任務的注意事項
