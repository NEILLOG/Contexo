# 發布前手動驗收清單

彙整 T00 的 POC、T03～T20 完成紀錄中「無法在開發環境驗證」的項目，以及 T22 規劃的實際情境。自動化測試（含 `Category=EndToEnd`）涵蓋不到這些：它們需要真實的視窗、Windows、真實軟體或真實的使用情境。

勾選時請在備註寫下日期、機器與結果；失敗的項目另開問題，不要直接在這裡改程式。

## 0. 開始之前

- **一律使用獨立的資料夾**，不要動到真正的資料：`CONTEXO_DATA_DIR` 指到暫存資料夾（Windows：`set CONTEXO_DATA_DIR=C:\temp\contexo-test`；macOS：`export CONTEXO_DATA_DIR=/tmp/contexo-test`）。
- 測試「AI 軟體」頁之前，先**備份**真實的 AI 軟體設定檔（Claude Desktop 的 `claude_desktop_config.json` 等）。Contexo 只會動 `contexo` 這一筆並產生 `.contexo.bak`，但備份是最便宜的保險。
- 產生測試資料夾：`dotnet run --project tools/Contexo.CorpusGen -- generate <資料夾>`，把 `<資料夾>/corpus` 加進 Contexo 就有 40 個虛構公司的檔案，`<資料夾>/queries.json` 是對照用的問題與預期答案。**不要用真實公司文件測試**，除非是你自己決定的去識別化樣本。
- 自動化端對端測試：`dotnet test --filter Category=EndToEnd`（有 `models/` 時含檢索品質門檻；沒有則只記錄數字）。品質報告：`dotnet run --project tools/Contexo.CorpusGen -- eval --models models`。
- 用真實軟體另存的中文樣本（macOS 開發機）：`bash tools/Contexo.CorpusGen/real-samples/make-real-samples.sh <資料夾>`，再用 `dotnet run --project tools/Contexo.CorpusGen -- parse <檔案>` 看解析結果。

## 1. 中文輸入（T00 POC、T15）

測試程式：`poc/ime-avalonia`（`dotnet run`），以及正式程式的搜尋框與各輸入框。T00 當初由使用者判定通過，**沒有逐項填寫結果**，所以下列全部都要補做。

| 環境 | 說明 |
|---|---|
| W1 | Windows 11，微軟注音（新版，預設） |
| W2 | Windows 10 或 11，微軟注音「使用舊版」 |
| M1 | macOS，系統內建「注音」 |

每一項在搜尋框、多行輸入框、清單內輸入框、對話框都做一次。

- [ ] #1 輸入「報價單」並選字：文字正確出現一次，無重複、無殘留注音符號（W1 / W2 / M1）
- [ ] #2 組字中的注音顯示在游標位置（行內）
- [ ] #3 按空白鍵叫出選字清單：出現在游標附近，不跑到螢幕左上角
- [ ] #4 搜尋框選字時按 Enter：只確定文字，**不會**送出搜尋；選完再按 Enter 才送出
- [ ] #5 組字中按 Backspace 刪除的是注音，不是已輸入的字
- [ ] #6 組字中按 Esc 取消組字，已輸入的文字不變
- [ ] #7 全形標點「，。、？「」」正確
- [ ] #8 中英切換（Windows：Shift；Mac：Caps Lock 或 Control+空白）
- [ ] #9 連續快速輸入「今天下午三點在第二會議室開採購驗收會議」不掉字、不亂序
- [ ] #10 字級切到「特大」後重做 #1～#3，選字視窗位置仍正確
- [ ] #11 淺色／深色主題下組字文字與底線都看得清楚
- [ ] #12 字型：沒有方框、繁體字形正確（「骨」「直」）、罕用字有顯示；Windows 字型為 `Microsoft JhengHei UI`
- [ ] #13 從記事本／備忘錄複製中文貼上
- [ ] #14 滑鼠點選輸入框中間後輸入，文字插在游標處

## 2. Windows 專屬項目

### 2.1 桌面外殼（T15）

- [ ] 系統匣圖示出現在右下角；左鍵開啟主視窗；右鍵選單「開啟 Contexo／暫停處理（繼續處理）／結束」
- [ ] 第一次關閉視窗顯示「Contexo 會在背景繼續執行…」的提示，之後不再提示；按「結束」才真正離開
- [ ] 系統深淺色切換時（設定為「跟隨系統」）即時跟著變；三段字級版面不破
- [ ] 第二次啟動只會叫出既有視窗，不會開第二個
- [ ] 閒置時間偵測（`GetLastInputInfo`）：使用者操作電腦時索引降速（「只在電腦閒置時全速」開啟），閒置兩分鐘後全速

### 2.2 資料夾頁與精靈（T16）

- [ ] 從檔案總管**拖放**資料夾到資料夾頁與精靈第 1 步
- [ ] OneDrive／OneDriveCommercial 環境變數的實際內容（精靈的常見位置）
- [ ] 「⋯」選單與右鍵選單位置正確，125% 字級下不被視窗邊緣裁切
- [ ] 隱藏、系統屬性的資料夾，以及連結資料夾，在子資料夾樹與檔案數計算中被略過
- [ ] 資料夾根目錄的「不要讓 AI 讀這個資料夾」：目前是清空資料但資料夾仍留在清單（見 `task/README.md` 待決定 A-2）

### 2.3 搜尋頁（T17、T11）

- [ ] 「開啟原檔」與「在檔案總管顯示」
- [ ] 路徑比對：`C:\A\報價` 與 `C:/A/報價` 視為相同、`報價` 不會選到 `報價單`

### 2.4 AI 軟體頁（T14、T18）

- [ ] `%APPDATA%`、`%LOCALAPPDATA%` 的實際路徑與各軟體安裝位置判斷（Claude Desktop 若為 MSIX 安裝，設定檔資料夾判斷是否仍正確）
- [ ] VS Code（`%APPDATA%\Code\User\mcp.json`）與 LM Studio（`%USERPROFILE%\.lmstudio\mcp.json`）的設定檔位置：來自第三方資料，非官方
- [ ] 按「加入」後設定檔正確、保留原有設定、產生 `.contexo.bak`；完全關閉並重開該軟體、問一個問題後，狀態變「已連線」且「最後查詢」更新
- [ ] 「⋯」選單的複製在 Windows 剪貼簿正常
- [ ] **各軟體實際回報的 `clientInfo.name`**（見第 4 節的紀錄表）

### 2.5 設定頁與開機啟動（T19）

- [ ] 開啟「開機時自動啟動」後，`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 出現值 `Contexo`，內容為 `"…\Contexo.exe" --minimized`；關閉後該值被刪除，其他程式的值不受影響
- [ ] 工作管理員「啟動」分頁看得到 Contexo
- [ ] 重新開機後以系統匣啟動（不彈出主視窗）
- [ ] 程式搬移位置後，下次開啟設定頁自動修正登錄值
- [ ] 公司電腦以群組原則禁止寫入 Run 機碼：開關彈回並出現「無法設定開機自動啟動…」
- [ ] 設定頁外觀與 macOS 截圖一致；「開啟」按鈕能開啟 `%LOCALAPPDATA%\Contexo` 與 `logs`
- [ ] 「清除全部資料」的真實流程

### 2.6 問題回報（T20）

- [ ] 顯示卡名稱（讀登錄檔）出現在 `system.json`
- [ ] 檔案總管選取匯出的 zip；日誌正被寫入時仍可複製
- [ ] `C:\Users\<名稱>` 在 zip 內被遮罩；打開 zip 檢查沒有你的使用者名稱與完整路徑
- [ ] 啟動錯誤畫面的匯出按鈕

### 2.7 檔案系統行為（T10）

- [ ] 被別的程式鎖住的檔案（共用違規 `0x80070020`／`0x80070021`）：保留舊資料，5 分鐘後自動重試
- [ ] 隱藏屬性的檔案與資料夾不會被讀取
- [ ] OneDrive 雲端佔位檔（僅線上、未下載）：不會觸發下載，也不會被當成檔案消失
- [ ] `FileSystemWatcher` 在 Windows 的實際行為：新增、修改、刪除、改名都能在數秒內反映

### 2.8 PDF 與 MCP（T07、T13）

- [ ] **真實中文 PDF**（Word 另存、Office 列印成 PDF、掃描器產生的 PDF）：段落切分、閱讀順序（含雙欄）、「第 N 頁」頁尾移除、PDF 內嵌附件、JPEG 掃描檔（第一版應顯示「掃描檔，讀不到」）
- [ ] 注意字元：PDF 文字層若含「部首」字元（例如 `⼀` `⼯` 而不是 `一` `工`），搜尋會找不到（見 T22 完成紀錄的缺陷 1）。請用你們環境實際產生的 PDF 確認是否發生
- [ ] `Contexo.Mcp.exe` 在路徑含空白或中文時能啟動；Windows 路徑在搜尋結果裡的樣子可讀
- [ ] `tools/download-models.ps1` 在 Windows 實跑一次（開發機沒有 pwsh，只驗證過 `.sh`）

## 3. macOS 項目（開發驗證，可在目前這台機器做）

子代理環境沒有螢幕擷取與輔助使用權限，以下只有 Headless 截圖與自動測試，沒有人實際看過。

- [ ] 視窗外觀對照 `plan/ui-mockup.html`；明暗主題跟隨系統；三段字級即時切換（T15）
- [ ] 選單列圖示與選單、第一次關閉視窗的提示（T15）
- [ ] 資料夾頁：拖放、子資料夾視窗、移除確認、首次啟動精靈三步驟（T16）
- [ ] 搜尋頁：注音選字按 Enter 不送出；「開啟原檔」與「在 Finder 中顯示」（T17）
- [ ] AI 軟體頁：按「加入」→ 重開 Claude Desktop → 提問 → 「已連線」（**會改到真實設定檔**，先備份）（T18）
- [ ] 設定頁：主題與字級即時切換；開機啟動寫入 `~/Library/LaunchAgents/tw.contexo.desktop.plist`，登出再登入後以選單列圖示啟動；「清除全部資料」（T19）
- [ ] 關於頁：「匯出問題回報…」、資料夾選擇器、「開啟所在資料夾」；打開 zip 檢查內容（T20）
- [ ] 淺色主題下 `ToggleSwitch`、`CheckBox` 的強調色應為 `Brush.Accent`，目前顯示 Fluent 預設藍（`task/README.md` 待決定 A-6）

## 4. 發布前情境（T22）

### 4.1 檔案正在 Excel 中開啟時被修改

1. 把 `銷售明細.xlsx`（語料中的 3,000 列大表）放進已加入的資料夾，等 Contexo 讀完。
2. 用 Excel 開啟該檔，**不要關閉**，修改一個金額並儲存。
3. 預期：檔案被 Excel 鎖住或寫入中時，Contexo 保留舊資料並在數分鐘內自動重試；關閉 Excel 後，新數字出現在搜尋與 `query_table` 的結果。不應出現「讀取失敗」的永久紅字，舊資料不應消失。
- [ ] 通過　備註：

### 4.2 OneDrive 登出再登入

1. 加入 OneDrive 資料夾，等讀完。
2. 登出 OneDrive（或暫停同步並讓資料夾無法讀取）。
3. 預期：資料夾顯示「暫時讀不到」，**已讀取的資料全部保留**，搜尋仍可用；不會問你是否刪除。
4. 重新登入後，資料夾自動恢復，**不重建**（沒有重新讀取全部檔案）。
- [ ] 通過　備註：

### 4.3 外接硬碟拔除後重新插入

1. 加入外接硬碟上的資料夾，等讀完。
2. 直接拔除硬碟。預期：資料夾狀態為「暫時讀不到」，資料保留、不詢問刪除、搜尋仍能回答（開啟原檔會失敗是正常的）。
3. 重新插入。預期：約一分鐘內自動恢復為正常，檔案數與片段數不變，沒有整批重讀。
- 自動化測試 `FolderSyncEndToEndTests.A_folder_that_cannot_be_read_keeps_its_data_and_recovers_when_it_returns` 在 macOS／Linux 以「整個資料夾搬走再搬回」模擬；Windows 不允許改名被監看的資料夾，所以**這一項只能在 Windows 手動確認**。
- [ ] 通過　備註：

### 4.4 一次刪除大量檔案（出現詢問）

1. 資料夾內至少 30 個檔案，等讀完。
2. 在檔案總管一次刪除超過三成且至少 20 個檔案。
3. 預期：Contexo 顯示確認視窗，說明有多少檔案不見了；回答「保留」→ 資料全留、資料夾回到正常；回答「刪除」→ 只刪掉 Contexo 資料庫裡那些檔案的資料。**任何情況下都不會動到磁碟上的檔案。**
- 自動化測試 `MassDeletionKeepEndToEndTests`／`MassDeletionConfirmedEndToEndTests` 驗證了服務層；視窗與按鈕要手動確認。
- [ ] 通過　備註：

### 4.5 Claude Desktop 實際查詢，記錄 `clientInfo.name`

`KnownClientNames`（T14）目前是猜測值，必須用真實軟體確認。

1. 在「AI 軟體」頁加入 Claude Desktop，完全關閉再重開 Claude Desktop。
2. 問一個需要查文件的問題（例如：「採購驗收的標準是什麼？」），確認 Claude 有呼叫 `search`；再問一個表格問題（例如：「去年銷售明細的客戶合計金額最高的是誰？」），確認依序呼叫 `search`、`describe_table`、`query_table`。
3. 確認 Contexo「AI 軟體」頁狀態變「已連線」、「最後查詢」更新。
4. 查看 `{DataDirectory}\logs\mcp-*.log` 或資料庫 `mcp_activity` 資料表，記錄實際的 `clientInfo.name` 與版本：

| 軟體 | 版本 | 實際 `clientInfo.name` | 與 `KnownClientNames` 一致？ | 狀態是否變「已連線」 |
|---|---|---|---|---|
| Claude Desktop（Windows） | | | | |
| Claude Desktop（macOS） | | | | |
| VS Code | | | | |
| Cursor | | | | |
| LM Studio | | | | |

- [ ] 工具清單出現 `search`、`describe_table`、`query_table`，描述讀得懂
- [ ] MCP 協定版本：SDK 2.2.0 預設協商沒有握手的新版協定，確認各軟體「已連線」記錄都能觸發
- [ ] 第一次搜尋的延遲（載入模型與向量）可接受；十萬筆向量約 200 MB 記憶體是否可接受（T22 的測試只有約 250 個片段，延遲約 90 毫秒，未量測大量資料）

### 4.6 真實文件樣本

自動化測試的所有 Word、PowerPoint、Excel 檔都是用 OpenXml SDK 組出來的，沒有用 Office 實際另存過。請用公司 Windows 電腦上的 Office 另存一批（去識別化）樣本，加進測試資料夾，檢查：

- [ ] Word：多層標題、表格、修訂追蹤、註腳、內嵌 Excel
- [ ] PowerPoint：流程圖連接線方向、SmartArt 組織圖、圖表、備忘稿、內嵌 Word
- [ ] Excel：表單型、大表（標題列與範圍偵測）、一頁兩表、兩層表頭、公式快取值、日期與民國年格式
- [ ] 儲存格顯示成 `1,285,000` 的金額，用 `1,285,000` 搜尋能找到（解析器存成 `1285000`；T22 的觀察題 o02 在關鍵字模式排第 1、語意模式排第 3～6，用真實 Excel 檔再確認）
- [ ] 資料夾中放 30～50 份真實檔案後，用「試試看搜尋」對照預期答案，記錄 Recall

## 5. T21 待辦（需 Windows）

T21（打包與安裝程式）尚未進行，維運者決定保留。以下項目**尚未驗證**，打包完成後需在乾淨的 Windows 電腦（沒有安裝 .NET、Office、Python 的虛擬機最理想）逐項確認：

- [ ] 打包：`dotnet publish` self-contained 產出 Contexo（桌面程式）與 `Contexo.Mcp.exe`，`Contexo.Mcp.runtimeconfig.json` 與相依 DLL 都在同一資料夾；模型資料夾（`models\bge-small-zh-v1.5\` 與 `models\default`）一併放入安裝目錄
- [ ] 安裝：乾淨機器直接執行安裝程式，不需要另外安裝任何東西；建立捷徑；可選擇開機自動啟動；可選擇預先加入 AI 軟體
- [ ] 安裝後第一次啟動：首次啟動精靈出現，embedding 模型被找到（搜尋不是「只用關鍵字」）
- [ ] 解除安裝：移除程式與捷徑、移除 Run 登錄值；詢問是否保留資料；AI 軟體設定檔中的 `contexo` 項目被移除（格式損壞時 `Remove` 會丟例外，安裝程式要自己處理）；**不會刪除使用者的任何原始檔案**
- [ ] 升級安裝：覆蓋安裝新版本，資料庫自動升級（保留既有資料夾與資料）、AI 軟體設定檔中的路徑自動修復、開機啟動的登錄值路徑更新
- [ ] 簽章與 SmartScreen 警告（若有簽章）
- [ ] CI 產出：安裝檔由 CI 建置並上傳
