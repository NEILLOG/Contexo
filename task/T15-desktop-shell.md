# T15 桌面外殼（Avalonia）、主題與系統匣

- **狀態**：完成
- **波次**：1
- **相依**：T01（T00 已判定通過）
- **必讀**：`AGENTS.md`（含 10a 節）、`plan/04-ui.md`、`plan/ui-mockup.html`（用瀏覽器開啟，開啟設計註記）、`poc/ime-avalonia/README.md`（POC 結果與發現的問題）、`src/Contexo.Core/Abstractions/AppEnvironment.cs`、`Indexing.cs`

## 目標

建立 Avalonia 桌面程式的外殼：主視窗版面、導覽、主題（跟隨系統／淺色／深色）、三段字級、系統匣常駐、單一執行個體、狀態列，以及後續頁面任務會用到的共用服務。**各頁面內容不在此任務**，只建立空白頁面讓後續任務填入。

程式在 **Windows（正式目標）與 macOS（開發驗證）** 都必須能啟動並操作。

## 要做

### 1. `Contexo.App`（ViewModel，不可引用 Avalonia）

- `Shell/ShellViewModel`：導覽項目清單（資料夾、試試看搜尋、AI 軟體、設定、關於與問題回報）、目前頁面、狀態列 VM、`FirstRunCompleted` 為 false 時顯示首次啟動精靈。
- `Shell/StatusBarViewModel`：訂閱 `IIndexingService.SnapshotChanged` 顯示「處理中 38%」或「已是最新」、最近活動（`RecentActivity` 轉成「剛剛更新了 3 個檔案」）、版本號；每 30 秒呼叫 `IAiClientStatusService` 顯示「Claude Desktop 已連線」這類摘要（取狀態最好的一個，沒有就不顯示）。
- 每個頁面一個 **空白 ViewModel**（只有標題屬性），檔名與類別名固定，後續任務在原檔填內容：
  `Folders/FoldersViewModel`（T16）、`Folders/FirstRunViewModel`（T16）、`Search/SearchViewModel`（T17）、`AiClients/AiClientsViewModel`（T18）、`Settings/SettingsViewModel`（T19）、`About/AboutViewModel`（T20）。
- 共用服務介面（`Services/`），實作在 Desktop：
  - `IDialogService`：`Task<bool> ConfirmAsync(ConfirmRequest request)`（標題、說明條列、確認按鈕文字、是否危險操作、可選的「我了解」勾選文字——有設定時要勾選才能按確認）、`Task ShowAsync(object dialogViewModel)`（以 ViewLocator 顯示自訂對話內容，VM 實作 `IDialogContent` 提供 `Title` 與關閉事件）。
  - `IFolderPicker`：`Task<string?> PickFolderAsync(string? initialPath)`。
  - `IFilePicker`：`Task<string?> PickSaveFolderAsync()`（給 T20 匯出用）。
  - `IShellLauncher`：`OpenFile(path)`、`RevealInFileManager(path)`、`OpenFolder(path)`。
  - `IUiDispatcher`：`void Post(Action action)`。
  - `IClipboardService`：`Task<bool> TrySetTextAsync(string text)`。
  - `IAppLifetime`：`void ShowMainWindow()`、`void Exit()`。
  - `IPageLifecycle`：頁面 VM 可選擇實作 `void OnNavigatedTo()`、`void OnNavigatedFrom()`；`ShellViewModel` 切換頁面、以及主視窗隱藏到系統匣／再顯示時呼叫（隱藏時視同離開頁面）。
  - `INavigationService`：`void NavigateTo<TViewModel>()`（例如資料夾頁的錯誤橫幅跳到「關於與問題回報」）。
  - `IStartupRegistration`：`bool IsEnabled { get; }`、`void SetEnabled(bool enabled)`。T15 只建立介面與兩個平台的 stub（`IsEnabled` 回 false、`SetEnabled` 不做事），T19 實作。
- **DI 註冊**：T15 在 Desktop 的組裝程式碼中註冊所有頁面 VM 與上列服務。後續任務新增的對話框 VM 由頁面 VM 直接建立（以建構式傳入相依），**不需要**再註冊 DI。
- `UserMessages/ErrorText.cs`：把 `DocumentErrorCode`、`FolderState`、`ClientConnectionState` 轉成白話繁中（例：`PasswordProtected` →「有密碼保護」、`Locked` →「正被其他程式開啟」、`Unavailable` →「無法存取」）。後續頁面共用。

### 2. `Contexo.Desktop`

- **ViewLocator**（`ViewLocator.cs`，註冊在 `App.axaml` 的 `Application.DataTemplates`）：依命名慣例把 VM 對應到 View——`Contexo.App.<區域>.<名稱>ViewModel` → `Contexo.Desktop.Views.<區域>.<名稱>View`。後續任務新增頁面或對話框時，只要照慣例命名 View，**不需要修改 `App.axaml`**。找不到 View 時顯示「找不到畫面：{型別}」的文字方塊（方便開發時發現）。
- `MainWindow.axaml`：左側導覽（寬約 190）＋右側內容（`ContentControl` 綁定目前頁面 VM）＋底部狀態列＋覆蓋層對話框；標題「文脈 Contexo」。最小尺寸 900×600。
- `Views/` 依區域分資料夾，每區先建立空白頁面 `UserControl`（內容只放頁面標題）：

  | 區域 | 頁面 |
  |---|---|
  | Folders | `Views/Folders/FoldersView.axaml`、`Views/Folders/FirstRunView.axaml` |
  | Search | `Views/Search/SearchView.axaml` |
  | AiClients | `Views/AiClients/AiClientsView.axaml` |
  | Settings | `Views/Settings/SettingsView.axaml` |
  | About | `Views/About/AboutView.axaml`、`Views/About/StartupErrorView.axaml` |

  `StartupErrorView` 是資料庫初始化失敗時顯示的畫面：白話說明＋「匯出問題回報」按鈕（先停用，T20 接上）。
- **佈景與主題**：
  - `App.axaml` 使用免費內建的 `FluentTheme` 作為基礎控制項樣式（**不使用**任何付費元件或付費佈景）。
  - `Themes/Colors.axaml`：`ResourceDictionary` 的 `ThemeDictionaries` 分別定義 `Light` 與 `Dark` 兩組，包含 `AGENTS.md` 10a 節列出的所有資源鍵。顏色取自 `plan/ui-mockup.html` 的 CSS token（淺色：`--bg #eef1f4`、`--win #fbfcfd`、`--panel #f3f5f8`、`--line #d9dee5`、`--fg #1d2733`、`--muted #5f6b7a`、`--accent #2e5f8f`、`--accent-soft #e3ecf5`、`--ok #2f7d4f`…；深色值見同檔）。同時覆寫 Fluent 主題的強調色資源，讓內建控制項與草圖一致。
  - `Themes/Controls.axaml`：按鈕、標籤（Chip）、文字樣式、清單、輸入框、核取方塊、選項按鈕、分段按鈕、進度條的樣式（Avalonia `Style` / `ControlTheme`），全部用 `DynamicResource` 引用顏色。
  - `Platform/Common/ThemeManager`：依 `AppSettings.Theme` 設定 `Application.RequestedThemeVariant`（`System` → `ThemeVariant.Default`，由 Avalonia 自動跟隨 Windows 與 macOS 的深淺色；`Light` / `Dark` 固定）。監聽 `ISettingsStore.Changed` 即時切換。
- **字型**：全域字型設為 `"Microsoft JhengHei UI, Microsoft JhengHei, PingFang TC, Noto Sans CJK TC, Noto Sans TC"`（依 POC 結果調整）。
- **字級**：`FontScale` 對應 1.0 / 1.12 / 1.25，以 `LayoutTransformControl` 包住主視窗內容與覆蓋層對話框，設定變更即時生效。
- **系統匣**（`App.axaml` 的 `TrayIcon.Icons`，Avalonia 內建）：`NativeMenu` 選單「開啟 Contexo」「暫停處理／繼續處理」（依 `IndexingSnapshot.State`）「結束」。Windows 左鍵點擊開啟主視窗；macOS 點擊顯示選單（平台預設行為）。關閉主視窗時若 `MinimizeToTray` 為 true 則隱藏視窗；**第一次**隱藏前在視窗內顯示提示「Contexo 會在背景繼續執行，可以從右下角（Mac 為上方選單列）的圖示再打開」＋「知道了」，之後不再提示（記在設定中）。圖示先用簡單的 PNG／ICO（可程式產生，T21 會換正式圖示）。
- **單一執行個體**（`Platform/Common/SingleInstance`）：具名 `Mutex`（名稱含使用者名稱）判斷；第二個執行個體透過 **named pipe**（`NamedPipeServerStream`，跨平台）通知第一個顯示主視窗後結束。**不要用具名 `EventWaitHandle`**（macOS 不支援）。
- **平台實作**（依 `OperatingSystem.IsWindows()` / `IsMacOS()` 在啟動時選擇；其他系統用 Mac 版或空實作，供 CI 的 Headless 測試使用）：

  | 服務 | `Platform/Windows/` | `Platform/Mac/` |
  |---|---|---|
  | `IUserActivityMonitor` | `GetLastInputInfo` | CoreGraphics `CGEventSourceSecondsSinceLastEventType`（P/Invoke）；做不到就回傳一律閒置 |
  | `IShellLauncher` | `explorer.exe` 與 `/select,` | `open` 與 `open -R` |
  | `IStartupRegistration` | stub（T19 實作登錄檔） | stub（T19 實作 LaunchAgent） |

  檔案與資料夾選擇、剪貼簿用 Avalonia 的 `StorageProvider` 與 `Clipboard`（跨平台，放在 `Platform/Common/`）。
- `DialogService`：主視窗內的覆蓋層（半透明遮罩＋置中卡片），Esc 取消、Enter 確認（危險操作的確認按鈕不設為預設）。
- **啟動流程**（`App.axaml.cs` 的 `OnFrameworkInitializationCompleted`）：單一執行個體檢查 → Host 啟動 → `IKnowledgeStore.InitializeAsync`（失敗時顯示 `StartupErrorView`）→ `IIndexingService.StartAsync` → 顯示主視窗（啟動參數 `--minimized` 時只顯示系統匣）。
- 未處理例外：`AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException` 與 UI 執行緒例外（`Dispatcher.UIThread.UnhandledException`）記錄到日誌；UI 執行緒例外顯示白話錯誤訊息後繼續執行。

### 3. Headless 畫面測試基礎（`tests/Contexo.Desktop.Tests/`）

- 使用 `Avalonia.Headless.XUnit` 建立測試基礎設施：`TestAppBuilder`（套用 `FluentTheme` 與 Contexo 的 `Colors.axaml`、`Controls.axaml`）。
- 提供 `ScreenshotHelper.Capture(Control control, string name)`：以 Skia 實際繪製並輸出 PNG 到 `artifacts/screenshots/{name}.png`（不提交到 git），供人與代理檢視畫面。後續頁面任務沿用。

## 不做

- 各頁面的內容（T16～T20）。
- 開機自動啟動的實作（T19）。
- 安裝程式與正式圖示（T21）。

## 可修改範圍

- `src/Contexo.App/Shell/**`、`src/Contexo.App/Services/**`、`src/Contexo.App/UserMessages/**`、`src/Contexo.App/ViewModels/**`
- 各頁面 ViewModel 的**初始空白檔**（建立後交給後續任務）
- `src/Contexo.Desktop/**`（`Views/` 下各區域頁面只建立空白檔；兩個 `StartupRegistration.cs` 只建 stub）
- `tests/Contexo.App.Tests/Shell/**`、`tests/Contexo.App.Tests/UserMessages/**`、`tests/Contexo.Desktop.Tests/**`

## 實作要點與已知陷阱

- `Contexo.App` 只能引用 `CommunityToolkit.Mvvm` 與 Core，不能引用 Avalonia。
- 狀態列事件在背景執行緒觸發，更新 VM 時透過 `IUiDispatcher`（`Dispatcher.UIThread.Post`），並節流（最多每 250ms 更新一次）。
- 使用 compiled bindings（`x:DataType`），讓繫結錯誤在編譯時發現。
- `TrayIcon` 必須定義在 `Application` 層級，選單用 `NativeMenu`（不能用一般 `Menu`）；圖示要以 `AvaloniaResource` 加入專案。
- Avalonia 12 的 API 與網路上許多 11.x 範例不同；以官方 v12 文件與 T01 鎖定版本為準。
- 參考 `poc/ime-avalonia/README.md` 的測試結果：若 POC 有「有條件通過」的問題，在本任務中套用已知的避免方式。

## 驗收條件

1. `dotnet build Contexo.slnx -warnaserror` 成功（Windows、macOS、Linux）。
2. `dotnet test` 通過，至少涵蓋：
   - `ShellViewModel`：導覽切換、首次啟動時顯示精靈。
   - `StatusBarViewModel`：以假的 `IIndexingService` 送出快照，文字正確（處理中百分比、已是最新、最近活動）；節流有效。
   - `ErrorText`：每個列舉值都有對應文字（以反射檢查，避免日後新增列舉值忘了翻譯）。
   - `ViewLocator`：命名慣例對應正確；找不到時顯示提示文字。
   - Headless：主視窗在淺色、深色、三段字級下都能建立並輸出截圖；確認對話框的 Esc／Enter、「我了解」勾選後才能確認。
3. **macOS 實際操作**（由執行者或使用者在 Mac 上以 `dotnet run --project src/Contexo.Desktop` 確認，列入完成紀錄）：
   - 主視窗、導覽、狀態列顯示正常；系統深淺色切換時跟著變；三段字級即時生效、版面不破。
   - 選單列圖示出現，選單可開啟與結束；關閉視窗後第一次有提示。
   - 第二次執行只會叫出既有視窗。
   - 用注音在任一輸入框輸入（沿用 POC 的 #1～#4）。
4. **Windows 實際操作**（可延後到 T21 前統一進行，列入 `tests/manual/CHECKLIST.md`）：同上，加上系統匣位置與左鍵點擊行為。

## 完成紀錄

### 做了什麼

- `Contexo.App`：`Shell/`（`ShellViewModel`、`StatusBarViewModel`、`NavigationService`、`DialogHostViewModel`、`ConfirmDialogViewModel`）、`Services/`（九個介面）、`UserMessages/ErrorText`、六個空白頁面 VM 與 `About/StartupErrorViewModel`。
- `Contexo.Desktop`：`ViewLocator`、`MainWindow`（導覽、狀態列、覆蓋層對話框、`LayoutTransformControl` 字級）、各區域空白 View、`Views/Shell/ConfirmDialogView`、`Themes/Colors.axaml`（Light/Dark，含 Fluent 強調色覆寫）、`Themes/Controls.axaml`、`Controls/StatusDot`、`Platform/Common`（`ThemeManager`、`SingleInstance`、`TrayCoordinator`、`AppLifetimeService`、Avalonia 選擇器與剪貼簿、`AddContexoDesktop` DI 註冊）、`Platform/Windows`、`Platform/Mac`、`Assets/tray.png`、`Assets/app.png`（程式產生的暫用圖示）、`App.axaml(.cs)`（TrayIcon、啟動流程、例外處理）。
- 測試：`tests/Contexo.App.Tests/Shell`、`UserMessages`（25 個）；`tests/Contexo.Desktop.Tests`（38 個，含 `ScreenshotHelper`、`TestShell`）。

### 驗收條件結果

1. `dotnet build Contexo.slnx -warnaserror`：成功，0 警告 0 錯誤（macOS；Windows、Linux 由 CI 確認）。
2. `dotnet test`：App.Tests 25、Desktop.Tests 38、Mcp.Tests 5、Core.Tests 513（14 略過，模型不存在）全數通過。涵蓋：Shell 導覽與首次啟動精靈、狀態列文字與 250ms 節流（自製 `ManualTimeProvider`）、`ErrorText` 以反射檢查每個列舉值、ViewLocator 命名慣例與找不到畫面、Headless 主視窗在淺色／深色 × 三段字級（6 組）可建立並輸出截圖、對話框 Esc／Enter／「我了解」勾選、危險操作 Enter 不確認、單一執行個體（named pipe）。截圖輸出在 `artifacts/screenshots/`（不提交）。
3. macOS 實際操作：**部分完成**。`dotnet run` 實際啟動成功（資料庫建立、日誌正常、無例外）；第二次執行會立即結束（結束碼 0）且第一個執行個體保持運作、日誌只出現一次「Contexo started」；SIGTERM 能正常關閉。**無法驗證**：本代理環境沒有螢幕擷取與輔助使用權限（`screencapture` 失敗、osascript 被拒），所以選單列圖示、視窗外觀、系統深淺色切換、字級即時切換、關閉視窗的首次提示、注音輸入（POC #1～#4）都**需要人在 Mac 上確認**。畫面外觀以 Headless 截圖替代（Skia 實際繪製）。
4. Windows：未執行，見下方清單。

### 與規格不同之處

- `IDialogService` 的實作是 `Contexo.App/Shell/DialogHostViewModel`（純 VM，可不靠 Avalonia 測試），不是 Desktop 內的 `DialogService` 類別；DI 把 `IDialogService` 指向它。對呼叫端沒有差別。
- `ConfirmRequest` 多一個選用參數 `CancelText`（預設「取消」；設為 null 則不顯示取消，用於「知道了」這類單純通知）。首次隱藏到系統匣的提示即用此。
- 第一次隱藏到系統匣的提示是否已顯示，存在 `IKnowledgeStore` 的 meta（鍵 `ui.tray_hint_shown`），因為 `AppSettings` 是共用契約不能新增欄位。若日後想改放設定檔，需要人決定是否擴充契約。
- 單一執行個體檢查依規格放在 `OnFrameworkInitializationCompleted`。具名 Mutex 在非 Windows 加 `Global\` 前綴（Unix 的無前綴名稱只在同一登入工作階段內有效，實測不同工作階段會各自成為第一個執行個體）。
- 視窗尺寸：最小 900×600，預設 1100×720（任務檔只規定最小值）。
- 狀態列另外顯示的「已收錄 N 個檔案」「運算來源」草圖項目沒有做（任務檔只列處理進度、最近活動、AI 軟體、版本號；運算來源屬第二階段）。
- 樣式用 Avalonia 的 style class 實作：`TextBlock.H1/H2/Body/Caption`、`Button.Primary/Secondary/Ghost/Danger`、`<Border Classes="Chip Ok">`（Ok/Running/Warn/Off）、`Border.Card`、`ListBox.Segmented`。用法寫在 `Themes/Controls.axaml` 檔頭。
- `tests/manual/CHECKLIST.md` 不在本任務可修改範圍，Windows 項目記在下方。
- `AppSettings.MinimizeToTray` 為 false 時，關閉視窗即結束程式。

### 待在 Windows 確認（請彙整到 tests/manual/CHECKLIST.md）

- 系統匣圖示出現在右下角；左鍵點擊開啟主視窗；右鍵選單「開啟 Contexo／暫停處理（繼續處理）／結束」。
- 關閉視窗第一次顯示「Contexo 會在背景繼續執行，可以從右下角…」提示，之後不再提示；按「結束」才真正離開。
- 系統深淺色切換時（設定為「跟隨系統」）即時跟著變；三段字級版面不破。
- 第二次執行只會叫出既有視窗（Mutex＋named pipe）。
- `GetLastInputInfo` 閒置時間（供 T10 使用）。
- 微軟注音（新版、舊版）在輸入框的 POC #1～#4、#10；字型 `Microsoft JhengHei UI` 顯示。
- `Platform/Windows/StartupRegistration` 為 stub，T19 實作。

### 給後續任務（T16～T20）的注意事項

- 頁面 VM 目前是 `public sealed class XxxViewModel : ViewModelBase { public string Title => ...; }`，在原檔擴充即可。需要 `INavigationService`、`IDialogService`、`IUiDispatcher` 等時直接建構式注入（皆已註冊）；頁面 VM 不要注入 `ShellViewModel`（會形成相依循環），導覽一律用 `INavigationService.NavigateTo<T>()`。
- 要在關閉對話框時回應，VM 實作 `IDialogContent`（`Title`、`CloseRequested`），用 `IDialogService.ShowAsync(vm)`；View 放 `Views/<區域>/<名稱>View.axaml`，卡片標題由主視窗畫，View 只放內容與按鈕列（參考 `Views/Shell/ConfirmDialogView.axaml`）。Esc 由主視窗處理（呼叫取消）；自訂對話框的 Enter 需要自己處理。
- 殼層只在內容區加 24,20 的邊距，**沒有** `ScrollViewer`；各頁自己決定捲動（清單頁請自己放 ScrollViewer）。字級放大到 125% 時可用寬度會變小，版面請用可換行的容器。
- 首次啟動精靈：完成時呼叫 `ISettingsStore.SaveAsync` 把 `FirstRunCompleted` 設為 true，殼層會自動收起精靈並切到資料夾頁。
- `IPageLifecycle`：視窗隱藏到系統匣時 `OnNavigatedFrom` 會被呼叫（再顯示時 `OnNavigatedTo`），頁面請在此停止／恢復更新。
- 狀態列每 30 秒呼叫 `IAiClientStatusService.GetStatusesAsync`，T14 未完成前是空清單，所以不顯示。`StatusBarViewModel.Start()` 只在資料庫初始化成功後呼叫。
- Headless 測試沿用 `TestShell`（用假服務組出真的 `ShellViewModel`）與 `ScreenshotHelper.Capture(control, name)`；`TestAppBuilder` 使用真的 `App`，因此與正式版共用主題。xunit v3 專案內請用 `TestContext.Current.CancellationToken`。
- Avalonia 12 差異：剪貼簿的 `SetTextAsync` 在 `Avalonia.Input.Platform` 的擴充方法；`Bitmap.Save` 需傳 `PngBitmapEncoderOptions`；Headless 要真的繪製需 `UseHeadlessDrawing = false` 加 `UseSkia()`。
- `ErrorText` 的 switch 故意沒有 `_` 分支，新增列舉值而沒翻譯會編譯失敗（CS8509）。
- 使用者資料目錄可用 `CONTEXO_DATA_DIR` 覆寫，手動執行桌面程式驗證時建議指到暫存資料夾。
