# T19 介面：設定頁

- **狀態**：待辦
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

（由執行者填寫）
