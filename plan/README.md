# 文脈 Contexo 產品計畫

> 狀態：規劃完成，進入實作（2026-10-09）
> 本資料夾只放計畫與設計決策，不含工作拆分與時程。

## 一句話

Contexo 是一個 Windows 桌面程式（macOS 僅用於開發驗證）：使用者指定資料夾，程式把裡面的辦公室文件整理成可檢索的知識庫，讓使用者已經在用的 AI 軟體（Claude Desktop、VS Code 等）透過 MCP 查詢這些內容。可以理解為「Dropbox 的向量版」。

## 目標使用者與前提

- **完全不懂技術的辦公室使用者**，電腦是一般文書機，可能沒有獨立顯卡。
- **乾淨環境**：不能要求使用者安裝 Ollama、Python、Node、Docker 或任何外部軟體。一個安裝檔就要能用。
- 使用者不需要手動設定路徑、寫排除規則或編輯 JSON。
- 生成（LLM 回答）不在本機做，由使用者的 AI 軟體或公司的 AI gateway 負責。Contexo 只負責「解析、索引、檢索」。

## 核心決策摘要

| 主題 | 決策 |
|---|---|
| 技術棧 | C# .NET 10，Avalonia 桌面程式（Windows 正式發布、macOS 開發驗證），self-contained 發布 |
| 對外介面 | MCP，**stdio** 傳輸，獨立的 `Contexo.Mcp.exe` 由 AI 軟體啟動 |
| Embedding | 程式內以 ONNX Runtime 執行；可切換為公司伺服器 |
| 儲存 | 單一 SQLite 檔：原文、向量、FTS5 全文索引放在一起 |
| 檢索 | Hybrid：向量相似度 + FTS5 關鍵字，合併排序 |
| 檔案解析 | 全部用 .NET 套件在程式內完成，不依賴 Office、LibreOffice 或 Python |
| Excel | 小表整張轉 HTML；大表做「表格說明向量 + 查詢時載入的 SQL 查詢」 |
| PPTX / DOCX | 用 OpenXml 讀結構（連接線、SmartArt、表格、備忘稿），只有圖片才送視覺模型 |
| 運算來源 | 「只用這台電腦」與「使用公司伺服器」可切換，伺服器設定只能匯入 |
| 介面原則 | 用「選」取代「填」，用「預設」取代「設定」 |

## 文件索引

1. [架構](01-architecture.md)：程序組成、MCP stdio、儲存結構、運算來源切換
2. [檔案解析與切塊](02-ingestion.md)：各格式處理方式、Excel 策略、資料夾同步
3. [檢索與 MCP 工具](03-retrieval-and-mcp.md)：Hybrid search、MCP 工具、AI 軟體連接與狀態偵測
4. [使用者介面](04-ui.md)：各頁面規劃、互動原則
5. [維運與品質](05-operations.md)：效能、版本號、問題回報、打包、測試方式
6. [風險與待決事項](06-open-questions.md)

介面草圖：[ui-mockup.html](ui-mockup.html)（用瀏覽器直接開啟，可點選操作，右上角可切換設計註記）。

## 第一版範圍

**納入**

- docx、pptx、文字型 PDF、純文字類（txt、md、csv，含 Big5 偵測）
- 清單型與表單型 Excel（小表轉 HTML、大表進 SQL 查詢）
- 本機 ONNX embedding、SQLite、hybrid search
- Contexo.Mcp.exe（stdio）與 Claude Desktop 一鍵加入
- 資料夾管理、試試看搜尋、設定、問題回報

**第二階段**

- 圖片 OCR 與視覺模型解釋、掃描型 PDF
- Outlook .msg / .eml、Visio .vsdx、ODF、壓縮檔
- 舊版 .doc / .xls / .ppt
- 公司伺服器模式
- Reranker

**不做或視需求再議**

- 本機 LLM 生成
- 會議錄音轉文字
- Outlook .pst 封存檔
