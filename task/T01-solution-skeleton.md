# T01 方案骨架與共用基礎

- **狀態**：完成
- **波次**：0
- **相依**：無
- **必讀**：`AGENTS.md`、`plan/01-architecture.md`、`src/Contexo.Core/Abstractions/*.cs`

## 目標

建立整個方案的骨架，讓後續 20 個任務可以**互不衝突地平行開發**：專案、套件、建置設定、所有實作類別的 stub 與 DI 註冊、共用小工具、CI。

`src/Contexo.Core/Abstractions/` 已經存在，內容是已確認可編譯的共用契約。**不要修改它們**，只要把它們納入 `Contexo.Core` 專案。

## 要做

### 0. 先確認 Avalonia 可用

開始建立方案前，先在 macOS（或 Windows）執行 `cd poc/ime-avalonia && dotnet run`：

- 確認 Avalonia 12 套件可以還原、程式可以編譯並開出視窗。
- 若有編譯錯誤，修正 `poc/ime-avalonia/` 內的程式（Avalonia 12 API 變更所致），並在完成紀錄寫下改了什麼——這些就是正式程式要注意的 API 差異。
- 若 Avalonia 12 無法使用，改用 11.3.x 並在完成紀錄說明；方案中所有 Avalonia 套件採用相同版本。

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
src/Contexo.Desktop/Contexo.Desktop.csproj  net10.0 WinExe（Avalonia 桌面程式），AssemblyName Contexo，引用 Core、App
tests/Contexo.Core.Tests/                   xUnit，引用 Core
tests/Contexo.App.Tests/                    xUnit，引用 App
tests/Contexo.Mcp.Tests/                    xUnit，引用 Mcp、Core
tests/Contexo.Desktop.Tests/                xUnit + Avalonia.Headless.XUnit，引用 Desktop、App
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
| Desktop | Avalonia、Avalonia.Desktop、Avalonia.Themes.Fluent、Microsoft.Extensions.Hosting、Serilog.Extensions.Hosting、Serilog.Sinks.File |
| 測試 | Microsoft.NET.Test.Sdk、xunit、xunit.runner.visualstudio、Xunit.SkippableFact；Mcp.Tests 另加 ModelContextProtocol；Desktop.Tests 另加 Avalonia.Headless.XUnit、Avalonia.Skia |
| 全部 | MinVer |

`ModelContextProtocol` 若只有預覽版，採最新預覽版並在完成紀錄註明。

Avalonia 系列套件使用 **12.x 最新穩定版**，且所有 Avalonia 套件版本必須一致。若 T00 的 POC 結論改用 11.3，則改用 11.3.x。**不要**加入任何 Avalonia 付費或需要授權金鑰的套件（例如 `AvaloniaUI.Licensing`、Pro／Enterprise 元件、XPF，以及需要授權的開發工具）。

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

**例外：兩個「可安全執行」的 stub**，讓桌面外殼（T15）在 T10、T14 完成前也能啟動：

- `IndexingService`：`Current` 回傳 `IndexingSnapshot.Initial`；`StartAsync`、`StopAsync`、`ResolveMassDeletionAsync` 回傳已完成的 Task；`Pause`、`Resume`、`RequestRescan`、`RequestRetry` 不做事；事件不觸發。
- `AiClientStatusService`：`Integrations` 回傳空清單；`GetStatusesAsync` 回傳空清單；`CurrentLaunch` 依 `IAppPaths.McpExecutablePath` 與 `--db` 參數組出。

`src/Contexo.Core/ServiceCollectionExtensions.cs`：

```csharp
public static IServiceCollection AddContexoCore(this IServiceCollection services)
```

註冊所有契約對應的實作（Singleton）、`IEnumerable<IDocumentParser>`、`IEnumerable<IAiClientIntegration>`、`ParserRegistry`、`AppPaths`、`JsonSettingsStore`、`AlwaysIdleActivityMonitor`（以 `TryAdd` 註冊，讓桌面程式依平台覆寫）、`ChunkingOptions` 與 `ParserOptions` 的預設值。

### 5. 程式進入點骨架

- `Contexo.Mcp/Program.cs`：建立 Host、Serilog（寫到 `{LogsDirectory}\mcp-.log` 與 stderr，**不得寫 stdout**），解析 `--db`、`--models` 參數（覆寫 `IAppPaths`），呼叫 `AddContexoCore()`；MCP 的部分留給 T13（`// T13`）。
- `Contexo.Desktop`：`Program.cs`（`AppBuilder.Configure<App>().UsePlatformDetect()…StartWithClassicDesktopLifetime`）、`App.axaml`（`FluentTheme`）與 `App.axaml.cs` 建立 Generic Host、Serilog、`AddContexoCore()`，顯示一個空白 `MainWindow.axaml`（內容留給 T15）。可參考 `poc/ime-avalonia/` 的最小程式結構。
- `Contexo.App`：建立空的 `ViewModels/` 資料夾與一個 `ViewModelBase : ObservableObject`。

### 6. 工具與 CI

- `tools/README.md`：說明各腳本用途（腳本本身由各任務新增）。
- `.github/workflows/ci.yml`：`windows-latest`，setup-dotnet 10.0.x，`fetch-depth: 0`（MinVer 需要 tag 歷史），`dotnet build -warnaserror`、`dotnet test`。再加一個 `ubuntu-latest` job 建置並測試全部專案（畫面測試以 Headless 執行），以及一個 `macos-latest` job 只做建置（確認開發驗證環境可用）。

## 不做

- 任何 stub 的實際功能（屬於其他任務）。
- UI 外觀（T15）、安裝程式（T21）。

## 可修改範圍

整個儲存庫（含 `poc/ime-avalonia/` 的編譯修正），**但不可修改** `src/Contexo.Core/Abstractions/`、`plan/`、`task/`（自己的任務檔狀態除外）、`AGENTS.md`。

## 實作要點與已知陷阱

- `Contexo.Desktop` 的 `AssemblyName` 設為 `Contexo`，Windows 上產出 `Contexo.exe`；`Contexo.Mcp` 產出 `Contexo.Mcp.exe`。T21 會把兩者發布到同一個資料夾。
- 開發時在 macOS 執行，`AppPaths.McpExecutablePath` 指向同一輸出資料夾中的 `Contexo.Mcp`（無副檔名）。為了讓 Desktop 的輸出資料夾也有 MCP 執行檔，Desktop 專案以 `ProjectReference` 引用 Mcp 並設定 `ReferenceOutputAssembly=false`、`OutputItemType=Content`／`CopyToOutputDirectory`，或在 Desktop 建置後複製 Mcp 的輸出；擇一並在完成紀錄說明。
- `poc/` 不加入方案（它有自己的 `Directory.*.props` 隔離設定）。
- `AppPaths.McpExecutablePath` 依作業系統加 `.exe`。
- `System.Text.Encoding.CodePagesEncodingProvider` 在 `AddContexoCore()` 中註冊一次（`Encoding.RegisterProvider`）。
- `OfficeEmbeddedContent` 不處理 OLE 複合檔，不要為此新增套件。

## 驗收條件

1. `dotnet build Contexo.slnx -warnaserror` 在 Windows 與 macOS/Linux 都成功。
2. `dotnet run --project src/Contexo.Desktop` 在 macOS 能開出空白主視窗（Windows 同樣可以）。
3. `dotnet test` 全部通過，至少包含：
   - `HtmlTableRenderer`：表頭、合併儲存格、HTML 編碼、換行。
   - `FileCategories`：各副檔名分類、內建排除檔案與資料夾。
   - `JsonSettingsStore`：預設值、儲存後重讀、檔案損壞時回到預設值。
   - `AppVersion`：兩種版本字串格式。
   - `TextDecoder`：UTF-8（有／無 BOM）、UTF-16 LE BOM、Big5（測試中以 `Encoding.GetEncoding(950)` 產生）都能還原繁中文字；換行統一。
   - `ParserRegistry`：解析所有 stub 副檔名（不分大小寫）、不支援的副檔名回 null、重複時拋例外。
   - `OfficeEmbeddedContent`：用 OpenXml 在測試中產生一個內嵌 xlsx 的 docx，能取出內嵌檔與一張圖片。
   - DI：`new ServiceCollection().AddContexoCore().BuildServiceProvider()` 能解析每個契約介面。
4. `dotnet run --project src/Contexo.Mcp -- --db <暫存路徑>` 啟動後 stdout 沒有任何輸出（可用重新導向檢查），日誌寫入 logs 資料夾。
5. CI workflow 檔存在且語法正確（`actionlint` 若可用就執行）。

## 完成紀錄

### 做了什麼

建立完整方案骨架：`Contexo.slnx`、4 個正式專案與 4 個測試專案、集中套件管理、全部實作類別的 stub 與 DI、共用工具、程式進入點、CI。`src/Contexo.Core/Abstractions/`、`plan/`、`AGENTS.md` 沒有修改；`poc/` 不需要修正。

主要檔案：

- 根目錄：`Contexo.slnx`、`Directory.Build.props`、`Directory.Packages.props`、`global.json`、`nuget.config`、`.editorconfig`、`.gitignore`（沿用 `chore/gitignore` 分支已有的版本，內容完全相同，合併時不會衝突）
- `src/Contexo.Core/Common/`：`HtmlTableRenderer`、`FileCategories`、`AppPaths`（含 `AppPathsOverrides`）、`JsonSettingsStore`、`TextDecoder`、`AppVersion`
- `src/Contexo.Core/Parsing/`：`ParserRegistry`、`OfficeEmbeddedContent`
- `src/Contexo.Core/Indexing/AlwaysIdleActivityMonitor.cs`，以及依 AGENTS.md 第 7 節建立的全部 stub 與 `ServiceCollectionExtensions.cs`
- `src/Contexo.Mcp/`：`Program.cs`、`McpArguments.cs`、`Logging/StderrSink.cs`
- `src/Contexo.Desktop/`：`Program.cs`、`App.axaml(.cs)`、`MainWindow.axaml(.cs)`
- `src/Contexo.App/ViewModels/ViewModelBase.cs`
- `tests/`（4 個專案）、`tools/README.md`、`.github/workflows/ci.yml`

### 步驟 0：Avalonia

`poc/ime-avalonia` 以 Avalonia 12.0.5 還原並編譯成功，無編譯錯誤，不需修改。正式方案採用 **Avalonia 12.1.3**（當時最新穩定版），所有 Avalonia 套件版本一致。沒有任何付費或需授權金鑰的套件。

### 套件版本（`Directory.Packages.props`）

| 套件 | 版本 |
|---|---|
| Microsoft.Data.Sqlite、Microsoft.Extensions.DependencyInjection(.Abstractions)、Logging.Abstractions、Hosting(.Abstractions) | 10.0.12 |
| DocumentFormat.OpenXml | 3.5.1 |
| PdfPig | 0.1.16（見「與規格不同」第 1 點） |
| Microsoft.ML.OnnxRuntime | 1.30.0 |
| Microsoft.ML.Tokenizers | 2.0.0 |
| UTF.Unknown | 2.7.0 |
| HtmlAgilityPack | 1.13.0 |
| RtfPipe | 2.0.7677.4303 |
| ModelContextProtocol | 2.2.0（正式版，非預覽版） |
| Serilog.Extensions.Hosting | 10.0.0 |
| Serilog.Sinks.File | 7.0.0 |
| CommunityToolkit.Mvvm | 8.4.2 |
| Avalonia、Avalonia.Desktop、Avalonia.Themes.Fluent、Avalonia.Headless.XUnit、Avalonia.Skia | 12.1.3 |
| Microsoft.NET.Test.Sdk | 18.10.1 |
| xunit / xunit.runner.visualstudio / Xunit.SkippableFact | 2.9.3 / 3.1.5 / 1.5.85 |
| xunit.v3（僅 Desktop.Tests） | 3.2.2 |
| MinVer | 8.0.0 |

### 驗收條件結果

1. `dotnet build Contexo.slnx -warnaserror`：macOS 成功（0 警告、0 錯誤）。Windows 與 Linux 尚未實測，交給 CI。
2. `dotnet run --project src/Contexo.Desktop`：在 macOS 啟動後程式持續執行，logs 資料夾與 `contexo-yyyyMMdd.log` 都有產生（內容「Contexo started」）。**沒有親眼看到視窗**：這個環境沒有螢幕擷取權限（`screencapture` 回報 could not create image from display）。視窗是否真的出現請在有畫面的 Mac 上確認一次；Headless 測試已驗證 `MainWindow` 可建立、顯示，標題為「文脈 Contexo」。
3. `dotnet test`：全部通過。Core.Tests 159、Mcp.Tests 5、App.Tests 1、Desktop.Tests 1，共 166 項，涵蓋任務檔列出的全部項目（HtmlTableRenderer、FileCategories、JsonSettingsStore、AppVersion、TextDecoder、ParserRegistry、OfficeEmbeddedContent、DI），另外補了 AppPaths、兩個可安全執行的 stub、`McpArguments`。
4. `dotnet run --project src/Contexo.Mcp -- --db <暫存路徑>`：stdout 0 bytes、結束碼 0；日誌寫入 `{LogsDirectory}/mcp-yyyyMMdd.log` 與 stderr。此項也寫成自動測試（`McpStartupTests`，實際啟動子程序檢查）。
5. CI workflow 已建立（Windows 建置＋測試、Ubuntu 建置＋測試、macOS 只建置），用 Ruby YAML 解析確認語法可讀。**`actionlint` 不在此環境**，未執行。

### Contexo.Mcp 輸出複製到 Desktop 的做法

Desktop 以 `ProjectReference`（`ReferenceOutputAssembly=false`，確保建置順序）引用 Mcp，並在 `Contexo.Desktop.csproj` 的 `CopyMcpToOutput` target 於建置後，把 Mcp 的輸出資料夾（不含 .pdb）複製到 Desktop 輸出資料夾。Mcp 的輸出資料夾用 `GetTargetPath` 取得，不寫死路徑。已確認 `Contexo.Mcp` 在 Desktop 輸出資料夾且有執行權限。這只涵蓋建置輸出；發布（publish）由 T21 處理。

### 與規格不同的地方及理由

1. **PdfPig 的套件識別碼是 `PdfPig`，不是 `UglyToad.PdfPig`。** 官方套件在 NuGet 上叫 `PdfPig`（命名空間才是 `UglyToad.PdfPig`，最新穩定版 0.1.16）。NuGet 上另有識別碼 `UglyToad.PdfPig`（只有 `1.7.0-custom-5` 等版本、作者欄為 `UglyToad.PdfPig`、無專案網址），與官方不是同一個東西。我採用官方的 `PdfPig`；這是同一個函式庫，不是新增技術棧以外的套件。程式碼仍使用 `using UglyToad.PdfPig;`。
2. **Desktop.Tests 使用 xUnit v3。** Avalonia.Headless.XUnit 12.x 只依賴 `xunit.v3.extensibility.core`，不支援 xUnit v2。因此只有 `Contexo.Desktop.Tests` 使用 `xunit.v3`（3.2.2）。其他三個測試專案維持 xUnit 2.9.3＋Xunit.SkippableFact。`Xunit.SkippableFact` 不支援 v3，所以 Desktop.Tests 要略過測試請用 `Assert.Skip(...)` / `Assert.SkipUnless(...)`，不要用 `Skip.If`。這算新增 `xunit.v3` 套件，雖然是同一個 xUnit 專案的新主版本，仍請人確認。
3. **Core.Tests 多了 `Microsoft.Extensions.DependencyInjection`。** `new ServiceCollection()` 在這個套件裡（Core 只引用 Abstractions），DI 驗收測試需要它。它本來就是 `Microsoft.Extensions.Hosting` 的相依項目。
4. **Mcp 的 stderr 日誌用自己寫的小 sink（`Logging/StderrSink.cs`），沒有加入 `Serilog.Sinks.Console`。** 因為任務檔的套件表沒有它。`Host.CreateApplicationBuilder()` 預設會加主控台日誌（寫 stdout），所以 `Program.cs` 先 `ClearProviders()` 再掛 Serilog。
5. **`AddContexoCore()` 在沒有註冊 `ILogger<>` 時退回 `NullLogger<>`（TryAdd）。** 否則任務檔要求的「空的 `ServiceCollection` 也能解析所有契約」做不到。副作用：用空的 `ServiceCollection` 時要先 `AddLogging()` 才有真的日誌；Generic Host 本來就先註冊了，不受影響。
6. **`IAppPaths` 也用 TryAdd 註冊**（任務檔只要求 `AlwaysIdleActivityMonitor`），這樣 Mcp 的 `--db` / `--models` 可以先註冊自己的 `AppPaths` 覆寫。`--db` 只覆寫資料庫路徑；日誌資料夾仍在 `DataDirectory\logs`（要隔離請設 `CONTEXO_DATA_DIR`）。
7. `InternalsVisibleTo`：Core → Core.Tests、Mcp.Tests；App → App.Tests、Desktop.Tests；Mcp → Mcp.Tests；Desktop → Desktop.Tests。多給了 Mcp.Tests（它引用 Core）與 Desktop.Tests（它引用 App）。
8. 實作類別預設 `internal sealed`；`HtmlTableRenderer`、`FileCategories`、`TextDecoder`、`AppVersion`、`AppPaths`、`AppPathsOverrides` 是 `public`（其他專案需要）。

### 留給後續任務的注意事項

- **`TextDecoder` 對極短的 Big5 檔案會誤判。** 依規格順序（BOM → 嚴格 UTF-8 → UTF.Unknown 信心 ≥ 0.5 → Big5），3 個字的 Big5 文字（「報價單」）會被 UTF.Unknown 判成 windows-1252 且信心 ≥ 0.5，解出亂碼。幾十個字以上的內容測試正常。我沒有改規則；T04、T08 若在意極短檔案，可考慮把「西歐單位元組編碼」的偵測結果視為不可信、改走 Big5，需由人決定。
- **`HtmlTableRenderer` 輸出格式**（T09、T17 會依賴）：`<table>` → 可選 `<caption>` → 可選 `<thead>` → `<tbody>` → 每個 `<tr>` 獨占一行 → `<th>` / `<td>`；沒有 `HeaderRowCount` 就沒有 `<thead>`；`rowspan` / `colspan` 只在大於 1 時輸出；模型漏列的位置補空白格以維持欄位對齊；只編碼 `& < > "`，換行轉 `<br>`。
- **`AppVersion.Parse`**：`Version` 是核心版號加預發行標記、不含 `+` 後的中繼資料（`1.4.2-alpha.0.37+3f2a…` → `1.4.2-alpha.0.37`；`1.4.2+37.g3f2a9c1` → `1.4.2`）；`CommitSha` 為小寫。關於頁若只想顯示 `1.4.2`，請在 T20 另外處理。
- **名稱注意**：`Contexo.Desktop.App`（類別）與 `Contexo.App`（命名空間）同名。在 `Contexo.Desktop*` 命名空間內寫 `App` 會得到類別；測試專案請寫完整名稱 `Contexo.Desktop.App`。
- `App.axaml.cs` 目前只建立 Host（Serilog 檔案日誌＋`AddContexoCore()`）但**沒有啟動它**，也沒有註冊平台服務；啟動順序留給 T15。
- Mcp 的 `Program.cs` 目前以 `StartAsync` / `StopAsync` 立刻結束（沒有 MCP 服務會一直卡住）；T13 加入 MCP 後改成 `RunAsync()`，位置已用 `// T13` 標出。
- `AppPaths` 的目錄在「第一次存取該屬性」時建立；`DatabasePath` 每次讀取都會確保上層資料夾存在。
- CI 的 macOS job 只建置；Windows 與 Linux job 的實際結果要等第一個 PR 跑完才知道。
- 注音輸入（T00）尚未逐項實測，T15 與 Windows 檢查表需補上，沒有因為 T01 而改變。
