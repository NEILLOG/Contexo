# T16 介面：資料夾頁與首次啟動精靈

- **狀態**：完成
- **波次**：3
- **相依**：T10、T15（T14 有完成更好，精靈第 3 步會用到；未完成時該步驟顯示空清單）
- **必讀**：`AGENTS.md`（10a 節）、`plan/04-ui.md`（資料夾、首次啟動精靈、確認與警語）、`plan/ui-mockup.html` 的「資料夾」與「首次啟動精靈」頁（開啟設計註記）、`src/Contexo.Core/Abstractions/Indexing.cs`、`Storage.cs`

## 目標

主畫面「資料夾」頁與首次啟動精靈。使用者全程只用勾選、拖放、按鈕，不需要輸入路徑。

## 要做

### 1. 資料夾頁（`FoldersViewModel` / `FoldersView`）

由上而下：

1. **標題列**：「AI 可以讀取的資料夾」＋說明＋「＋ 加入資料夾」按鈕（`IFolderPicker`）。視窗也接受把資料夾拖進來。
2. **錯誤橫幅**（有 `Unavailable` 資料夾時）：「發生錯誤：『{名稱}』目前無法存取，已暫停這個資料夾。」＋「匯出問題回報」（`INavigationService.NavigateTo<AboutViewModel>()`）。
3. **大量消失詢問**（收到 `MassDeletionPendingRaised` 時，以 `IDialogService.ConfirmAsync`）：「『{名稱}』裡有 {N} 個檔案不見了」，說明「如果你確實刪除或搬走了這些檔案，按『移除這些資料』；如果只是暫時無法存取（例如外接硬碟沒插），按『保留』。你的原始檔案不受影響。」→ `ResolveMassDeletionAsync`。
4. **整體進度卡**（處理中才顯示）：進度條、「1,284 / 3,420 個檔案 · 預估還要約 42 分鐘」、「目前：{檔名}」、暫停／繼續按鈕、「已處理完的檔案現在就能查詢。最近修改的檔案會優先處理。」
5. **重複提示**（加入資料夾時觸發，可關閉）：
   - 新資料夾位在既有資料夾之內 →「『{新}』已經包含在『{既有}』裡，不需要重複加入。」不加入。
   - 新資料夾包含既有資料夾 → 確認對話：「『{新}』包含了已加入的『{既有…}』，要合併成一個嗎？」確認後先移除子資料夾再加入新資料夾。
6. **摘要兼篩選**：「全部 15」「有問題 1」「處理中 2」「已完成 12」切換鈕；資料夾超過 8 個時顯示搜尋框（比對名稱與路徑）。
7. **資料夾清單**：一行一個——資料夾圖示、名稱、路徑（過長以中間省略）、檔案數（處理中顯示「612 / 980」）、狀態標籤、「⋯」選單。
   - 分組與排序：有問題（`Unavailable`、`AwaitingDeletionConfirmation`）→ 處理中（有待處理檔案）→ 已完成；組內依名稱。「已完成」群組在「全部」篩選下預設收合為一行「已完成 · 12 個資料夾 [展開]」。
   - 最近有變動的資料夾，在已完成標籤位置顯示「剛更新 3 個檔案」（依 `RecentActivity`，5 分鐘內）。
   - 點一列 → 開啟「選擇子資料夾」對話框。
   - 「⋯」選單：選擇子資料夾…、在檔案總管中開啟、立即重新掃描、移除…。
8. **無法讀取的檔案**卡：`GetFailedDocumentsAsync`，標題「有 N 個檔案無法讀取」＋「全部重試」；最多顯示 5 筆（檔名、資料夾 › 子路徑、白話原因標籤、單筆動作），其餘「查看全部 N 個 ›」開啟對話框列出全部。單筆動作：可重試的（`Locked`、`Timeout`、`Unknown`、`AccessDenied`）顯示「重試」；其他（`PasswordProtected`、`Corrupted`、`TooLarge`、`Unsupported`）顯示「略過」，按下 = 加入排除清單（`AddExclusionAsync`），提示可在設定中恢復。
9. 清單與失敗清單項目的右鍵選單：開啟、在檔案總管中顯示、「不要讓 AI 讀這個檔案／資料夾」（確認後 `AddExclusionAsync`）。

### 2. 對話框

- **選擇子資料夾**（`SubfolderPickerViewModel`）：標題「選擇子資料夾 · {名稱}」、說明「取消勾選的資料夾，AI 不會讀取裡面的檔案。」、樹狀勾選清單（展開時才載入下一層，略過內建排除與隱藏資料夾）、取消／儲存。勾選語意：取消勾選某資料夾即排除其整個子樹；子層繼承父層狀態。儲存 → `SetFolderExclusionsAsync`（只存最上層被排除的路徑，`/` 分隔、相對路徑）＋`RequestRescan(folderId)`。
- **移除資料夾**：`ConfirmAsync`，標題「移除資料夾」，條列「移除後，AI 將查不到這個資料夾的內容。」「你的原始檔案不會被刪除，仍然留在原本的位置。」「之後重新加入，需要重新讀取這個資料夾。」，危險按鈕「移除資料夾」→ `RemoveFolderAsync`。
- **全部無法讀取的檔案**：清單＋同樣的單筆動作。

### 3. 首次啟動精靈（`FirstRunViewModel` / `FirstRunView`）

`AppSettings.FirstRunCompleted == false` 時由外殼顯示。步驟指示「1 選資料夾 → 2 檔案類型 → 3 加入 AI 軟體」。

1. **選資料夾**：標題「要讓 AI 讀哪些資料夾？」、說明「之後隨時可以在『資料夾』頁面調整。」。列出存在的常見位置：文件、桌面、OneDrive（環境變數 `OneDrive`、`OneDriveCommercial`，兩者都列）、下載；預設勾選文件與 OneDrive，**下載不勾**。每項背景計算檔案數（只算支援的副檔名，上限 10,000，超過顯示「超過 10,000 個」，3 秒逾時顯示「計算中」後繼續）。「把資料夾拖到這裡，或『選擇其他資料夾…』」。底部「預估第一次建立約需 X，期間可以照常使用電腦。」（每個檔案以 1 秒粗估，顯示為「約 10 分鐘」「約 1～2 小時」這類區間）。
2. **檔案類型**：同設定頁的類別勾選（文件、簡報、試算表、PDF；圖片標示「處理時間較長」且停用，註明「之後的版本提供」）。
3. **加入 AI 軟體**：列出 `IAiClientStatusService` 中已安裝的軟體，每個一個「加入」按鈕（`AddOrRepair`），加入後顯示「請完全關閉 {名稱} 後重新開啟」。沒有偵測到任何軟體時：「目前沒有偵測到支援的 AI 軟體，之後可以在『AI 軟體』頁面加入。」可略過。
4. 完成：加入資料夾 → 儲存設定（類別、`FirstRunCompleted = true`）→ `RequestRescan(null)` → 切到資料夾頁。

## 不做

- 設定頁（T19）、AI 軟體頁（T18）。

## 可修改範圍

- `src/Contexo.App/Folders/**`
- `src/Contexo.Desktop/Views/Folders/**`
- `tests/Contexo.Desktop.Tests/Folders/**`
- `tests/Contexo.App.Tests/Folders/**`

## 實作要點與已知陷阱

- 快照事件來自背景執行緒，透過 `IUiDispatcher` 更新，並沿用 T15 的節流做法。
- 路徑包含判斷要以目錄邊界比較，不分大小寫（`C:\A\報價` 不包含 `C:\A\報價單`）。
- 清單可能有數十個資料夾，用虛擬化的 `ItemsControl`／`ListBox`。
- 所有使用者看得到的文字以 `plan/ui-mockup.html` 為準。
- 檔案數計算與子資料夾樹載入都在背景執行，可取消（對話框關閉時）。

## 驗收條件

1. `dotnet test --filter FullyQualifiedName~Contexo.App.Tests.Folders` 通過，以假的服務測試至少：
   - 分組、排序、篩選、搜尋框出現門檻（> 8）、已完成群組收合。
   - 加入資料夾的三種情況（一般、已被包含、包含既有）。
   - 子資料夾勾選 → 儲存的排除清單正確（只存最上層、相對路徑、`/` 分隔）。
   - 移除確認：取消時不呼叫 `RemoveFolderAsync`。
   - 大量消失詢問兩種回答。
   - 失敗清單只顯示 5 筆、單筆動作依錯誤碼正確、略過會加入排除清單。
   - 精靈：下載預設不勾、完成時呼叫順序正確、`FirstRunCompleted` 被設定。
   - 預估時間文字的區間。
2. 編譯成功（任何 OS）。
3. Headless 測試：資料夾頁（含 15 個資料夾的假資料）、子資料夾視窗、移除確認、精靈三個步驟，在淺色與深色下輸出截圖並確認可建立無例外。
4. 在 macOS 實際操作確認（`dotnet run --project src/Contexo.Desktop`，列入完成紀錄）：與 `plan/ui-mockup.html` 對照版面；拖放資料夾（Avalonia `DragDrop`，`DataFormats.Files`）；子資料夾視窗；淺色／深色、三段字級下顯示正常。

## 完成紀錄

**分支**：`task/T16-ui-folders`

### 做了什麼

`Contexo.App/Folders/`（不引用 Avalonia）：

- `FoldersViewModel`：標題列、錯誤橫幅、提示條（重複加入、略過、移除後的說明，可關閉）、整體進度卡、摘要兼篩選（全部／有問題／處理中／已完成，附數量）、搜尋框（資料夾超過 8 個才顯示）、分組清單（有問題 → 處理中 → 已完成，組內依名稱；已完成在「全部」篩選下收合成一行）、「剛更新 N 個檔案」（5 分鐘）、無法讀取的檔案卡（最多 5 筆）。實作 `IPageLifecycle`；快照事件用與狀態列相同的 250ms 節流，經 `IUiDispatcher` 更新。加入資料夾與子資料夾儲存後呼叫 `RequestRescan(folderId)`；收到 `MassDeletionPendingRaised` 以 `IDialogService.ConfirmAsync` 詢問並一定呼叫 `ResolveMassDeletionAsync`（「保留」與 Esc 都是 false）。
- `FolderRowViewModel`、`FailedFileRowViewModel`、`FolderGroupHeader`、`FolderFilterOption`：清單列與命令（選擇子資料夾、在檔案總管開啟／顯示、立即重新掃描、移除、不要讓 AI 讀）。
- `SubfolderPickerViewModel` + `SubfolderNodeViewModel`：樹狀勾選，展開時才在背景讀下一層；取消勾選＝排除整個子樹；儲存只存最上層被排除的相對路徑（`/` 分隔）；使用者沒展開過的區域裡原有的排除路徑會保留；沒有變動時不寫入也不重掃。
- `FailedFilesDialogViewModel`：「查看全部」對話框，與頁面共用同一份清單。
- `FirstRunViewModel`（精靈三步驟）＋ `WizardLocation／Category／Client／StepViewModel`：常見位置（文件、桌面、OneDrive 與 OneDriveCommercial、下載）背景計算檔案數（上限 10,000）；預設勾文件與 OneDrive、不勾下載；一個勾選的資料夾若位在另一個勾選資料夾內，完成時只加入外層；預估時間；步驟 3 呼叫 `AddOrRepair`；完成順序為加入資料夾 → `SaveAsync`（類別、`FirstRunCompleted = true`）→ `RequestRescan(null)`，任何資料夾加入失敗則留在精靈並顯示訊息。
- `PathRelations`（以目錄邊界、不分大小寫、`\` 與 `/` 通用的路徑比較與中間省略）、`TimeText`（預估時間區間、剩餘時間）、`FolderServices`（`IKnownFolders`、`IFolderFileCounter`、`IFolderTreeReader` 與預設的檔案系統實作）。

`Contexo.Desktop/Views/Folders/`：`FoldersView`、`FolderRowView`、`FailedFileRowView`（後兩者由 ViewLocator 依命名慣例自動對應）、`SubfolderPickerView`、`FailedFilesDialogView`、`FirstRunView`、`FolderDropHandler`（拖放資料夾：只在資料夾頁或精靈第 1 步顯示期間，讓整個視窗接受 `DragDrop`，離開時還原）。

測試：`tests/Contexo.App.Tests/Folders/`（101 個，含 Theory 展開）、`tests/Contexo.Desktop.Tests/Folders/`（19 個）。

### 驗收條件結果

1. `dotnet test --filter FullyQualifiedName~Contexo.App.Tests.Folders`：通過。涵蓋分組／排序／篩選／搜尋框門檻（8 個沒有、9 個有）／已完成收合與展開／搜尋時不收合；加入資料夾三種情況（一般、已被包含、包含既有並合併；另有「報價」與「報價單」不算包含、同一個資料夾重複加入、資料夾不存在、拖放）；子資料夾勾選與儲存內容（最上層、相對路徑、`/`、未展開區域保留、無變動不寫入）；移除確認取消時不呼叫 `RemoveFolderAsync`；大量消失兩種回答；失敗清單只顯示 5 筆、八種錯誤碼的單筆動作、略過加入排除清單並提示可在設定恢復、重試與全部重試；精靈下載預設不勾、完成呼叫順序（AddFolder → SaveSettings → RequestRescan(null)）與 `FirstRunCompleted`；預估時間區間（「約 10 分鐘」「約 1～2 小時」等 14 組）。
2. `dotnet build Contexo.slnx -warnaserror`：0 警告、0 錯誤（macOS；Windows、Linux 由 CI 確認）。
3. Headless 測試（`tests/Contexo.Desktop.Tests/Folders/FoldersViewTests.cs`）：資料夾頁（15 個資料夾、7 個無法讀取的檔案、進度、錯誤橫幅）在 淺色／標準、深色／標準、淺色／特大、深色／大 四種組合下建立、檢查畫面元素並輸出截圖（收合與展開各一張）；空白頁；提示條；子資料夾視窗（淺色、深色，含展開載入下一層、儲存與 Esc 不儲存）；移除確認（淺色、深色）；大量消失詢問；全部無法讀取的檔案視窗；精靈三步驟（淺色、深色、淺色／特大）；完成精靈後外殼切到資料夾頁；視窗只在資料夾頁顯示期間接受拖放。截圖在 `artifacts/screenshots/`（`folders-*.png`、`wizard-*.png`，不提交），已逐張檢視版面。Headless 測試用產品本身的 DI 容器（`AddContexoDesktop` ＋ `AddContexoCore`，暫存資料夾、真的 SQLite 與真的資料夾），只把索引服務、AI 軟體狀態與精靈的常見位置換成假的，順便驗證了新建構式能由 DI 解析。
4. macOS 實際操作：**無法完成**。`dotnet run --project src/Contexo.Desktop`（`CONTEXO_DATA_DIR` 指到暫存資料夾）在本代理環境直接失敗：`Avalonia.Native was not able to start the RenderTimer. Native error code: -6661`（沒有視窗伺服器）。因此「對照 mockup 的版面、拖放資料夾、三段字級、系統深淺色」只能以 Headless 截圖替代，**需要人在 Mac 上實際確認**，特別是拖放（`FolderDropHandler` 只有程式碼與「AllowDrop 開關」的自動測試，沒有真的拖過）。

全方案 `dotnet test`：Core 909（14 略過）、App 126、Mcp 5 通過；Desktop 57 個中 1 個失敗：`SingleInstanceTests.Can_be_woken_more_than_once`（具名管道喚醒逾時 5 秒），單獨重跑結果相同，與 T10 完成紀錄記載的是同一個既有問題，與本任務無關，未處理。

### 與規格不同或規格未寫處的決定

- **共用外殼的測試仍用無參數建構式**：`ShellViewModelTests`、`TestShell`、`ViewLocatorTests`（皆在範圍外）用 `new FoldersViewModel()` 與 `new FirstRunViewModel()`。為了不改範圍外的檔案，兩個類別各保留一個 `internal` 無參數建構式（建出什麼都不做的「空殼」實例，DI 只看 public 建構式所以不受影響）。T17～T20 若遇到同樣問題可用同樣做法；之後若統一改為假服務，可刪掉。
- 新服務（`IKnownFolders`、`IFolderFileCounter`、`IFolderTreeReader`）沒有註冊進 `AddContexoDesktop`（範圍外），而是建構式的選用參數，沒註冊時用預設的檔案系統實作；`ILogger<T>` 也是選用。測試可直接用 DI 註冊覆寫。
- 清單用一般 `ItemsControl`＋`StackPanel`，沒有虛擬化：整個頁面（橫幅、進度、清單、失敗檔案）共用一個 `ScrollViewer`，清單放進去虛擬化會失效；數十個資料夾的量沒有問題。
- 「3 秒逾時顯示計算中」：檔案數沒算完前一律顯示「計算中…」，算完才換成數字，沒有另外的 3 秒計時。算失敗的位置不顯示數字。
- 「剛更新 N 個檔案」：`RecentActivity` 沒有資料夾欄位，所以改為：觀察到某資料夾的待處理檔案從有變成 0 時，記下當時的最大待處理數與時間，再配合 `RecentActivity` 在 5 分鐘內有「更新／新增」才顯示。首次建立完成後也會短暫顯示「剛更新 N 個檔案」。
- 子資料夾視窗與「全部無法讀取的檔案」視窗受主視窗對話框卡片的 `MaxWidth=440` 限制（`MainWindow.axaml` 在範圍外），所以較窄；清單另有捲動。
- 資料夾根目錄的右鍵「不要讓 AI 讀這個資料夾」依規格會 `AddExclusionAsync(path, true)`，該資料夾仍留在清單中（資料被清空）。若覺得該改成移除資料夾，請人決定。
- 移除資料夾對話框除了規格的三條說明，第一行多了「要移除「{名稱}」嗎？」，避免誤按。
- 拖放：只在資料夾頁與精靈第 1 步顯示期間讓整個視窗接受拖放（`TopLevel` 加 `AllowDrop`），沒有修改 `MainWindow`。拖進來的檔案會被忽略，只處理資料夾。
- 精靈儲存類別時，若設定中原本有 `Email`（目前沒有副檔名）會保留。
- 失敗檔案卡只列 `Status = Failed`（`GetFailedDocumentsAsync` 的行為）；`Skipped`（過大、不支援）不會出現在這張卡，但「略過」按鈕對應的錯誤碼 `TooLarge`、`Unsupported` 仍依規格處理。

### 待在 Windows（及 Mac）確認

- 拖放資料夾（Windows 檔案總管、macOS Finder）到資料夾頁與精靈第 1 步。
- 實際操作對照 `plan/ui-mockup.html`；三段字級、淺色／深色切換即時生效；中文輸入（搜尋框）。
- OneDrive／OneDriveCommercial 環境變數在 Windows 的實際內容；「⋯」選單與右鍵選單的位置；`MenuFlyout` 在 125% 字級下是否被視窗邊緣裁切。
- 隱藏、系統屬性與連結資料夾在子資料夾樹與檔案數計算中被略過（`FileSystemFolderTreeReader`／`FileSystemFolderFileCounter` 只在 macOS 用一般資料夾測過，Windows 的屬性行為需在 Windows 看）。

### 給後續任務的注意事項

- `PathRelations`（`Contexo.App.Folders`，public）可供 T17～T20 比較路徑；`TimeText.Remaining` 可顯示剩餘時間。
- 測試裡想取得 `FoldersViewModel` 的所有列（含收合的已完成群組）用 `internal Rows`；`PendingRefresh` 可等待最近一次重新讀取。
- `tests/Contexo.Desktop.Tests/Folders/FoldersHarness.cs` 示範了用產品 DI 容器加暫存資料夾組出真的 `ShellViewModel`／`MainWindow`，不必再手動 `new` 各個頁面 VM（頁面 VM 建構式改變時不會壞）。
- 大量消失詢問由 `FoldersViewModel` 負責（單例，外殼建立時就訂閱事件），其他頁面不需要處理。
- T19（設定頁）需要提供「恢復」被排除的檔案與資料夾（本頁的提示文字都寫「可以在『設定』中恢復」）。
- T20：資料夾頁的「匯出問題回報」是 `INavigationService.NavigateTo<AboutViewModel>()`。
