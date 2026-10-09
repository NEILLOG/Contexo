# Contexo 第一版工作細項

範圍依 [`plan/README.md`](../plan/README.md) 的「第一版範圍」。第二階段項目不在這裡。

開始任何任務前，代理必須先讀 [`AGENTS.md`](../AGENTS.md)。

## 任務總覽

| 編號 | 任務 | 波次 | 相依 | 主要產出 |
|---|---|---|---|---|
| [T00](T00-ime-poc.md) | 中文輸入 POC（**人工**，✅ 已完成） | 前置 | — | Avalonia 注音輸入、字型、主題、字級的實測結果 |
| [T01](T01-solution-skeleton.md) | 方案骨架與共用基礎（✅ 已完成） | 0 | — | sln、專案、套件、stub、DI、共用工具、CI |
| [T02](T02-sqlite-store.md) | SQLite 儲存層（✅ 已完成） | 1 | T01 | `SqliteKnowledgeStore` |
| [T03](T03-onnx-embedding.md) | 本機 ONNX Embedding（✅ 已完成） | 1 | T01 | `OnnxEmbeddingService`、模型下載腳本 |
| [T04](T04-text-parsers.md) | 文字類解析器（✅ 已完成） | 1 | T01 | txt / md / json / xml / log / html / rtf |
| [T05](T05-word-parser.md) | Word 解析器（✅ 已完成） | 1 | T01 | `WordParser` |
| [T06](T06-powerpoint-parser.md) | PowerPoint 解析器（✅ 已完成） | 1 | T01 | `PowerPointParser` |
| [T07](T07-pdf-parser.md) | PDF 解析器（✅ 已完成） | 1 | T01 | `PdfParser` |
| [T08](T08-spreadsheet-parser.md) | Excel / CSV 解析器（✅ 已完成） | 1 | T01 | `SpreadsheetParser`、`SpreadsheetRegionReader` |
| [T09](T09-chunker.md) | 結構化切塊（✅ 已完成） | 1 | T01 | `StructuredChunker` |
| [T15](T15-desktop-shell.md) | 桌面外殼（Avalonia）、主題、系統匣（✅ 已完成，macOS 畫面操作與 Windows 待人工確認） | 1 | T01 | 主視窗、導覽、主題、字級、系統匣、狀態列 |
| [T10](T10-indexing-pipeline.md) | 索引管線與資料夾同步 | 2 | T02、T03、T09 | `IndexingService` |
| [T11](T11-hybrid-search.md) | Hybrid 檢索 | 2 | T02、T03 | `HybridSearchService` |
| [T12](T12-table-query.md) | Excel 表格查詢 | 2 | T02、T08 | `TableQueryService` |
| [T14](T14-ai-client-integration.md) | AI 軟體設定整合與狀態 | 2 | T02 | `Integrations.*` |
| [T13](T13-mcp-server.md) | MCP Server | 3 | T11、T12 | `Contexo.Mcp` |
| [T16](T16-ui-folders.md) | 介面：資料夾頁與首次啟動精靈 | 3 | T10、T15 | 資料夾頁、子資料夾視窗、移除確認、精靈 |
| [T17](T17-ui-search.md) | 介面：試試看搜尋 | 3 | T11、T15 | 搜尋頁 |
| [T18](T18-ui-ai-clients.md) | 介面：AI 軟體頁 | 3 | T14、T15 | AI 軟體頁 |
| [T19](T19-ui-settings.md) | 介面：設定頁 | 3 | T10、T15 | 設定頁、開機啟動 |
| [T20](T20-about-diagnostics.md) | 關於與問題回報 | 3 | T02、T15 | `DiagnosticsExporter`、關於頁 |
| [T21](T21-packaging.md) | 打包與安裝程式 | 4 | T13、T16～T20 | 發布設定、安裝程式、CI 產出 |
| [T22](T22-end-to-end.md) | 端對端驗證 | 4 | T10、T13 | 測試語料產生器、E2E 測試、驗收報告 |

## 執行順序

```
前置   T00 ✅（使用者判定通過）
波次 0  T01 ✅
         │
波次 1  T02✅ T03✅ T04✅ T05✅ T06✅ T07✅ T08✅ T09✅ T15✅     ← 全部可平行
         │
波次 2  T10(T02,T03,T09)  T11(T02,T03)  T12(T02,T08)  T14(T02)
         │
波次 3  T13(T11,T12)  T16(T10,T15)  T17(T11,T15)  T18(T14,T15)  T19(T10,T15)  T20(T02,T15)
         │
波次 4  T21  T22
```

- 同一波次的任務**檔案範圍互不重疊**，可同時交給不同子代理。
- 解析器（T04～T08）不是 T10 的硬相依：T10 透過 `IParserRegistry` 取用，解析器未完成時該格式會被記為「不支援」，T10 的測試用假的解析器。
- T01 必須先合併，其他任務才能開始。

## 給派工者的提醒

- 派工時給子代理：儲存庫、分支基準（`main`）、任務檔路徑，並要求先讀 `AGENTS.md`。
- 桌面程式用 Avalonia，**macOS 可以實際執行與操作**（只用於開發驗證，不發布 Mac 版）。畫面任務（T15～T20）以 Headless 自動測試加 Mac 實際操作驗收；Windows 專屬項目（登錄檔、系統匣位置、安裝程式）彙整到 `tests/manual/CHECKLIST.md`，在 T21 前於 Windows 統一確認。
- T00 已判定通過，但 POC 尚未逐項實測：T01 會先編譯 POC，T15 與 Windows 檢查表會補上注音輸入的確認。發現阻擋性問題時停止畫面任務並回報。
- 需要網路的步驟：NuGet 還原、模型下載（huggingface.co）、查閱 AI 軟體設定格式文件。環境無法連線時子代理會停下來回報。
- 每個任務完成後檢查任務檔頂端「狀態」與「完成紀錄」。

## 派工指南

### 進度追蹤

每個任務的狀態寫在任務檔頂端（待辦／進行中／完成）。派工前確認相依任務都是「完成」且已合併到 `main`。

### 建議順序

1. **T01**：單獨一個子代理。完成並合併後才進入下一步。
2. **波次 1**：T02～T09、T15 共 9 個，可同時派給 9 個子代理（或依可用數量分批）。建議優先：T02、T03、T09（波次 2 的關鍵路徑）、T15（所有畫面任務的前提）。
3. **波次 2**：T10、T11、T12、T14。
4. **波次 3**：T13、T16～T20。
5. **波次 4**：T21（需 Windows）、T22。

關鍵路徑：T01 → T02／T03／T09 → T10 → T16／T19 → T21。

### 執行環境需求

- .NET 10 SDK、可連線 `api.nuget.org`。
- T03、T22 需要連線 `huggingface.co` 下載模型。
- T14 需要查閱各 AI 軟體官方文件（網路搜尋）。
- 畫面任務（T15～T20）建議在 macOS 上執行，以便實際操作；T21 需要 Windows。

### 派工提示範本

```
你負責 Contexo 專案的任務 {Txx}。

儲存庫：https://github.com/NEILLOG/Contexo（以 main 為基準）

請依序：
1. 完整閱讀 AGENTS.md。
2. 閱讀 task/README.md 與 task/{Txx 檔名}.md，以及任務檔「必讀」列出的文件。
3. 確認相依任務的狀態都是「完成」；不是的話停止並回報。
4. 開分支 task/{Txx}-{簡短英文描述}，只修改任務檔「可修改範圍」內的檔案。
5. 完成所有驗收條件，執行 dotnet build Contexo.slnx -warnaserror 與 dotnet test 確認通過。
6. 把任務檔狀態改為「完成」並填寫完成紀錄，提交並推送分支（不開 PR，由維運者檢查後直接合併到 main）。

遇到以下情況請停止並回報，不要自行繞過：
- 需要修改共用契約（src/Contexo.Core/Abstractions/）或範圍外的檔案
- 需要新增 AGENTS.md 技術棧以外的套件
- 無法連線 NuGet 或 Hugging Face
- 規格有矛盾或不清楚
```

## 任務檔格式

每個任務檔包含：狀態、相依、目標、必讀、要做、不做、可修改範圍、實作要點與已知陷阱、驗收條件、完成紀錄。「可修改範圍」以外的檔案一律不動。

## 第二階段（尚未拆分）

圖片 OCR 與視覺模型、掃描型 PDF、Outlook .msg / .eml、Visio、ODF、壓縮檔、舊版 Office 格式、公司伺服器模式（匯入設定、模式切換警語）、Reranker。
