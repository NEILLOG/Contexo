# T15 WPF 外殼、主題與系統匣

- **狀態**：待辦
- **波次**：1
- **相依**：T01
- **必讀**：`AGENTS.md`（含 10a 節）、`plan/04-ui.md`、`plan/ui-mockup.html`（用瀏覽器開啟，開啟設計註記）、`src/Contexo.Core/Abstractions/AppEnvironment.cs`、`Indexing.cs`

## 目標

建立 WPF 程式的外殼：主視窗版面、導覽、主題（跟隨 Windows / 淺色 / 深色）、三段字級、系統匣常駐、單一執行個體、狀態列，以及後續頁面任務會用到的共用服務。**各頁面內容不在此任務**，只建立空白頁面讓後續任務填入。

## 要做

### 1. `Contexo.App`（ViewModel，不可引用 WPF）

- `Shell/ShellViewModel`：導覽項目清單（資料夾、試試看搜尋、AI 軟體、設定、關於與問題回報）、目前頁面、狀態列 VM、`FirstRunCompleted` 為 false 時顯示首次啟動精靈。
- `Shell/StatusBarViewModel`：訂閱 `IIndexingService.SnapshotChanged` 顯示「處理中 38%」或「已是最新」、最近活動（`RecentActivity` 轉成「剛剛更新了 3 個檔案」）、版本號；每 30 秒呼叫 `IAiClientStatusService` 顯示「Claude Desktop 已連線」這類摘要（取狀態最好的一個，沒有就不顯示）。
- 每個頁面一個 **空白 ViewModel**（只有標題屬性），檔名與類別名固定，後續任務在原檔填內容：
  `Folders/FoldersViewModel`（T16）、`Folders/FirstRunViewModel`（T16）、`Search/SearchViewModel`（T17）、`AiClients/AiClientsViewModel`（T18）、`Settings/SettingsViewModel`（T19）、`About/AboutViewModel`（T20）。
- 共用服務介面（`Services/`），實作在 Wpf：
  - `IDialogService`：`Task<bool> ConfirmAsync(ConfirmRequest request)`（標題、說明條列、確認按鈕文字、是否危險操作、可選的「我了解」勾選文字——有設定時要勾選才能按確認）、`Task ShowAsync(object dialogViewModel)`（以 DataTemplate 顯示自訂對話內容，VM 實作 `IDialogContent` 提供 `Title` 與關閉事件）。
  - `IFolderPicker`：`string? PickFolder(string? initialPath)`。
  - `IFilePicker`：`string? PickSaveFolder()`（給 T20 匯出用）。
  - `IShellLauncher`：`OpenFile(path)`、`RevealInExplorer(path)`、`OpenFolder(path)`。
  - `IUiDispatcher`：`void Post(Action action)`。
  - `IClipboardService`：`bool TrySetText(string text)`。
  - `IAppLifetime`：`void ShowMainWindow()`、`void Exit()`。
  - `IPageLifecycle`：頁面 VM 可選擇實作 `void OnNavigatedTo()`、`void OnNavigatedFrom()`；`ShellViewModel` 切換頁面、以及主視窗隱藏到系統匣／再顯示時呼叫（隱藏時視同離開頁面）。
  - `INavigationService`：`void NavigateTo<TViewModel>()`（例如資料夾頁的錯誤橫幅跳到「關於與問題回報」）。
  - `IStartupRegistration`：`bool IsEnabled { get; }`、`void SetEnabled(bool enabled)`。T15 只建立介面與 `Platform/StartupRegistration.cs` 的 stub（`IsEnabled` 回 false、`SetEnabled` 不做事），T19 實作。
- **DI 註冊**：T15 在 Wpf 的組裝程式碼中註冊所有頁面 VM 與上列服務。後續任務新增的對話框 VM 由頁面 VM 直接建立（以建構式傳入相依），**不需要**再註冊 DI。
- `UserMessages/ErrorText.cs`：把 `DocumentErrorCode`、`FolderState`、`ClientConnectionState` 轉成白話繁中（例：`PasswordProtected` →「有密碼保護」、`Locked` →「正被其他程式開啟」、`Unavailable` →「無法存取」）。後續頁面共用。

### 2. `Contexo.Wpf`

- `MainWindow`：左側導覽（寬約 190）＋右側內容（`ContentControl` 綁定目前頁面 VM，用 DataTemplate 對應 View）＋底部狀態列；標題「文脈 Contexo」。最小尺寸 900×600。
- `Views/` 依區域分資料夾，每區一個空白頁面 `UserControl`（內容只放頁面標題）與一個空白 `ResourceDictionary`，供該區任務放自己的 DataTemplate（例如對話框內容），後續任務**不需要修改 `App.xaml`**：

  | 區域 | 頁面 | 範本字典 |
  |---|---|---|
  | Folders | `Views/Folders/FoldersView.xaml`、`Views/Folders/FirstRunView.xaml` | `Views/Folders/FoldersTemplates.xaml` |
  | Search | `Views/Search/SearchView.xaml` | `Views/Search/SearchTemplates.xaml` |
  | AiClients | `Views/AiClients/AiClientsView.xaml` | `Views/AiClients/AiClientsTemplates.xaml` |
  | Settings | `Views/Settings/SettingsView.xaml` | `Views/Settings/SettingsTemplates.xaml` |
  | About | `Views/About/AboutView.xaml`、`Views/About/StartupErrorView.xaml` | `Views/About/AboutTemplates.xaml` |

  `App.xaml` 合併這五個字典，並寫好頁面 VM → View 的 DataTemplate。`StartupErrorView` 是資料庫初始化失敗時顯示的畫面：白話說明＋「匯出問題回報」按鈕（先停用，T20 接上）。
- **主題**：`Themes/Light.xaml`、`Themes/Dark.xaml` 定義 `AGENTS.md` 10a 節列出的所有資源鍵；`Themes/Controls.xaml` 定義按鈕、標籤（Chip）、文字樣式、清單、輸入框、捲軸、核取方塊、選項按鈕、進度條的樣式，全部用 `DynamicResource` 引用顏色。顏色取自 `plan/ui-mockup.html` 的 CSS token（淺色：`--bg #eef1f4`、`--win #fbfcfd`、`--panel #f3f5f8`、`--line #d9dee5`、`--fg #1d2733`、`--muted #5f6b7a`、`--accent #2e5f8f`、`--accent-soft #e3ecf5`、`--ok #2f7d4f`…；深色值見同檔）。
- `Platform/ThemeManager`：依 `AppSettings.Theme` 套用；`System` 時讀 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`（不存在視為淺色），並監聽 `SystemEvents.UserPreferenceChanged` 即時切換。切換方式：替換 `Application.Resources.MergedDictionaries` 中的主題字典。標題列跟隨深淺色：呼叫 `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE = 20)`，失敗忽略。
- **字級**：`FontScale` 對應 1.0 / 1.12 / 1.25，套用在主視窗內容根元素的 `LayoutTransform`（`ScaleTransform`），設定變更即時生效；對話框也要套用。
- `Platform/TrayIcon`：WinForms `NotifyIcon`，選單「開啟 Contexo」「暫停處理／繼續處理」（依 `IndexingSnapshot.State`）「結束」；雙擊開啟。關閉主視窗時若 `MinimizeToTray` 為 true 則隱藏視窗，第一次隱藏時顯示氣球提示「Contexo 仍在背景執行，從這裡可以再打開」。圖示先用簡單的 `.ico`（可程式產生或手繪，T21 會換正式圖示）。
- `Platform/SingleInstance`：具名 `Mutex`（名稱含使用者 SID）；第二個執行個體以 `EventWaitHandle` 通知第一個顯示主視窗後結束。
- `Platform/UserActivityMonitor`：以 `GetLastInputInfo` 實作 `IUserActivityMonitor`，在 DI 中覆寫 Core 的預設實作。
- `Platform/*`：上述 App 服務介面的實作。`DialogService` 用主視窗內的覆蓋層（半透明遮罩＋置中卡片）呈現，不用 `MessageBox`，Esc 取消、Enter 確認（危險操作的確認按鈕不設為預設）。
- 啟動流程（`App.xaml.cs`）：單一執行個體檢查 → Host 啟動 → `IKnowledgeStore.InitializeAsync`（失敗時顯示錯誤畫面並提供「匯出問題回報」按鈕的位置，按鈕行為由 T20 完成前先停用）→ `IIndexingService.StartAsync` → 顯示主視窗（啟動參數 `--minimized` 時只顯示系統匣）。
- 未處理例外：`DispatcherUnhandledException`、`AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException` 記錄到日誌，UI 執行緒例外顯示白話錯誤訊息後繼續執行。

## 不做

- 各頁面的內容（T16～T20）。
- 開機自動啟動的登錄設定（T19）。
- 安裝程式與正式圖示（T21）。

## 可修改範圍

- `src/Contexo.App/Shell/**`、`src/Contexo.App/Services/**`、`src/Contexo.App/UserMessages/**`、`src/Contexo.App/ViewModels/**`
- 各頁面 ViewModel 的**初始空白檔**（建立後交給後續任務）
- `src/Contexo.Wpf/**`（`Views/` 下各區域的頁面與範本字典只建立空白檔；`Platform/StartupRegistration.cs` 只建 stub）
- `tests/Contexo.App.Tests/Shell/**`、`tests/Contexo.App.Tests/UserMessages/**`

## 實作要點與已知陷阱

- `Contexo.App` 只能引用 `CommunityToolkit.Mvvm` 與 Core，不能引用 `System.Windows`。
- 狀態列事件在背景執行緒觸發，更新 VM 時透過 `IUiDispatcher`，並節流（最多每 250ms 更新一次）。
- `DynamicResource` 才能即時換主題；樣式中的 `Setter.Value` 也要用 `DynamicResource`。
- 系統匣結束時要 `Dispose` `NotifyIcon`，否則圖示殘留。
- 在 macOS / Linux 只能編譯，請在完成紀錄列出需要在 Windows 人工確認的項目。

## 驗收條件

1. `dotnet build Contexo.sln -warnaserror` 成功（任何 OS）。
2. `dotnet test --filter FullyQualifiedName~Contexo.App.Tests` 通過，至少涵蓋：
   - `ShellViewModel`：導覽切換、首次啟動時顯示精靈。
   - `StatusBarViewModel`：以假的 `IIndexingService` 送出快照，文字正確（處理中百分比、已是最新、最近活動）；節流有效。
   - `ErrorText`：每個列舉值都有對應文字（以反射檢查，避免日後新增列舉值忘了翻譯）。
3. 在 Windows 人工確認（列入完成紀錄的檢查表）：
   - 主視窗、導覽、狀態列顯示正常；切換「跟隨 Windows / 淺色 / 深色」（可先在程式碼中暫時切換驗證）即時生效，Windows 切換深淺色時跟著變。
   - 三段字級即時生效，版面不破。
   - 關閉視窗後縮到系統匣，氣球提示只出現一次；系統匣選單可開啟與結束。
   - 第二次執行程式只會叫出既有視窗。
   - 確認對話框（以測試按鈕觸發後移除）：Esc、Enter 行為正確，「我了解」勾選後才能確認。

## 完成紀錄

（由執行者填寫）
