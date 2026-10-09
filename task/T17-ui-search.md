# T17 介面：試試看搜尋

- **狀態**：完成
- **波次**：3
- **相依**：T11、T15
- **必讀**：`AGENTS.md`（10a 節）、`plan/03-retrieval-and-mcp.md`（「試試看搜尋」）、`plan/ui-mockup.html` 的「試試看搜尋」頁、`src/Contexo.Core/Abstractions/Search.cs`

## 目標

讓使用者打一句話，就看到 AI 會查到哪些內容，用來建立信任，也是開發時檢查檢索品質的工具。**不經過 LLM，不產生回答。**

## 要做

`SearchViewModel` / `SearchView`：

1. 標題「試試看搜尋」、說明「輸入一句話，看看 AI 會找到哪些內容。這裡只列出找到的段落，不會產生回答。」
2. 搜尋框＋「搜尋」按鈕（Enter 也會搜尋）。搜尋中顯示進度指示並停用按鈕；新搜尋會取消前一次。
3. 尚未搜尋時的空狀態：三個可點的範例（例如「去年給客戶的報價單」「採購驗收標準」「會議決議」），點了就填入並搜尋。
4. 結果卡片（`ISearchService.SearchAsync`，`TopK = 10`）：
   - 檔名（粗體）＋右側「相關度 0.86」。
   - 位置：工作表「名稱」· 範圍／第 N 張投影片「標題」／第 N 頁／章節路徑；內嵌檔加「內嵌：x.xlsx ›」前綴。（位置文字產生方式抽成 `Search/LocationText.cs`，T13 的 MCP 輸出也是同樣格式，但 T13 在不同專案，各自實作即可。）
   - 內容摘錄：最多 300 字；表格類（HTML）內容轉成「欄：值；欄：值」的純文字顯示；查詢中的詞（長度 ≥ 2 的英數詞與 CJK 片段）以醒目底色標示。
   - 標籤：「語意」「關鍵字」（依 `MatchedBy`）；大型表格加「大型表格」標籤。
   - 按鈕：「開啟原檔」「在資料夾中顯示」（`IShellLauncher`）；檔案已不存在時停用並顯示「檔案已移動或刪除」。
   - 右鍵：「不要讓 AI 讀這個檔案」（確認後 `AddExclusionAsync`，並從結果移除）。
5. `Degraded` 時在結果上方顯示：「目前只使用關鍵字比對（語意搜尋尚未準備好）。」
6. 沒有結果：「沒有找到相關內容。可以換個說法，或確認檔案所在的資料夾已加入。」
7. 資料庫沒有任何文件時：「還沒有收錄任何資料。先到『資料夾』頁面加入資料夾。」＋前往按鈕。

## 不做

- 搜尋歷史、篩選條件。

## 可修改範圍

- `src/Contexo.App/Search/**`
- `src/Contexo.Desktop/Views/Search/**`
- `tests/Contexo.Desktop.Tests/Search/**`
- `tests/Contexo.App.Tests/Search/**`

## 實作要點與已知陷阱

- 醒目標示：在 VM 產生「片段＋是否標示」的清單，View 用 Avalonia `TextBlock.Inlines`（`Run` 加背景）或 `SelectableTextBlock` 呈現（以附加屬性或轉換器），不要在 VM 裡產生 XAML。
- 不記錄查詢內容到日誌。
- HTML 轉純文字：輸入一定是 `HtmlTableRenderer` 的格式，簡單的標籤掃描即可。

## 驗收條件

1. `dotnet test --filter FullyQualifiedName~Contexo.App.Tests.Search` 通過，以假的 `ISearchService` 測試：
   - 搜尋中狀態、取消前一次、空查詢不搜尋。
   - 位置文字五種情況。
   - 醒目片段切分（中英混合、重疊詞）。
   - 表格 HTML 轉「欄：值」。
   - `Degraded`、無結果、無資料三種提示。
   - 排除後結果被移除。
2. 編譯成功。
3. Headless 測試：有結果、無結果、無資料、降級四種狀態的截圖。
4. 在 macOS 實際操作確認（`dotnet run --project src/Contexo.Desktop`，列入完成紀錄）：與 mockup 對照；用注音輸入查詢；開啟原檔與在 Finder 中顯示正常；深淺色與字級下顯示正常。

## 完成紀錄

### 做了什麼

- `Contexo.App/Search/`：
  - `SearchViewModel`（在 T15 的空類別上擴充，實作 `IPageLifecycle`）：直接注入 `ISearchService`、`IKnowledgeStore`、`IShellLauncher`、`IDialogService`、`INavigationService`、`ILogger`；`TopK = 10`；空白查詢不搜尋；新搜尋取消前一次（舊結果不會蓋掉新結果）；搜尋前以 `GetStatisticsAsync` 檢查是否有資料（`ChunkCount == 0` 就顯示「還沒有收錄任何資料」並不呼叫搜尋）；`Degraded`、無結果、無資料、搜尋失敗各有狀態旗標；範例按鈕為 `SearchExample`（文字加指令）；排除前先 `ConfirmAsync`，成功後 `AddExclusionAsync` 並移除同一檔案的所有結果。
  - `SearchResultItemViewModel`：一張結果卡片（相關度文字 `相關度 0.86`、位置、摘錄片段、語意／關鍵字／大型表格旗標、原檔是否存在、開啟／顯示／排除指令）。
  - `LocationText.Format`：位置文字（工作表＋範圍、第 N 張投影片「標題」、第 N 頁、章節路徑、表格標題、`內嵌：a.xlsx › …` 前綴）。
  - `TableHtmlText`：`HtmlTableRenderer` 的 HTML 轉「欄：值；欄：值」（每列一行；處理 `thead`／全 `th` 表頭、多層表頭以 `_` 連接、rowspan／colspan、`caption`、HTML 實體）。
  - `QueryHighlighter`：查詢詞擷取（全形轉半形；英數詞 ≥ 2；CJK 兩字詞原樣、≥ 3 字拆成三字視窗，與關鍵字搜尋實際比對的單位一致）與片段切分（不分大小寫、重疊或相連的命中合併）。
  - `ExcerptBuilder`：摘錄最多 300 字；第一個命中離開頭太遠時，視窗移到命中附近並加「…」。
  - `TextFragment`。
- `Contexo.Desktop/Views/Search/`：`SearchView.axaml`（標題與說明、搜尋框＋按鈕、Enter 搜尋、進度條、降級／錯誤橫幅、無資料（含前往按鈕）／無結果／範例三種提示、結果卡片與右鍵選單）；`HighlightedText.cs`（附加屬性 `HighlightedText.Fragments`，用 `Run.Background` 畫醒目底色，色彩取自 `Brush.WarnSoft`，換主題時重新套用）。
- 測試：`tests/Contexo.App.Tests/Search/`（`SearchViewModelTests`、`LocationTextTests`、`ExcerptTests`、`SearchFakes`，共 51 個）；`tests/Contexo.Desktop.Tests/Search/SearchViewTests.cs`（8 個 Headless 測試）。

### 驗收條件結果

1. `dotnet test --filter FullyQualifiedName~Contexo.App.Tests.Search`：通過 51、失敗 0。涵蓋：搜尋中狀態、取消前一次、空查詢不搜尋；位置文字（工作表、投影片、頁、章節路徑、內嵌前綴，另有空位置與組合）；醒目片段切分（中英混合、重疊詞、相連詞、全形與重複詞）；表格 HTML 轉「欄：值」（含多層表頭與合併儲存格）；`Degraded`／無結果／無資料三種提示；排除（確認、取消、失敗、最後一筆被排除）後結果被移除。
2. `dotnet build Contexo.slnx -warnaserror`：成功，0 警告 0 錯誤。
3. Headless 測試（8 個）通過，截圖在 `artifacts/screenshots/`（不提交）：`search-initial`（範例）、`search-results`（有結果）、`search-no-results`、`search-no-data`、`search-degraded`、`search-results-dark`（深色）。另驗證：Enter 會搜尋、卡片有右鍵「不要讓 AI 讀這個檔案」、醒目文字真的有底色、檔案不存在的卡片按鈕停用並顯示「檔案已移動或刪除」。
4. `dotnet test`（整個方案）：App 76、Mcp 5、Core 909（14 略過，模型不存在）通過；Desktop 46 個中 `SingleInstanceTests.Can_be_woken_more_than_once` 在多個代理同時執行時失敗一次，單獨重跑 7 個單一執行個體測試全數通過（與本任務無關）。
5. macOS 實際操作：**未完成**。這台機器上同時有其他代理的 Contexo 在執行（單一實例機制會讓第二個直接結束），且環境沒有螢幕擷取與輔助使用權限，所以無法開視窗操作。**需要人在 Mac 上確認**：與 mockup 對照、注音輸入查詢（Enter 在選字時是否不會誤觸搜尋）、「開啟原檔」與「在 Finder 中顯示」、深淺色與三段字級。畫面外觀以 Headless 截圖（Skia 實際繪製）替代。

### 與規格不同或規格未明之處

- 為了不修改範圍外的 `tests/Contexo.App.Tests/Shell/ShellViewModelTests.cs`、`tests/Contexo.Desktop.Tests/TestShell.cs`、`ViewLocatorTests.cs`（它們都用 `new SearchViewModel()`），`SearchViewModel` 另有 `internal` 的無參數建構式（只供這些測試建出「只顯示的頁面」，用它搜尋會拋例外）。DI 只看 public 建構式，不受影響。若維運者想改成傳入假服務，三個檔案各改一行即可，無參數建構式可刪。
- 原檔是否存在以建構式選用參數 `Func<string,bool>? fileExists`（預設 `File.Exists`）取得，方便測試；DI 不需額外註冊。
- 排除確認對話框的確認鈕不是危險樣式（不是刪除檔案，原檔完全不動），內容寫明「原本的檔案不會被刪除或更動」。
- 無資料的判斷是 `StoreStatistics.ChunkCount == 0`；進入頁面時（`OnNavigatedTo`）與每次搜尋前都會檢查，所以資料夾被讀取後回到本頁會自動切回範例畫面。
- 「檔案已不存在」只在搜尋當下檢查一次，不會即時更新。
- 摘錄的醒目標示不含語意命中的同義詞（只標示查詢字面出現的詞），語意命中的卡片可能沒有任何底色，這是預期行為。
- 搜尋失敗時顯示「搜尋時發生問題，請稍後再試一次。」（規格沒寫，補上），日誌只記例外型別、筆數與耗時，不記查詢。

### 給後續任務的注意事項

- 搜尋頁的「大型表格」卡片之後可接 T13 的 `describe_table`／`query_table` 說明；`SearchHit.TableId` 目前只用來判斷標籤。
- `HighlightedText` 附加屬性可供其他頁面重用（例如日後的結果預覽）；位置文字格式 `LocationText.Format` 與 T13 的 MCP 輸出刻意一致（T13 在不同專案，需各自實作）。
- 第一次搜尋會載入全部向量（T11 注意事項），第一次可能稍慢，畫面以進度條表示搜尋中。

