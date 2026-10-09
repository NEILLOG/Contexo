# T20 關於與問題回報

- **狀態**：完成
- **波次**：3
- **相依**：T02、T15
- **必讀**：`AGENTS.md`（第 9 節）、`plan/05-operations.md`（版本號、問題回報）、`plan/ui-mockup.html` 的「關於與問題回報」頁、`src/Contexo.Core/Abstractions/Diagnostics.cs`

## 目標

使用者遇到問題時，按一個按鈕就能把設定與錯誤紀錄打包成 zip 交給管理者；並顯示版本資訊與版本紀錄。

## 要做

### 1. `Diagnostics.DiagnosticsExporter : IDiagnosticsExporter`（Core）

輸出 `Contexo問題回報_yyyyMMdd_HHmm.zip`（檔名已存在時加 `_2`），內容：

| 項目 | 條件 | 內容 |
|---|---|---|
| `manifest.json` | 必附 | 產生時間、Contexo 版本、包含的項目清單與選項 |
| `system.json` | 必附 | 作業系統版本、.NET 版本、處理器數、可用記憶體（`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`）、顯示卡名稱（Windows 上讀登錄 `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\000*\DriverDesc`，讀不到就省略）、Embedding 模型 id 與是否可用、`StoreStatistics`、資料夾清單（名稱、狀態、文件數；路徑依 `IncludeFullPaths`） |
| `settings.json` | `IncludeSettings` | `AppSettings`；任何名稱含 `key`、`secret`、`token`、`password` 的屬性值改為 `"***"`（遞迴、不分大小寫，為將來的設定預留） |
| `logs/` | `IncludeRecentLogs` | `LogsDirectory` 中最近 `LogDays` 天的檔案（以 `FileShare.ReadWrite` 複製，因為 Serilog 正在寫） |
| `failed-files.csv` | `IncludeFailedFileList` | 檔名、資料夾、錯誤碼、白話原因、時間；**不含檔案內容** |

`IncludeFullPaths = false` 時，所有路徑改為「{資料夾顯示名稱}\…\{檔名}」，包含日誌檔中的路徑：日誌內容中出現的使用者資料夾路徑與 `%USERPROFILE%` 都以字串取代遮罩。

### 2. 關於頁（`AboutViewModel` / `AboutView`）

1. **版本卡**：「文脈 Contexo」、版本、組建（`AppVersion` 的 informational version 與日期）、本機模型、資料庫版本（`meta` 或 `PRAGMA user_version`）；「複製版本資訊」按鈕（`IClipboardService.TrySetTextAsync`，失敗時改為顯示可選取的文字）。
2. **匯出問題回報卡**：說明「把設定和錯誤紀錄打包成一個 .zip 檔，寄給管理者協助判斷。」；勾選項目（版本與系統資訊為必要且停用勾選；設定內容「金鑰與密碼一律遮罩」；最近 7 天的錯誤紀錄；無法讀取的檔案清單「只有檔名和錯誤原因，不含檔案內容」；完整資料夾路徑「可能包含個人名稱，預設不附」）；「匯出問題回報…」→ `IFilePicker.PickSaveFolderAsync()`（預設桌面）→ 匯出 → 結果框「已儲存到桌面：{檔名}（1.2 MB）」＋「開啟所在資料夾」。
3. **版本紀錄卡**：顯示儲存庫根目錄 `CHANGELOG.md` 的內容（以內嵌資源方式打包進 `Contexo.App`），只顯示最近 5 個版本；格式為 `## 1.4.2 - 2026-10-06` 加條列。檔案不存在時隱藏此卡。
4. **啟動錯誤畫面**：接上 T15 的 `StartupErrorView`「匯出問題回報」按鈕，使用相同的匯出流程（預設選項）。

### 3. `CHANGELOG.md`

在儲存庫根目錄建立初始內容（`## 未發布` 加一行「第一版開發中」）。T21 會加入自動產生的腳本。

## 不做

- 寄送郵件或上傳（使用者自行把 zip 交給管理者）。
- 管理者聯絡方式（來自伺服器設定，第二階段）。

## 可修改範圍

- `src/Contexo.Core/Diagnostics/**`
- `src/Contexo.App/About/**`
- `src/Contexo.Desktop/Views/About/**`
- `tests/Contexo.Desktop.Tests/About/**`
- `src/Contexo.App/Contexo.App.csproj`（**只**新增 `CHANGELOG.md` 的 `EmbeddedResource` 連結）
- `CHANGELOG.md`
- `tests/Contexo.Core.Tests/Diagnostics/**`、`tests/Contexo.App.Tests/About/**`

## 實作要點與已知陷阱

- 匯出在背景執行，可取消；失敗時刪除不完整的 zip。
- 讀取登錄檔前用 `OperatingSystem.IsWindows()` 保護，並加上 `[SupportedOSPlatform("windows")]` 避免分析器警告（警告會被視為錯誤）。
- 遮罩後的日誌仍可能含使用者在檔名中寫的人名，這是可接受的（檔名預設就會出現在 failed-files.csv）。

## 驗收條件

1. `dotnet test --filter "FullyQualifiedName~Diagnostics|FullyQualifiedName~Contexo.App.Tests.About"` 通過，至少涵蓋：
   - zip 內容依選項正確；`manifest.json` 列出實際包含的項目。
   - 設定遮罩（巢狀與大小寫）。
   - 日誌複製在檔案被另一個 `FileStream` 以寫入方式開著時仍成功；只包含指定天數。
   - `IncludeFullPaths=false` 時，zip 內任何檔案都不出現測試用的使用者資料夾完整路徑。
   - 檔名重複時加尾碼；失敗時不留下殘缺 zip。
   - 版本紀錄解析（只取 5 個版本、檔案不存在時隱藏）。
2. 編譯成功。
3. 在 macOS 實際操作確認（`dotnet run --project src/Contexo.Desktop`，列入完成紀錄）：匯出流程、開啟所在資料夾、啟動錯誤畫面的匯出按鈕。

## 完成紀錄

### 做了什麼

- `Contexo.Core/Diagnostics`：`DiagnosticsExporter`（zip 內容依選項、`FileMode.CreateNew` 保留檔名、失敗或取消刪除殘缺 zip、檔名衝突加 `_2`、`_3`…）；內部輔助 `PathMasker`（日誌遮罩）、`SettingsMasker`（設定遮罩）、`GpuRegistry`（Windows 登錄檔，`[SupportedOSPlatform("windows")]`）；公開靜態 `DatabaseVersionReader`（讀 `PRAGMA user_version`，供關於頁的「資料庫版本」使用，因為 `IKnowledgeStore` 契約沒有提供）。
- `Contexo.App/About`：`AboutViewModel`（版本卡、複製版本資訊、版本紀錄）、`ProblemReportExport`（匯出流程：選項、選資料夾、背景匯出可取消、結果框、開啟所在資料夾、白話錯誤）、`ChangelogParser`、`StartupErrorViewModel`（接上匯出流程）。
- `Contexo.Desktop/Views/About`：`AboutView.axaml`（三張卡）、`StartupErrorView`（匯出按鈕＋結果框）。
- `CHANGELOG.md`（儲存庫根目錄）與 `Contexo.App.csproj` 的內嵌資源連結（檔案不存在時以 `Condition` 略過，不會讓建置失敗）。
- 測試：`tests/Contexo.Core.Tests/Diagnostics`（18，Windows 登錄檔 1 個在非 Windows 略過）、`tests/Contexo.App.Tests/About`（30）、`tests/Contexo.Desktop.Tests/About`（11，含 DI 容器組裝＋真正的匯出器端到端、Headless 截圖）。

### 驗收條件結果（macOS，.NET 10.0.401）

1. 指定的測試篩選全部通過（Core 17 通過 1 略過、App.Tests.About 30 通過），涵蓋：zip 內容與 `manifest.json` 條目一致；選項全關時只剩 `manifest.json`、`system.json`；設定遮罩（巢狀、陣列、大小寫）；日誌在另一個 `FileStream` 以寫入方式開著時仍可複製、只含指定天數（6 天內含、10 天前不含、`LogDays=30` 含）；`IncludeFullPaths=false` 時整個 zip（含日誌、CSV、system.json）搜尋不到測試用使用者資料夾完整路徑、使用者名稱、受監看資料夾的完整路徑；失敗清單 CSV 不含文件內容與錯誤訊息原文；檔名衝突加尾碼；取消與例外都不留下殘缺 zip 且不動既有 zip；資料庫讀不到時仍能產生報告；版本紀錄只取 5 個、檔案不存在時隱藏卡片。
2. `dotnet build Contexo.slnx -warnaserror`：0 警告 0 錯誤。
3. `dotnet test`（全部）：Core 926 通過／15 略過、App 55、Mcp 5、Desktop 48 通過；唯一失敗是 `SingleInstanceTests.Can_be_woken_more_than_once`，原因是同一台機器上另一個代理正在執行真正的 Contexo 桌面程式（pgrep 看到 `agent-ac2c96a1894936e06` 的 Contexo 行程），單一執行個體的 pipe 互相干擾，與本任務無關。
4. macOS 實際操作：**部分完成**。以獨立的暫存 `CONTEXO_DATA_DIR` 執行 `dotnet run --project src/Contexo.Desktop`，程式正常啟動（建立資料庫與日誌、日誌出現「Contexo started」、無例外），並已結束行程、清除暫存資料。**無法驗證**：本代理環境沒有螢幕擷取與輔助使用權限，無法實際點擊「匯出問題回報…」、系統資料夾選擇器、「開啟所在資料夾」（Finder）、啟動錯誤畫面的按鈕。以 Headless 測試替代：以假的選擇器／匯出器驗證按鈕與命令、結果框、錯誤框、啟動錯誤畫面；以真正的 DI 容器與真正的 `DiagnosticsExporter` 產生 zip 並檢查內容（`AboutCompositionTests`）。截圖在 `artifacts/screenshots/`（`about-light.png`、`about-dark-1.00.png`、`about-dark-1.25.png`、`about-export-done.png`、`startup-error-*.png`，不提交），人需要在 Mac 上實際操作一次。

### 與規格不同或規格未明之處

- **預設桌面**：`IFilePicker.PickSaveFolderAsync()` 沒有參數，`Platform/Common/AvaloniaServices.cs` 的實作沒有設定起始位置，所以選擇器不一定從桌面開始（不在本任務範圍）。結果框會判斷選到的資料夾是否就是桌面，是的話顯示「已儲存到桌面：」，否則顯示「已儲存到「資料夾名」資料夾：」。建議 T19 或維運者把 `SuggestedStartLocation` 設成桌面。
- **啟動錯誤畫面的連接方式**：`StartupErrorViewModel` 由 `App.axaml.cs`（範圍外）以 `new StartupErrorViewModel()` 建立，沒有 DI。因此由 `StartupErrorView` 在 `DataContextChanged` 時從 `App.Services` 取得 `AboutViewModel` 並呼叫 `CreateExportFlow()` 接上（全新的匯出流程，預設選項）。若維運者願意修改 `App.axaml.cs`，改成 `new StartupErrorViewModel(about.CreateExportFlow())` 會更乾淨，兩種方式可並存。
- **`AboutViewModel` 保留 public 無參數建構式**（無服務、只能看版本、不能匯出）：`ShellViewModelTests` 與 `tests/Contexo.Desktop.Tests/TestShell.cs`（範圍外）都以 `new AboutViewModel()` 建立。DI 會挑參數最多的建構式，所以正式程式用的是注入版本。
- **白話原因**：`ErrorText` 在 `Contexo.App`，Core 不能引用，所以 `DiagnosticsExporter` 內有一份相同用語的 `DocumentErrorCode` 對照（switch 沒有 `_` 分支，新增列舉值而沒翻譯會編譯失敗）。
- **failed-files.csv 欄位**：檔名、資料夾、位置、錯誤碼、原因、時間（任務檔只列前五項，多了「位置」：不附完整路徑時為「{資料夾顯示名稱}\…\{檔名}」，附完整路徑時為完整路徑）；UTF-8 含 BOM，以 Excel 開啟中文不會亂碼；以 `=`、`+`、`-`、`@` 開頭的儲存格會加上單引號避免被當成公式。`DocumentRecord.ErrorMessage` 不匯出（可能含路徑或內容）。清單最多 2000 筆。
- **日誌遮罩**：不論是否附完整路徑，日誌中 `apiKey=…`、`password: …`、`token=…`、`secret=…` 的值一律改為 `***`（契約註解寫明金鑰一律遮罩）。不附完整路徑時：受監看資料夾的完整路徑改為其顯示名稱，使用者資料夾（`%USERPROFILE%` 的實際值）與 `X:\Users\<名稱>`、`/Users/<名稱>`、`/home/<名稱>`（含 JSON 轉義的雙反斜線）改為 `%USERPROFILE%`；中間的子資料夾名稱與檔名不遮罩（任務檔已說明可接受）。
- **日誌天數**：以檔案最後寫入時間判斷（`LastWriteTimeUtc >= 現在 - LogDays 天`）；`LogDays` 在 UI 固定 7。
- `manifest.json` 的 `entries` 含 `manifest.json` 自己；另記錄各選項與 Contexo 版本。`system.json` 另含 `schemaVersion`（`PRAGMA user_version`）、組建日期、CPU 架構；不含電腦名稱與使用者名稱。系統資訊的各區塊（資料庫、資料夾、模型）各自 try/catch，資料庫打不開時只記錄例外型別名稱，所以啟動錯誤畫面也能匯出。
- 版本紀錄卡：`## 未發布` 這類沒有日期的標題也會顯示（初始 `CHANGELOG.md` 因此會有一張卡）；最近 5 個版本依檔案順序取前 5 個。
- 版本卡的「資料庫版本」顯示為 `schema N`（讀 `PRAGMA user_version`，不是 meta）；「本機模型」把模型 id `bge-small-zh-v1.5/int8` 顯示成「bge-small-zh-v1.5（int8）」，未安裝時加「，尚未安裝」。
- 管理者聯絡方式依任務檔不做。

### 待在 Windows 確認

- 登錄檔讀顯示卡名稱（`GpuRegistry`，`Category=Windows` 測試在 Windows 才會跑）；`system.json` 的 `gpu`。
- 系統資料夾選擇器、儲存到桌面後的文字、`RevealInFileManager`（檔案總管選取 zip）、日誌被 Serilog 開著時的複製（`FileShare.ReadWrite | Delete`）。
- `%USERPROFILE%` 遮罩在 `C:\Users\<名稱>` 的實際效果。

### 給後續任務的注意事項

- T21：`CHANGELOG.md` 的格式是 `## 1.4.2 - 2026-10-06` 加 `- ` 條列（日期可省略，也接受 `–`、`—` 當分隔）；`Contexo.App.csproj` 會把根目錄的 `CHANGELOG.md` 以 `LogicalName="CHANGELOG.md"` 內嵌，腳本只要更新該檔。
- 其他任務若新增 `IDiagnosticsExporter` 的選項，請洽人決定（契約不可改）。
- `DatabaseVersionReader`（Core，public static）可供其他畫面讀取資料庫 schema 版本。
