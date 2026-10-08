# tools/

開發用腳本。腳本本身由各任務新增，這裡說明各自的用途。腳本不屬於正式產品，不會隨安裝程式發布。

| 腳本 | 新增任務 | 用途 |
|---|---|---|
| `download-models.ps1` / `download-models.sh` | T03 | 從 huggingface.co 下載 embedding 模型（bge-small-zh-v1.5 int8）到 `models/`。`models/` 不進版控 |
| `Contexo.CorpusGen/` | T22 | 產生端對端測試用的文件語料（全部由程式產生，不含真實公司文件）與檢索品質評估 |

## 慣例

- 每個腳本提供 PowerShell 與 bash 兩種版本（或用 .NET 寫成可跨平台的小程式）。
- 需要網路的腳本在無法連線時要清楚說明原因並以非 0 結束，不要靜默失敗。
- 腳本不得修改使用者資料夾或 `%LOCALAPPDATA%\Contexo`。
