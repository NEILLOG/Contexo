# T20 關於與問題回報

- **狀態**：待辦
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

（由執行者填寫）
