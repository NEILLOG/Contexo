# T18 介面：AI 軟體頁

- **狀態**：待辦
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
   - 每張卡片的「⋯」選單：「複製設定內容」（`BuildManualSnippet` → `IClipboardService`，成功顯示「已複製」）。
6. 頁面顯示時每 10 秒重新整理狀態，離開頁面停止；按鈕操作後立即重新整理。

## 不做

- 設定檔的讀寫邏輯（T14 已完成）。

## 可修改範圍

- `src/Contexo.App/AiClients/**`
- `src/Contexo.Wpf/Views/AiClients/**`
- `tests/Contexo.App.Tests/AiClients/**`

## 實作要點與已知陷阱

- 重新整理用可取消的計時迴圈（`PeriodicTimer`），不要用 `DispatcherTimer`（VM 不能依賴 WPF）。頁面可見性用 T15 的 `IPageLifecycle`（`OnNavigatedTo` 開始、`OnNavigatedFrom` 停止）。
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
3. Windows 人工確認：實際按「加入到 Claude Desktop」後，設定檔內容正確；重新開啟 Claude Desktop 並問一個問題後，狀態變成「已連線」且最後查詢時間更新。

## 完成紀錄

（由執行者填寫）
