# T18 介面：AI 軟體頁

- **狀態**：完成（macOS 實機操作與 Windows 待人工確認，見完成紀錄）
- **波次**：3
- **相依**：T14、T15
- **必讀**：`AGENTS.md`（10a 節）、`plan/03-retrieval-and-mcp.md`（加入 AI 軟體、連線狀態偵測）、`plan/ui-mockup.html` 的「AI 軟體」頁、`src/Contexo.Core/Abstractions/Integrations.cs`

## 目標

讓使用者把 Contexo 加入 AI 軟體，並清楚看到每個軟體目前的狀態。文字從使用者角度描述（「加入到 Claude Desktop」，不說「連接」）。

## 要做

`AiClientsViewModel` / `AiClientsView`：

1. 標題「讓 AI 軟體使用我的資料」、說明「加入後，在這些 AI 軟體裡提問時，AI 就能查詢你的檔案。」
2. 每個**已安裝**的 AI 軟體一張卡片：圖示（名稱首字母的方塊即可）、名稱、狀態標籤、說明行、動作按鈕：

   | 狀態 | 標籤 | 說明行 | 按鈕 |
   |---|---|---|---|
   | `Connected` | 已連線（Ok） | 最後連線：今天 14:32 · 最後查詢：10 分鐘前 | 移除 |
   | `WaitingForConnection` | 已設定，等待連線（Running） | 請把 {名稱} 完全關閉後重新開啟。 | 移除 |
   | `NeedsRepair` | 設定有問題（Warn） | `Problem` 的白話說明 | 修復（主要） |
   | `NotAdded` | 尚未加入（Off） | 這台電腦已安裝。 | 加入（主要） |

   時間顯示：今天 → 「今天 HH:mm」；昨天；更早 → 「M/d HH:mm」；相對時間 → 「剛剛」「N 分鐘前」「N 小時前」。
3. **未安裝**的軟體收在下方一行「其他支援的 AI 軟體：Cursor、LM Studio」（不顯示按鈕）。
4. 下方說明框：「怎麼確認可以用了？在 AI 軟體裡問『用 Contexo 找報價單』，這裡的『最後查詢』時間就會更新。」
5. 動作：
   - 加入／修復：`AddOrRepair(CurrentLaunch)`；成功後顯示提示「已加入。請把 {名稱} 完全關閉後重新開啟。」；失敗（例如設定檔格式錯誤）以白話錯誤顯示在卡片內，並提供「複製設定內容」。
   - 移除：`ConfirmAsync`（「移除後，{名稱} 將無法查詢你的資料。你的資料仍保留在 Contexo。」）→ `Remove()`。
   - 每張卡片的「⋯」選單：「複製設定內容」（`BuildManualSnippet` → `IClipboardService.TrySetTextAsync`，成功顯示「已複製」）。
6. 頁面顯示時每 10 秒重新整理狀態，離開頁面停止；按鈕操作後立即重新整理。

## 不做

- 設定檔的讀寫邏輯（T14 已完成）。

## 可修改範圍

- `src/Contexo.App/AiClients/**`
- `src/Contexo.Desktop/Views/AiClients/**`
- `tests/Contexo.Desktop.Tests/AiClients/**`
- `tests/Contexo.App.Tests/AiClients/**`

## 實作要點與已知陷阱

- 重新整理用可取消的計時迴圈（`PeriodicTimer`），不要用 `DispatcherTimer`（VM 不能依賴 Avalonia）。頁面可見性用 T15 的 `IPageLifecycle`（`OnNavigatedTo` 開始、`OnNavigatedFrom` 停止）。
- 相對時間的「現在」用注入的 `TimeProvider`，方便測試。

## 驗收條件

1. `dotnet test --filter FullyQualifiedName~Contexo.App.Tests.AiClients` 通過，以假的 `IAiClientStatusService` 與假的整合測試：
   - 四種狀態的標籤、說明、按鈕對應。
   - 未安裝的軟體只出現在下方一行。
   - 加入成功、加入失敗（拋例外）、移除取消、移除確認。
   - 複製設定內容成功與失敗。
   - 時間文字（今天、昨天、更早、相對時間）。
   - 只有頁面可見時才定期重新整理。
2. 編譯成功。
3. Headless 測試：四種狀態的卡片截圖。
4. 在 macOS 實際操作確認（`dotnet run --project src/Contexo.Desktop`，列入完成紀錄）：在 Mac 上安裝 Claude Desktop，實際按「加入到 Claude Desktop」後設定檔內容正確；重新開啟 Claude Desktop 並問一個問題後，狀態變成「已連線」且最後查詢時間更新。Windows 上的同樣流程列入 `tests/manual/CHECKLIST.md`。

## 完成紀錄

### 做了什麼

- `src/Contexo.App/AiClients/`：`AiClientsViewModel`（卡片清單、未安裝一行、空狀態、`IPageLifecycle` 週期更新、加入／修復／移除／複製設定內容）、`AiClientCardViewModel`（單張卡片：標籤、說明行、按鈕、卡片內錯誤與提示）、`TimeText`（時間文字）。
- `src/Contexo.Desktop/Views/AiClients/AiClientsView.axaml`：標題、卡片（首字母方塊、狀態標籤、說明、動作按鈕、「⋯」選單）、卡片內錯誤框（含「複製設定內容」按鈕）與成功提示、未安裝一行、說明框。`.axaml.cs` 未動。
- 測試：`tests/Contexo.App.Tests/AiClients/`（43 個）、`tests/Contexo.Desktop.Tests/AiClients/`（8 個，含一個用真的整合類別與暫存使用者資料夾的端對端測試）。
- 建構式：`AiClientsViewModel(IAiClientStatusService, IDialogService, IClipboardService, IUiDispatcher, TimeProvider, ILogger<>)`，DI 已能自動解析（實際啟動桌面程式確認無例外）。另有 `internal` 無參數建構式（所有服務都是空實作），讓範圍外既有的 `ShellViewModelTests`、`TestShell` 的 `new AiClientsViewModel()` 不必修改就能編譯。

### 驗收條件結果

1. `dotnet test --filter FullyQualifiedName~Contexo.App.Tests.AiClients`：43 通過。涵蓋：四種狀態的標籤／說明／按鈕／樣式；未安裝只出現在下方一行（含全部未安裝的空狀態）；加入成功（用 `CurrentLaunch`、在背景執行緒、提示文字、立即重新整理）、修復、加入失敗（白話例外直接顯示、其他例外給通用句子、不外洩技術訊息）、下次操作清除舊錯誤、操作中按鈕停用；移除取消（不呼叫 `Remove`、不重新整理）、移除確認、移除遇到損毀設定檔；複製成功／失敗／剪貼簿拋例外／加入失敗後仍可複製；時間文字（今天、昨天、M/d、剛剛、N 分鐘前、N 小時前、跨時區的日界線、負值）；只有頁面可見才週期更新（未顯示不讀取、顯示後立刻讀取並每 10 秒一次、離開後停止且計時器釋放、再進入恢復、重複進入不會多個迴圈）。
2. `dotnet build Contexo.slnx -warnaserror`：0 警告 0 錯誤。
3. Headless 測試：四種狀態（淺色、深色）、加入後提示、加入失敗、全部未安裝、125% 字級的截圖，輸出在 `artifacts/screenshots/ai-clients-*.png`（不提交）；另驗證標籤 class、說明文字、按鈕文字與樣式、「⋯」選單的命令真的綁到該卡片並能複製。
4. `dotnet test` 全部：App 68、Mcp 5、Desktop 45 通過；`SingleInstanceTests.Can_be_woken_more_than_once` 在整批／整個類別執行時失敗，單獨執行通過（已知與機器負載有關的偶發失敗，本任務未動相關檔案）；`Core.Tests` 的 `VectorIndexTests.Search_50kVectorsOf512Dimensions_FinishesUnder100Ms`（效能門檻）在整批執行時失敗一次，單獨執行 4/4 通過。
5. macOS 實際操作：**部分完成**。`CONTEXO_DATA_DIR` 指向暫存資料夾執行 `dotnet run --project src/Contexo.Desktop`，啟動成功、DI 解析 `AiClientsViewModel` 無例外、日誌正常，之後已結束該程序。**未做**：本代理無螢幕擷取與輔助使用權限，且為避免改到真實的 Claude Desktop 設定，沒有在 Mac 上實際按「加入」、重新開啟 Claude Desktop 並提問。這部分需要人在 Mac 上確認（見下方清單）。取代方式：端對端 Headless 測試以真的 `ClaudeDesktopIntegration`＋`AiClientStatusService` 在暫存使用者資料夾（`ClientPathOptions` 全部指向暫存資料夾）驗證按「加入」後設定檔內容正確且保留原有設定、按「移除」後只移除 `contexo`、設定檔損毀時顯示白話錯誤且檔案不動。

### 與規格不同之處

- 修復成功的提示用「已修復。請把 {名稱} 完全關閉後重新開啟。」（任務檔寫「已加入。…」；修復時說「已加入」不自然）。其餘文字照規格。
- 額外加了「找不到任何已安裝的 AI 軟體」時的一句空狀態文字（任務檔沒寫）；`LastQueryAt` 為空時說明行顯示「最後查詢：還沒有」，`LastConnectedAt` 為空時顯示「最後連線：不明」。
- 失敗時卡片內的「複製設定內容」是獨立按鈕（不必打開「⋯」選單）；「⋯」選單永遠可用。
- 圖示方塊只用名稱第一個字母（Cursor 與 Claude Desktop 都是 C；mockup 的「Cu」沒照做）。
- 移除確認對話框標題為「從 {名稱} 移除」，不是危險樣式（不會刪除任何資料，Enter 可確認）。
- 時間顯示轉成 `TimeProvider.LocalTimeZone`；相對時間超過一天改顯示昨天／M/d。
- 非 `InvalidOperationException` 的例外（例如沒權限寫入）只顯示「無法加入／修復／移除，請稍後再試一次。」，細節只寫入日誌（不含檔案內容）。

### 待在 macOS／Windows 確認（請彙整到 tests/manual/CHECKLIST.md）

- 在裝有 Claude Desktop 的機器上：按「加入」後設定檔內容正確（Windows `%APPDATA%\Claude\claude_desktop_config.json`、macOS `~/Library/Application Support/Claude/claude_desktop_config.json`，保留原有設定，產生 `.contexo.bak`）；完全關閉並重新開啟 Claude Desktop、問一個問題後，狀態變成「已連線」且「最後查詢」更新。VS Code、Cursor、LM Studio 同樣流程。
- 「⋯」選單在實機能開啟並複製（Headless 已驗證命令綁定，未驗證系統剪貼簿在 Windows 的行為）。
- 注音輸入不涉及本頁（無輸入框）。
- 每 10 秒更新時畫面不閃爍、離開頁面或視窗隱藏到系統匣後不再讀取。

### 給後續任務的注意事項

- `AiClientsViewModel` 不注入 `ShellViewModel`，也沒有導覽相依；若 T19 的設定頁要放「複製 AI 軟體設定內容」，可直接呼叫各 `IAiClientIntegration.BuildManualSnippet(IAiClientStatusService.CurrentLaunch)`。
- 其他並行任務若也把頁面 VM 改成有參數的建構式，需要自行處理 `TestShell` 與 `ShellViewModelTests`；本任務用 `internal` 無參數建構式避開了範圍外檔案的修改，T16、T17、T19、T20 可參考，或由派工者統一在 `TestShell` 補上。
- 測試用的 `TestClock`（可手動推進、會觸發 `PeriodicTimer`）在 `tests/Contexo.App.Tests/AiClients/Fakes.cs`。

