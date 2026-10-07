# T06 PowerPoint 解析器

- **狀態**：待辦
- **波次**：1
- **相依**：T01
- **必讀**：`AGENTS.md`、`plan/02-ingestion.md`（PowerPoint）、`src/Contexo.Core/Abstractions/Parsing.cs`、`Tables.cs`

## 目標

實作 `Parsing.PowerPoint.PowerPointParser`（`.pptx`）。重點是**還原流程圖與架構圖的關係**，而不只是抽文字。

## 要做

以 `PresentationDocument.Open(stream, false)` 開啟，依 `presentation.xml` 的 `sldIdLst` 順序處理投影片（隱藏投影片 `show="0"` 也處理，但在 `Title` 後加「（隱藏）」）。

每張投影片輸出：

1. **一個 `Slide` 區段**（`KeepWhole=true`，`Location.Slide` = 1 起算，`Location.Title` = 標題）：
   - 標題：placeholder type 為 `title` 或 `ctrTitle` 的圖形文字。
   - 其他文字圖形依**閱讀順序**（先依 Y 座標分列，同列依 X）輸出，群組圖形（`grpSp`）遞迴展開並套用群組座標轉換。
   - 段落層級（`a:pPr lvl`）以縮排「- 」表示。
   - 表格（`a:tbl`）→ `TableModel`（`gridSpan`、`rowSpan`、`hMerge`、`vMerge`）→ HTML，放在區段文字中。
   - 圖表（`c:chart`）：讀取 `ChartPart` 中各數列的快取值（`c:strCache` / `c:numCache`），輸出成「圖表：{標題}」加一個 HTML 表格（類別 × 數列）。
2. **一個 `Diagram` 區段**（只在有連接線或 SmartArt 時）：
   - **連接線**：`p:cxnSp` 的 `a:stCxn id` 與 `a:endCxn id` 指向圖形 ID，輸出 Mermaid 風格的行：`[起點圖形文字] --> [終點圖形文字]`；連接線本身有文字時輸出 `[A] -->|文字| [B]`。只有一端連上的連接線略過。箭頭方向依 `a:headEnd` / `a:tailEnd`：只有 head 有箭頭時方向反轉。
   - 沒有文字的圖形用「圖形{ID}」代稱。
   - **SmartArt**：從 `DiagramDataPart` 讀取節點（`dgm:pt` type 為 node）與 `dgm:cxn`（parOf）關係，輸出縮排的階層清單。
3. **一個 `Notes` 區段**（只在有備忘稿時）：`NotesSlidePart` 中 body placeholder 的文字。

另外：

- 內嵌檔案與圖片：對每個 `SlidePart` 使用 `OfficeEmbeddedContent`，`Location.Slide` 設定好，圖片的 `contextText` 為投影片標題。
- 版面配置與母片上的固定文字（頁尾、公司名稱）不輸出。
- 密碼保護 → `PasswordProtected`；無法開啟 → `Corrupted`。

## 不做

- 舊版 `.ppt`。
- 渲染投影片成圖片。
- 圖片內容辨識（只收集 `Images`）。

## 可修改範圍

- `src/Contexo.Core/Parsing/PowerPoint/**`
- `tests/Contexo.Core.Tests/Parsing/PowerPoint/**`

## 實作要點與已知陷阱

- 圖形 ID 是 `p:cNvPr` 的 `id` 屬性，只在同一張投影片內唯一。
- 群組內子圖形的座標要經 `a:chOff` / `a:chExt` 換算到投影片座標，否則閱讀順序會錯。
- 預留位置（placeholder）圖形可能沒有 `a:xfrm`，座標要從對應的版面配置繼承；找不到就排在最後。
- 用 `a:t` 逐段組字，不要用 `InnerText`（會把段落黏在一起）。
- 測試檔在測試中用 OpenXml SDK 產生（建立 `PptxBuilder` 輔助類別）。建立連接線時要寫入 `a:stCxn` / `a:endCxn`。

## 驗收條件

`dotnet test --filter FullyQualifiedName~Parsing.PowerPoint` 全部通過，至少涵蓋：

1. 3 張投影片的順序、標題、`Location.Slide` 正確。
2. 文字依閱讀順序：左上、右上、左下的三個文字框輸出順序正確；群組內文字有出現且順序正確。
3. 流程圖：「申請 → 主管審核 → 採購」三個圖形加兩條連接線，`Diagram` 區段含兩行正確方向的關係；只連一端的連接線不輸出；反向箭頭正確反轉。
4. SmartArt：一個兩層的階層圖輸出正確縮排。
5. 表格合併儲存格正確；圖表輸出類別與數值。
6. 備忘稿輸出為 `Notes` 區段。
7. 內嵌 docx 被取出，`ContainerLocation.Slide` 正確；圖片被收集且有 `ContextText`。
8. 母片上的頁尾文字不出現。
9. 損壞檔案拋 `Corrupted`。

## 完成紀錄

（由執行者填寫）
