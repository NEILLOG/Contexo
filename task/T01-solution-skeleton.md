# T01 方案骨架與共用基礎

- **狀態**：待辦
- **波次**：0
- **相依**：無
- **必讀**：`AGENTS.md`、`plan/01-architecture.md`、`src/Contexo.Core/Abstractions/*.cs`

## 目標

建立整個方案的骨架，讓後續 20 個任務可以**互不衝突地平行開發**：專案、套件、建置設定、所有實作類別的 stub 與 DI 註冊、共用小工具、CI。

`src/Contexo.Core/Abstractions/` 已經存在，內容是已確認可編譯的共用契約。**不要修改它們**，只要把它們納入 `Contexo.Core` 專案。

## 要做

### 1. 方案與專案

```
Contexo.slnx                    .NET 10 SDK 預設的新版方案格式（`dotnet new sln`）
Directory.Build.props
Directory.Packages.props
global.json                     sdk 10.0.100，rollForward latestFeature
nuget.config                    只有 nuget.org
.editorconfig
.gitignore                      Visual Studio 範本 + models/ + *.db + *.db-wal + *.db-shm
src/Contexo.Core/Contexo.Core.csproj        net10.0 classlib
src/Contexo.Mcp/Contexo.Mcp.csproj          net10.0 exe，AssemblyName Contexo.Mcp
src/Contexo.App/Contexo.App.csproj          net10.0 classlib，引用 Core
src/Contexo.Wpf/Contexo.Wpf.csproj          net10.0-windows WinExe，UseWPF、UseWindowsForms、EnableWindowsTargeting，AssemblyName Contexo，引用 Core、App
tests/Contexo.Core.Tests/                   xUnit，引用 Core
tests/Contexo.App.Tests/                    xUnit，引用 App
tests/Contexo.Mcp.Tests/                    xUnit，引用 Mcp、Core
```

`Directory.Build.props`：`Nullable enable`、`ImplicitUsings enable`、`TreatWarningsAsErrors true`、`InvariantGlobalization false`（需要 Big5 等編碼）、`Company/Product = Contexo`；所有專案加入 MinVer（`PrivateAssets=all`），`MinVerTagPrefix v`，`MinVerDefaultPreReleaseIdentifiers alpha.0`。

Core、App、Mcp 加 `InternalsVisibleTo` 給對應測試專案。

### 2. 套件（Central Package Management）

在 `Directory.Packages.props` 一次加入所有任務需要的套件，版本用**目前最新穩定版且支援 net10.0**，並在各專案 csproj 加入 `PackageReference`。後續任務不得再改這兩類檔案（T21 例外）。

| 專案 | 套件 |
|---|---|
| Core | Microsoft.Data.Sqlite、DocumentFormat.OpenXml、UglyToad.PdfPig、Microsoft.ML.OnnxRuntime、Microsoft.ML.Tokenizers、UTF.Unknown、HtmlAgilityPack、RtfPipe、Microsoft.Extensions.DependencyInjection.Abstractions、Microsoft.Extensions.Logging.Abstractions、Microsoft.Extensions.Hosting.Abstractions |
| Mcp | ModelContextProtocol、Microsoft.Extensions.Hosting、Serilog.Extensions.Hosting、Serilog.Sinks.File |
| App | CommunityToolkit.Mvvm、Microsoft.Extensions.Logging.Abstractions |
| Wpf | Microsoft.Extensions.Hosting、Serilog.Extensions.Hosting、Serilog.Sinks.File |
| 測試 | Microsoft.NET.Test.Sdk、xunit、xunit.runner.visualstudio、Xunit.SkippableFact；Mcp.Tests 另加 ModelContextProtocol |
| 全部 | MinVer |

`ModelContextProtocol` 若只有預覽版，採最新預覽版並在完成紀錄註明。

`Microsoft.Extensions.*`、`Microsoft.Data.Sqlite` 使用與 .NET 10 對應的 10.x 版本；其他套件只要支援 net10.0（含透過 net8.0 / netstandard2.0 目標相容）即可。

### 3. 共用工具（T01 完整實作並測試）

放在 `src/Contexo.Core/` 對應資料夾：

- `Common/HtmlTableRenderer.cs`：`public static string Render(TableModel table)`。輸出 `<table>`，有 `HeaderRowCount` 時前幾列放 `<thead>` 用 `<th>`；`RowSpan/ColSpan > 1` 才輸出屬性；文字做 HTML 編碼，換行轉 `<br>`；`Caption` 輸出 `<caption>`；不輸出任何 style。
- `Common/FileCategories.cs`：分類與副檔名對照，以及內建排除規則：
  - Documents：`.docx .txt .md .markdown .json .xml .log .html .htm .rtf`
  - Presentations：`.pptx`
  - Spreadsheets：`.xlsx .xlsm .csv`
  - Pdf：`.pdf`
  - Email、Images：第一版無副檔名（保留分類，回傳空集合）
  - `static bool IsBuiltInExcludedFile(string path)`：檔名以 `~$` 開頭、`.tmp .lnk .exe .dll .sys .ini .db .db-wal .db-shm`、`desktop.ini`、`Thumbs.db`、`.DS_Store`
  - `static bool IsBuiltInExcludedDirectory(string name)`：`.git .svn .hg node_modules bin obj .vs .idea $RECYCLE.BIN System Volume Information`，以及以 `.` 開頭的資料夾
  - 隱藏或系統屬性的檔案與資料夾也排除（由呼叫端以 `FileAttributes` 判斷，提供 `IsHiddenOrSystem(FileSystemInfo)` 輔助）
- `Common/AppPaths.cs`：實作 `IAppPaths`，規則見契約註解。
- `Common/JsonSettingsStore.cs`：實作 `ISettingsStore`；讀檔失敗或格式錯誤時用預設值並記錄警告，不拋例外；寫入用暫存檔再 `File.Replace` / `File.Move(overwrite)`。
- `Common/TextDecoder.cs`（`public static`，T04、T08 共用）：`static string Decode(Stream stream, out Encoding detected)`。順序：有 BOM 依 BOM → 嚴格 UTF-8（`new UTF8Encoding(false, throwOnInvalidBytes: true)`）成功即採用 → UTF.Unknown 偵測且信心 ≥ 0.5 → 退回 Big5（code page 950）。讀取後統一換行為 `\n`、移除 `\0`。
- `Common/AppVersion.cs`：從 `AssemblyInformationalVersionAttribute` 解析成 `AppVersionInfo`（`1.4.2+37.g3f2a9c1` 這類格式；MinVer 預設格式為 `1.4.2-alpha.0.37+3f2a9c1…`，兩種都要能解析出 Version 與 CommitSha）。
- `Parsing/ParserRegistry.cs`：以 DI 注入的 `IEnumerable<IDocumentParser>` 建立副檔名對照；重複副檔名時拋例外（啟動即發現）。
- `Parsing/OfficeEmbeddedContent.cs`：給 T05、T06 共用的工具：
  - `static IReadOnlyList<EmbeddedFile> ExtractEmbeddedFiles(OpenXmlPart part, SourceLocation location)`：只讀取 `EmbeddedPackagePart`（內嵌的 docx / xlsx / pptx 等新格式），依內容類型給副檔名。`EmbeddedObjectPart`（OLE 複合檔，例如舊版 .xls、Visio）第一版**略過**，回傳值不含它們、不拋例外；第二階段再評估 OpenMcdf。
  - `static IReadOnlyList<ExtractedImage> ExtractImages(OpenXmlPart part, SourceLocation location, string? contextText)`：讀取 `ImageParts`。
- `Indexing/AlwaysIdleActivityMonitor.cs`：`IUserActivityMonitor` 預設實作，`IdleTime` 回傳 `TimeSpan.MaxValue`。

### 4. Stub 與 DI

依 `AGENTS.md` 第 7 節的表格，在指定命名空間與資料夾建立每個實作類別，實作對應介面，所有成員拋 `NotImplementedException("Txx")`（寫上擁有任務編號）。解析器的 `SupportedExtensions` 要先填好正確的副檔名，讓 `ParserRegistry` 可以建立：

| 類別 | SupportedExtensions |
|---|---|
| `PlainTextParser` | `.txt .md .markdown .json .xml .log` |
| `HtmlParser` | `.html .htm` |
| `RtfParser` | `.rtf` |
| `WordParser` | `.docx` |
| `PowerPointParser` | `.pptx` |
| `PdfParser` | `.pdf` |
| `SpreadsheetParser` | `.xlsx .xlsm .csv` |

`Integrations/` 底下建立 `ClaudeDesktopIntegration`、`VsCodeIntegration`、`CursorIntegration`、`LmStudioIntegration`、`AiClientStatusService` 的 stub。

**例外：兩個「可安全執行」的 stub**，讓 WPF 外殼（T15）在 T10、T14 完成前也能啟動：

- `IndexingService`：`Current` 回傳 `IndexingSnapshot.Initial`；`StartAsync`、`StopAsync`、`ResolveMassDeletionAsync` 回傳已完成的 Task；`Pause`、`Resume`、`RequestRescan`、`RequestRetry` 不做事；事件不觸發。
- `AiClientStatusService`：`Integrations` 回傳空清單；`GetStatusesAsync` 回傳空清單；`CurrentLaunch` 依 `IAppPaths.McpExecutablePath` 與 `--db` 參數組出。

`src/Contexo.Core/ServiceCollectionExtensions.cs`：

```csharp
public static IServiceCollection AddContexoCore(this IServiceCollection services)
```

註冊所有契約對應的實作（Singleton）、`IEnumerable<IDocumentParser>`、`IEnumerable<IAiClientIntegration>`、`ParserRegistry`、`AppPaths`、`JsonSettingsStore`、`AlwaysIdleActivityMonitor`（以 `TryAdd` 註冊，讓 WPF 可覆寫）、`ChunkingOptions` 與 `ParserOptions` 的預設值。

### 5. 程式進入點骨架

- `Contexo.Mcp/Program.cs`：建立 Host、Serilog（寫到 `{LogsDirectory}\mcp-.log` 與 stderr，**不得寫 stdout**），解析 `--db`、`--models` 參數（覆寫 `IAppPaths`），呼叫 `AddContexoCore()`；MCP 的部分留給 T13（`// T13`）。
- `Contexo.Wpf`：`App.xaml` 與 `App.xaml.cs` 建立 Generic Host、Serilog、`AddContexoCore()`，顯示一個空白 `MainWindow`（內容留給 T15）。
- `Contexo.App`：建立空的 `ViewModels/` 資料夾與一個 `ViewModelBase : ObservableObject`。

### 6. 工具與 CI

- `tools/README.md`：說明各腳本用途（腳本本身由各任務新增）。
- `.github/workflows/ci.yml`：`windows-latest`，setup-dotnet 10.0.x，`fetch-depth: 0`（MinVer 需要 tag 歷史），`dotnet build -warnaserror`、`dotnet test`。再加一個 `ubuntu-latest` job 只建置並測試 Core、App、Mcp 與其測試專案（驗證跨平台）。

## 不做

- 任何 stub 的實際功能（屬於其他任務）。
- UI 外觀（T15）、安裝程式（T21）。

## 可修改範圍

整個儲存庫，**但不可修改** `src/Contexo.Core/Abstractions/`、`plan/`、`task/`（自己的任務檔狀態除外）、`AGENTS.md`。

## 實作要點與已知陷阱

- WPF 專案在 macOS / Linux 上需要 `<EnableWindowsTargeting>true</EnableWindowsTargeting>` 才能還原與編譯。
- `Contexo.Wpf` 的 `AssemblyName` 設為 `Contexo`，產出 `Contexo.exe`；`Contexo.Mcp` 產出 `Contexo.Mcp.exe`。T21 會把兩者發布到同一個資料夾。
- `AppPaths.McpExecutablePath` 依作業系統加 `.exe`。
- `System.Text.Encoding.CodePagesEncodingProvider` 在 `AddContexoCore()` 中註冊一次（`Encoding.RegisterProvider`）。
- `OfficeEmbeddedContent` 不處理 OLE 複合檔，不要為此新增套件。

## 驗收條件

1. `dotnet build Contexo.slnx -warnaserror` 在 Windows 與 macOS/Linux 都成功。
2. `dotnet test` 全部通過，至少包含：
   - `HtmlTableRenderer`：表頭、合併儲存格、HTML 編碼、換行。
   - `FileCategories`：各副檔名分類、內建排除檔案與資料夾。
   - `JsonSettingsStore`：預設值、儲存後重讀、檔案損壞時回到預設值。
   - `AppVersion`：兩種版本字串格式。
   - `TextDecoder`：UTF-8（有／無 BOM）、UTF-16 LE BOM、Big5（測試中以 `Encoding.GetEncoding(950)` 產生）都能還原繁中文字；換行統一。
   - `ParserRegistry`：解析所有 stub 副檔名（不分大小寫）、不支援的副檔名回 null、重複時拋例外。
   - `OfficeEmbeddedContent`：用 OpenXml 在測試中產生一個內嵌 xlsx 的 docx，能取出內嵌檔與一張圖片。
   - DI：`new ServiceCollection().AddContexoCore().BuildServiceProvider()` 能解析每個契約介面。
3. `dotnet run --project src/Contexo.Mcp -- --db <暫存路徑>` 啟動後 stdout 沒有任何輸出（可用重新導向檢查），日誌寫入 logs 資料夾。
4. CI workflow 檔存在且語法正確（`actionlint` 若可用就執行）。

## 完成紀錄

（由執行者填寫：實際採用的套件版本、與規格不同處、待確認事項）
