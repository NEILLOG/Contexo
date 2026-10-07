# T21 打包與安裝程式

- **狀態**：待辦
- **波次**：4
- **相依**：T13、T16、T17、T18、T19、T20
- **必讀**：`AGENTS.md`、`plan/05-operations.md`（打包與發布、版本號）、`plan/01-architecture.md`（程序與生命週期）

## 目標

產出一個乾淨的 Windows 電腦可以直接安裝使用的安裝檔：不需要系統管理員權限、不需要預先安裝 .NET 或任何軟體，模型一起帶入。

## 要做

### 1. 發布設定

- `Contexo.Wpf` 與 `Contexo.Mcp`：`win-x64`、`SelfContained=true`、`PublishSingleFile=false`（ONNX Runtime 與 SQLite 的原生 DLL 放在旁邊較穩定）、`PublishReadyToRun=true`、`InvariantGlobalization=false`。兩者發布到**同一個資料夾**（共用執行環境檔案，`Contexo.exe` 與 `Contexo.Mcp.exe` 並列）。以 `tools/publish.ps1` 封裝這些步驟：`dotnet publish` 兩個專案到 `artifacts/publish/`，再把模型複製到 `artifacts/publish/models/`。
- 確認 `AppPaths.ModelsDirectory` 會優先使用安裝資料夾內的 `models`。
- 正式圖示：`assets/contexo.ico`（多尺寸 16～256），套用到兩個 exe、系統匣、安裝程式。圖示設計為簡單幾何圖形（「脈」字或交織線條），以程式或 SVG 產生，不使用他人素材。

### 2. 安裝程式（Inno Setup）

`installer/contexo.iss`：

- 安裝到 `{localappdata}\Programs\Contexo`，`PrivilegesRequired=lowest`（不需要系統管理員）。
- 開始功能表捷徑；可選的桌面捷徑。
- 選項（預設勾選）：「開機時自動啟動 Contexo」→ 寫入 `HKCU\…\Run`（與 T19 相同的值名與格式）。
- 安裝完成後可選「啟動 Contexo」。
- 升級安裝：偵測到 Contexo 正在執行時，先請它結束（`Contexo.exe --exit`，見下方）。
- **解除安裝**：
  1. 執行 `Contexo.exe --uninstall-cleanup`：對每個 AI 軟體呼叫 `IAiClientIntegration.Remove()`，移除開機啟動登錄值。
  2. 詢問是否同時刪除資料（`%LOCALAPPDATA%\Contexo`：資料庫、設定、日誌）。預設**不刪除**。說明文字強調「你的原始文件不會受影響」。
- 安裝程式介面使用繁體中文（Inno Setup 的 `ChineseTraditional.isl`）。
- 版本號取自 MinVer 產生的版本（由 `tools/publish.ps1` 傳給 `iscc /DAppVersion=…`）。

### 3. 命令列參數（`src/Contexo.Wpf/Platform/CommandLine.cs`）

- `--minimized`：只顯示系統匣（T15 已處理，確認行為即可）。
- `--exit`：通知執行中的 Contexo 結束（沿用 T15 的單一執行個體通道，新增「結束」訊息），自身也結束。
- `--uninstall-cleanup`：執行上述清理後結束，不顯示視窗。

需要修改 T15 的單一執行個體與啟動流程以支援這些參數；只做必要的最小修改。

### 4. 版本紀錄自動產生

`tools/generate-changelog.ps1`：讀取 git tag（`v*`）之間的 commit 訊息第一行，產生 `CHANGELOG.md`（格式與 T20 相同：`## 版本 - 日期` 加條列；略過以 `chore`、`ci`、`test` 開頭與合併提交）。發布流程中執行並提交。

### 5. CI 發布流程

`.github/workflows/release.yml`：在推送 `v*` tag 時於 `windows-latest` 執行：

1. checkout（`fetch-depth: 0`）→ setup-dotnet → `dotnet test`。
2. 下載模型（`tools/download-models.ps1`），以 `actions/cache` 快取。
3. `tools/publish.ps1`。
4. 安裝 Inno Setup（`choco install innosetup -y`，若映像已內建則略過）並編譯安裝程式。
5. 程式碼簽章：若 repository secret `SIGNING_CERT_PFX`（base64）與 `SIGNING_CERT_PASSWORD` 存在，以 `signtool` 簽署兩個 exe 與安裝檔；不存在則略過並在日誌提示。
6. 上傳 `Contexo-Setup-{版本}.exe` 為 artifact，並建立 GitHub Release（草稿）。

## 不做

- MSIX、Microsoft Store。
- 自動更新（之後再評估）。

## 可修改範圍

- `tools/publish.ps1`、`tools/generate-changelog.ps1`
- `installer/**`、`assets/**`
- `.github/workflows/release.yml`
- `src/Contexo.Wpf/Platform/CommandLine.cs`，以及為支援命令列所需的 `src/Contexo.Wpf/App.xaml.cs`、`Platform/SingleInstance.cs` 最小修改
- `src/Contexo.Wpf/Contexo.Wpf.csproj`、`src/Contexo.Mcp/Contexo.Mcp.csproj`（發布屬性與圖示）
- `Directory.Build.props`（如需共用發布屬性）
- `CHANGELOG.md`

## 實作要點與已知陷阱

- 兩個 self-contained 專案發布到同一資料夾時，若共用的執行環境檔案版本相同就不會衝突；在 `publish.ps1` 中先發布 Wpf 再發布 Mcp，最後檢查資料夾中沒有重複但版本不同的 DLL（列出差異並使建置失敗）。
- ReadyToRun 需在 Windows 上建置。
- 防毒誤判：不使用任何壓縮殼；簽章後大幅降低誤判。
- 在 macOS / Linux 上無法執行本任務的大部分驗證，請在 Windows 環境進行。

## 驗收條件

1. 在 Windows 執行 `pwsh tools/publish.ps1` 成功，`artifacts/publish/` 內有 `Contexo.exe`、`Contexo.Mcp.exe`、`models/`。
2. `iscc installer/contexo.iss` 產出安裝檔。
3. 在**乾淨的 Windows 10/11 虛擬機**（未安裝 .NET、Office）人工驗證，列入完成紀錄：
   - 一般使用者帳號（非系統管理員）可安裝。
   - 安裝後啟動、完成首次啟動精靈、加入一個含 docx/pptx/xlsx/pdf 的資料夾並完成處理。
   - 「試試看搜尋」有結果。
   - 加入到 Claude Desktop（若可安裝）後能查詢。
   - 重新開機後以系統匣啟動。
   - 解除安裝：AI 軟體設定中的 `contexo` 項目被移除、開機啟動被移除；選擇保留資料時資料夾仍在；原始文件完好。
   - 升級安裝（安裝較新版本）時資料保留。
4. `release.yml` 在測試 tag（例如 `v0.1.0-test.1`）上執行成功並產出安裝檔 artifact。

## 完成紀錄

（由執行者填寫）
