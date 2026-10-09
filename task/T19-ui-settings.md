# T19 介面：設定頁

- **狀態**：完成
- **波次**：3
- **相依**：T10、T15（T02 的統計與排除清單 API）
- **必讀**：`AGENTS.md`（10a 節）、`plan/04-ui.md`（設定、確認與警語）、`plan/ui-mockup.html` 的「設定」頁、`src/Contexo.Core/Abstractions/AppEnvironment.cs`、`Storage.cs`

## 目標

設定頁：外觀、檔案類型、行為、進階。設定變更立即生效並自動儲存（沒有「儲存」按鈕）。

## 要做

`SettingsViewModel` / `SettingsView`，每個區塊一張卡片：

1. **外觀**
   - 色彩主題：分段按鈕「跟隨系統｜淺色｜深色」，說明「預設跟隨電腦的淺色或深色模式，系統切換時會自動跟著變。」
   - 文字大小：分段按鈕「標準｜大｜特大」，說明「整個視窗的文字和按鈕會一起放大。」
   - 變更即 `ISettingsStore.SaveAsync`；T15 的 `ThemeManager` 與字級已監聽 `Changed`，會即時套用。
2. **要讀取的檔案類型**：膠囊狀勾選「文件（Word、文字檔）」「簡報」「試算表」「PDF」「郵件」「圖片（處理時間較長）」。郵件與圖片第一版停用並註明「之後的版本提供」。至少要保留一個類別（最後一個不能取消勾選）。變更後 T10 會自動重新對帳；取消勾選時先 `ConfirmAsync`：「取消後，AI 將查不到這類檔案的內容。你的原始檔案不受影響。」
3. **行為**（開關）
   - 「只在電腦閒置時全速處理」／「你在使用電腦時，會自動放慢速度。」
   - 「開機時自動啟動」／「在背景保持資料夾同步，新檔案會自動加入。」→ 同時呼叫 `IStartupRegistration.SetEnabled`。
   - 「關閉視窗時縮小到系統匣」／「從右下角圖示可以再打開。」
   - 「略過大型檔案」下拉：20 MB／50 MB／100 MB／不限制。
4. **進階**（可展開，預設收合）
   - 資料佔用空間：`GetStatisticsAsync().DatabaseBytes` 以「1.8 GB」格式顯示，旁註資料位置（`IAppPaths.DataDirectory`，可點「開啟」）。
   - 排除的檔案與資料夾：「管理（N）」→ 對話框列出 `GetExclusionsAsync`（路徑、類型、加入時間），每筆「恢復」→ `RemoveExclusionAsync` ＋ `RequestRescan(null)`。
   - **清除全部資料**（危險按鈕）→ `ConfirmAsync`：標題「確定要清除全部資料嗎？」，條列「AI 將查不到任何資料，直到重新建立完成。」「重新建立可能需要一到數小時。」「你的原始檔案不會被刪除，資料夾清單和設定也會保留。」；「我了解」勾選文字「我了解需要重新建立，可能要數小時」；另一個選項「清除後立即重新建立」（預設勾選，放在對話框內，用自訂對話框 VM 實作）。確認後：`IIndexingService.Pause()` → `ClearIndexedDataAsync` → 依選項 `RequestRescan(null)` → `Resume()`；完成後顯示「已清除 1.8 GB 資料，正在重新建立。」
   - 「開啟記錄檔資料夾」（`IAppPaths.LogsDirectory`）。
   - 「複製 AI 軟體設定內容」：選擇軟體（下拉）後複製 `BuildManualSnippet`。

### 開機自動啟動（實作 T15 建立的兩個 stub）

- `Platform/Windows/StartupRegistration.cs`（正式產品）：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值名 `Contexo`，內容 `"{Contexo.exe 完整路徑}" --minimized`。`IsEnabled` 讀取並確認路徑與目前執行檔相同。啟動時若設定為開啟但登錄值路徑不同（程式搬移或更新），自動修正。加上 `[SupportedOSPlatform("windows")]`。
- `Platform/Mac/StartupRegistration.cs`（開發驗證用）：在 `~/Library/LaunchAgents/tw.contexo.desktop.plist` 寫入 LaunchAgent（`ProgramArguments` 為目前執行檔與 `--minimized`，`RunAtLoad` 為 true）；關閉時刪除該 plist（這是 Contexo 自己建立的設定檔，不是使用者檔案）。不需要呼叫 `launchctl`，下次登入生效即可。

## 不做

- 運算來源（公司伺服器模式）：**第一版不顯示這個區塊**（第二階段）。

## 可修改範圍

- `src/Contexo.App/Settings/**`
- `src/Contexo.Desktop/Views/Settings/**`
- `src/Contexo.Desktop/Platform/Windows/StartupRegistration.cs`、`src/Contexo.Desktop/Platform/Mac/StartupRegistration.cs`
- `tests/Contexo.Desktop.Tests/Settings/**`
- `tests/Contexo.App.Tests/Settings/**`

## 實作要點與已知陷阱

- 設定以 `record with` 產生新物件後儲存；快速連續切換時以最後一次為準（序列化儲存動作）。
- 位元組顯示：< 1 MB 顯示 KB，其餘 MB / GB，保留一位小數。
- 清除資料期間停用整個進階區塊，避免重複按。

## 驗收條件

1. `dotnet test --filter FullyQualifiedName~Contexo.App.Tests.Settings` 通過，以假的服務測試：
   - 每個設定變更都呼叫 `SaveAsync` 且值正確；連續變更以最後一次為準。
   - 最後一個類別不能取消；取消類別時有確認，按取消則設定不變。
   - 開機啟動同步呼叫 `IStartupRegistration`。
   - 清除全部資料：未勾「我了解」不能確認；呼叫順序 Pause → Clear → Rescan（依選項）→ Resume；失敗時仍會 Resume 並顯示錯誤。
   - 排除清單恢復。
   - 容量格式化。
2. 編譯成功。
3. Headless 測試：設定頁淺色／深色、三段字級截圖；清除資料對話框。
4. 在 macOS 實際操作確認（`dotnet run --project src/Contexo.Desktop`，列入完成紀錄）：主題與字級即時切換；開機啟動寫入 LaunchAgent、登出再登入後以選單列圖示啟動；清除全部資料流程。
5. Windows 的開機啟動（登錄檔、重開機後以系統匣啟動）列入 `tests/manual/CHECKLIST.md`。

## 完成紀錄

**分支**：`task/T19-ui-settings`

### 做了什麼

- `Contexo.App/Settings/`：`SettingsViewModel`（在 T15 的空類別上擴充，建構式注入 `ISettingsStore`、`IIndexingService`、`IKnowledgeStore`、`IAppPaths`、`IDialogService`、`IStartupRegistration`、`IShellLauncher`、`IClipboardService`、`IAiClientStatusService`、`IUiDispatcher`、logger；不注入 `ShellViewModel`）、`CategoryOption`、`ClearDataDialogViewModel`、`ExclusionsDialogViewModel`（含 `ExclusionItem`）、`DataSizeFormatter`。
- `Contexo.Desktop/Views/Settings/`：`SettingsView`（外觀、檔案類型、使用方式、進階四張卡片）、`ClearDataDialogView`、`ExclusionsDialogView`（依 ViewLocator 慣例自動對應，`App.axaml`、DI 都不需改）。
- `Platform/Windows/StartupRegistration.cs`：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`、值名 `Contexo`、內容 `"{Contexo.exe 完整路徑}" --minimized`；`IsEnabled` 要求登錄值裡的路徑（不分大小寫）與目前執行檔相同。登錄檔存取抽成內部介面 `IRunKeyStore`（真實作 `RegistryRunKeyStore` 標 `[SupportedOSPlatform("windows")]`），測試只用假實作。
- `Platform/Mac/StartupRegistration.cs`：寫入 `~/Library/LaunchAgents/tw.contexo.desktop.plist`（`Label`、`ProgramArguments`、`RunAtLoad`=true），關閉時只刪除這個自己建立的 plist，不呼叫 `launchctl`。若是用 `dotnet run`（程序是 `dotnet`）啟動，`ProgramArguments` 會是 `dotnet`、Contexo.dll、`--minimized`。
- 測試：`tests/Contexo.App.Tests/Settings/`（44 個）、`tests/Contexo.Desktop.Tests/Settings/`（`StartupRegistrationTests` 21 個、`SettingsViewTests` 16 個，含截圖）。

### 行為重點

- 每個設定變更都用 `record with` 產生新物件，排入**序列化的儲存佇列**，輪到時才套用在最新的 `Current` 上，所以連續切換以最後一次為準；儲存中不會被自己的 `Changed` 事件把畫面蓋回舊值；儲存失敗會顯示訊息並把畫面還原成實際儲存的值。
- 檔案類型：郵件、圖片停用並註明「之後的版本提供」，不計入「至少保留一個」，儲存時原樣保留（`AppSettings` 預設把 Email 放在啟用清單裡，所以不能整份覆寫）。取消勾選先 `ConfirmAsync`，按取消或被擋下時勾選會復原。**不另外呼叫 `RequestRescan`**：T10 的 `IndexingService` 自己監聽 `ISettingsStore.Changed`，類別或大小上限改變時會全量對帳。
- 開機啟動：切換開關時先呼叫 `IStartupRegistration.SetEnabled`，成功才儲存設定；系統拒絕時設定不變、開關彈回並顯示訊息。另外 `SettingsViewModel` 建構時（以及 `FirstRunCompleted`／`LaunchAtStartup` 被其他處改變時）會在「首次精靈完成後」讓登錄值與設定一致，這同時達成「程式搬移或更新後路徑不同就自動修正」；首次精靈完成前不會註冊。
- 清除全部資料：自訂對話框（勾「我了解」才能按；Enter 不會確認；Esc 關閉不清除；「清除後立即重新建立」預設勾選）；確認後 `Pause()` → `ClearIndexedDataAsync` → 依選項 `RequestRescan(null)` → `Resume()`（`finally` 保證 Resume，失敗顯示錯誤）；清除期間 `IsAdvancedEnabled=false`，進階區塊整個停用。完成訊息「已清除 1.8 GB 資料，正在重新建立。」，大小為清除前後資料庫大小的差。
- 進階區塊預設收合（自製標題按鈕＋箭頭，沒有用 Fluent `Expander`，因為它會多一層邊框）；展開或回到此頁時才讀 `GetStatisticsAsync`／`GetExclusionsAsync`。
- 「運算來源」區塊依任務檔不顯示。

### 驗收條件結果（macOS，.NET 10）

1. `dotnet test --filter FullyQualifiedName~Contexo.App.Tests.Settings`：44 個全通過。涵蓋每個設定變更都 `SaveAsync` 且值正確、連續變更以最後一次為準且依序儲存、最後一個類別不能取消、取消類別有確認且按取消設定不變、開機啟動同步呼叫與失敗還原、清除全部資料（未勾「我了解」不能確認、Pause→Clear→Rescan→Resume 順序、不重建時沒有 Rescan、失敗仍 Resume 並顯示錯誤、Esc 不清除）、排除清單恢復（`RemoveExclusionAsync`＋`RequestRescan(null)`）、容量格式化（KB／MB／GB、一位小數、邊界進位）。
2. `dotnet build Contexo.slnx -warnaserror`：0 警告 0 錯誤。`dotnet test` 全部通過：Core 909（14 略過，模型不存在）、App 69、Mcp 5、Desktop 75。
3. Headless：設定頁 淺色／深色 × 標準／大／特大 6 張截圖（`artifacts/screenshots/settings-{light|dark}-{standard|large|extralarge}.png`）、進階展開、清除資料對話框（未勾／已勾）、排除清單對話框、取消勾選確認、清除完成訊息；另測 Enter 不會確認危險操作、切換分段按鈕與開關會立即儲存。
4. macOS 實際操作：**部分完成**。`dotnet run --project src/Contexo.Desktop`（獨立暫存 `CONTEXO_DATA_DIR`，設定檔預先放 `FirstRunCompleted=true、LaunchAtStartup=false`）能正常啟動、無例外，日誌出現「Contexo started」，證明 DI 能組出 `SettingsViewModel`，且沒有建立任何 LaunchAgent。**未能驗證**：本環境沒有螢幕擷取與輔助使用權限，無法在真實視窗點選，所以「主題與字級即時切換」「開機啟動寫入真實 LaunchAgent、登出再登入後以選單列圖示啟動」「清除全部資料的真實流程」都**需要人在 Mac 上確認**（畫面行為以 Headless 截圖與上列測試替代）。為避免改到使用者真實的登入項目，測試與自動執行都沒有寫入 `~/Library/LaunchAgents`。
5. Windows 開機啟動項目：`tests/manual/CHECKLIST.md` 目前不存在且不在本任務可修改範圍，請彙整（見下）。

### 待在 Windows 確認（請彙整到 tests/manual/CHECKLIST.md）

- 設定頁「開機時自動啟動」開啟後，`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 出現值 `Contexo`，內容為 `"…\Contexo.exe" --minimized`；關閉後該值被刪除（其他程式的值不受影響）。
- 工作管理員「啟動」分頁看得到 Contexo；重開機（或登出再登入）後程式以系統匣圖示啟動、不彈出主視窗。
- 把安裝資料夾搬到別處再啟動：登錄值自動改成新路徑（設定為開啟時）。
- 公司電腦若以群組原則禁止寫入 Run 機碼：開關彈回並出現「無法設定開機自動啟動…」。
- 設定頁在 Windows 的外觀（`Microsoft JhengHei UI` 字型、三段字級版面、深淺色切換）與 macOS 截圖一致；「開啟」資料夾按鈕能開啟 `%LOCALAPPDATA%\Contexo` 與 `logs`。

### 與規格不同或規格未寫處

- `SettingsViewModel` 另有 `internal` 的無參數建構式（無服務、顯示預設值、不做任何事）。原因：`TestShell`（Desktop.Tests）與 `ShellViewModelTests`（App.Tests）都以 `new SettingsViewModel()` 建立，這兩個檔案不在本任務範圍；其他並行任務的頁面 VM 也面臨同樣問題。DI 只會選 public 建構式，不受影響。**維運者合併後若這兩個檔案改用假服務建立，可以刪掉這個建構式。**
- Fluent 的 `ToggleSwitch`／`CheckBox` 在淺色主題顯示的是預設藍（#0078D4）而不是 `Brush.Accent`（#2e5f8f），深色則正確。`Themes/Colors.axaml` 的 Fluent 強調色覆寫在淺色字典似乎沒有生效，這是 T15 範圍，沒有動。
- 清除資料的完成訊息顯示的是清除前後資料庫大小的差（VACUUM 之後的實際釋放量）；若差為 0 則顯示「已清除全部資料」。
- 對話框第四條警語（公司伺服器）依「第一版不做運算來源」未放入。
- 設定頁排除清單排序依路徑。

### 給後續任務的注意事項

- `ErrorMessage`／`InfoMessage` 是頁面最上方的橫幅，用 `Brush.BadSoft`／`Brush.OkSoft`。
- 換 embedding 模型後舊向量不會清除（T10 紀錄）；本頁「清除全部資料」是目前唯一會清掉它們的入口。
- `MinimizeToTray=false` 時關閉視窗即結束程式（T15）；本頁只負責寫設定。

