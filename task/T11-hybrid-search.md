# T11 Hybrid 檢索

- **狀態**：完成
- **波次**：2
- **相依**：T02、T03
- **必讀**：`AGENTS.md`、`plan/03-retrieval-and-mcp.md`、`src/Contexo.Core/Abstractions/Search.cs`、`Storage.cs`、`Embedding.cs`

## 目標

實作 `Search.HybridSearchService : ISearchService`：語意（向量）＋關鍵字（FTS5 trigram）兩路檢索，合併排序。桌面程式的「試試看搜尋」與 MCP 的 `search` 共用此服務。

## 要做

### 1. 向量索引快取（`Search/VectorIndex.cs`）

- 把目前模型（`IEmbeddingService.ModelId`）的所有向量載入記憶體：一個連續的 `float[]`（N × 維度）加上 `long[]` chunk id。
- 每次搜尋前呼叫 `GetIndexVersionAsync`；版本變了才重新載入（重新載入期間其他搜尋使用舊快取）。
- 相似度：向量已正規化，cosine = 內積。用 `System.Numerics.Vector<float>` 寫 SIMD 內積（不要為此新增套件）。
- 取前 K：用大小為 K 的最小堆積，不要整個排序。

### 2. 關鍵字查詢組成（`Search/KeywordQueryBuilder.cs`）

輸入使用者查詢句，輸出 `(string? ftsQuery, IReadOnlyList<string> likeTerms)`：

1. 正規化：全形英數轉半形、轉小寫、移除標點（保留 `-`、`_`、`.` 於英數字之間，例如型號 `ABC-123`、版本 `1.2`）。
2. 切成片段：英數連續字串為一個詞；CJK 連續字串為一個詞。
3. 每個詞：
   - 長度 ≥ 3 的英數詞 → `"詞"`（FTS5 字串，雙引號跳脫）。
   - 長度 ≥ 3 的 CJK 詞 → 產生所有長度 3 的連續子字串，各自加引號（trigram 會命中包含它們的片段）。
   - 長度 2 的詞（英數或 CJK）→ 放入 `likeTerms`。
   - 長度 1 的詞略過。
4. 全部以 ` OR ` 連接；沒有任何 FTS 詞時 `ftsQuery = null`。
5. 最多 32 個 FTS 詞、8 個 like 詞（超過時取前面的）。

### 3. 搜尋流程

1. 語意：`IEmbeddingService.IsAvailable` 時 `EmbedQueryAsync` → 向量索引取前 `max(TopK × 4, 30)`。不可用或發生例外時 `Degraded = true`，只走關鍵字。
2. 關鍵字：`KeywordSearchAsync(ftsQuery, likeTerms, max(TopK × 4, 30))`。
3. 合併：**RRF**（Reciprocal Rank Fusion），`score = Σ 1 / (60 + rank)`；記錄每筆由哪一路命中（`MatchKinds`）。
4. 取 `GetChunksAsync`，套用 `PathPrefixes` 篩選（不分大小寫、目錄邊界正確）。
5. **同檔去重**：同一檔案最多保留 3 筆。
6. 取前 `TopK`，分數正規化到 0～1（除以第一名分數）。
7. `FileName` 由路徑取得。

### 4. 其他

- 空白查詢回傳空結果。
- 執行緒安全，可同時處理多個搜尋。
- 記錄耗時（不記錄查詢內容）。

## 不做

- Reranker（第二階段）。
- 查詢改寫、同義詞。

## 可修改範圍

- `src/Contexo.Core/Search/**`
- `tests/Contexo.Core.Tests/Search/**`

## 實作要點與已知陷阱

- FTS5 查詢字串中的雙引號要寫成兩個雙引號。不要讓使用者輸入的 `AND`、`NOT`、`*`、`:`、`^`、`(` 被當成語法：所有詞都用引號包起來就安全。
- 向量快取以模型 id 區分；模型 id 改變時整個重載。
- 10 萬個 512 維向量約 200 MB 記憶體，屬可接受範圍；載入時串流讀取 `ReadVectorsAsync`，先用 `List` 收集再一次配置連續陣列，或預先以統計數量配置。
- 測試用假的 embedding（例如依關鍵字對應固定向量）讓語意結果可預期。

## 驗收條件

`dotnet test --filter FullyQualifiedName~Search` 全部通過，至少涵蓋：

1. `KeywordQueryBuilder`：「去年給客戶的報價單」、「ABC-123 規格」、「報價」、「Ｑ３營收」、含 `"`、`*`、`AND` 的輸入，輸出符合規則且 FTS 語法有效（實際送進 SQLite 執行不報錯）。
2. 只有關鍵字命中、只有語意命中、兩者都命中三種情況，`MatchedBy` 正確，兩者都命中的排序較前。
3. Embedding 不可用 → `Degraded = true`，仍有關鍵字結果。
4. 寫入新文件後（index_version 變動），下一次搜尋就能找到。
5. `PathPrefixes` 篩選正確（`C:\A\報價` 不會選到 `C:\A\報價單`）。
6. 同一檔案超過 3 筆命中時只回傳 3 筆。
7. `TableSummary` 片段命中時 `TableId` 有值。
8. 效能：5 萬個 512 維隨機向量，單次搜尋（不含 embedding）< 100ms（在完成紀錄寫下實測數字）。

## 完成紀錄

**做了什麼**

- `src/Contexo.Core/Search/KeywordQueryBuilder.cs`：`Build(query)` 回傳 `(ftsQuery, likeTerms)`。全形英數轉半形、轉小寫、`-` `_` `.` 只保留在英數字之間；英數詞 ≥3 字元整詞加引號、CJK 詞 ≥3 字元拆成所有 3 字元子字串、2 字元詞進 `likeTerms`、1 字元略過；重複詞去除；上限 32 個 FTS 詞與 8 個 like 詞（取前面）。所有 FTS 詞都加雙引號（引號內的 `"` 變兩個），`AND` `NOT` `*` `:` `^` `(` 不會變成語法。CJK 判斷除任務列的漢字區外，另含假名、注音、韓文音節。
- `src/Contexo.Core/Search/VectorIndex.cs`：`VectorSnapshot`（連續 `float[]` N×維度＋`long[]` chunk id、`Vector<float>` SIMD 內積、大小 K 的最小堆積取前 K）與 `VectorIndex`（以 `GetIndexVersionAsync` 與模型 id／維度判斷是否重載；資料過期但模型相同時，第一個搜尋者重載，同時進來的搜尋沿用舊快取；模型改變或第一次載入則等待載入完成）。
- `src/Contexo.Core/Search/HybridSearchService.cs`：建構式 `(IKnowledgeStore, IEmbeddingService, ILogger<HybridSearchService>)`，DI 原本就註冊好。關鍵字與語意兩路並行；RRF（k=60）合併並記錄 `MatchedBy`；`GetChunksAsync` 後套用 `PathPrefixes`（不分大小寫、目錄邊界、`\` 與 `/` 視為相同）、同檔最多 3 筆、取前 `TopK`、分數除以第一名；`FileName` 由路徑取得。日誌只記耗時與筆數，不記查詢內容。
- 測試：`tests/Contexo.Core.Tests/Search/`（`KeywordQueryBuilderTests`、`HybridSearchServiceTests`、`VectorIndexTests`），用假 embedding 與真實的 `SqliteKnowledgeStore`。

**驗收結果（macOS，.NET 10）**

- `dotnet build Contexo.slnx -warnaserror`：0 警告、0 錯誤。
- `dotnet test --filter FullyQualifiedName~Search`：通過 58（含儲存層既有的檢索測試）、失敗 0。
- `dotnet test`：Core 595 通過／14 略過、App 25、Mcp 5 全數通過；Desktop 38 個中 `SingleInstanceTests.Can_be_woken_more_than_once`（T15）第一次跑失敗一次，之後連跑兩次都通過，與本任務無關（偶發的逾時）。
- 驗收 1：「去年給客戶的報價單」、「ABC-123 規格」、「報價」、「Ｑ３營收」、含 `"` `*` `AND` 等輸入皆有測試，輸出符合規則，並實際送進 SQLite FTS5 trigram 資料表執行（不報錯）。
- 驗收 2～7：只有關鍵字／只有語意／兩者都命中的 `MatchedBy` 與排序、embedding 不可用與拋例外都 `Degraded = true` 仍有關鍵字結果、寫入新文件與刪除文件後下一次搜尋即反映、`PathPrefixes`（`報價` 不選到 `報價單`、大小寫、兩種分隔符）、同檔最多 3 筆、`TableSummary` 的 `TableId` — 皆有對應測試。另有：模型 id 改變會重載、24 個並行搜尋加並行寫入、取消會往外傳、日誌不含查詢內容。
- 驗收 8（5 萬個 512 維向量，單次搜尋 top 30，不含 embedding）：Debug 組態中位數 35.6 ms（最小 28.0、最大 36.2）；Release 組態 3.2 ms。測試門檻 100 ms，使用 Debug 組態。

**無法在目前環境驗證**

- 未用真實的 bge 模型做端到端搜尋品質驗證（模型不在此環境，測試以假 embedding 為主）；品質評估屬 T22。
- Windows 路徑（`C:\A\報價`）：macOS 的 `Path.GetFullPath` 不會把它當絕對路徑，測試以 `Path.Combine` 組路徑；比對邏輯本身把 `\` 與 `/` 視為相同，需在 Windows 實測一次。

**與規格不同或規格未明之處**

- 有指定 `PathPrefixes` 時，候選數量放大為 5 倍（`max(TopK×4, 30) × 5`），因為路徑篩選在取回候選之後才做，否則範圍小的資料夾容易被全域前 N 名擠掉。沒指定時與規格相同。
- `TopK` 上限夾在 200（避免 `TopK×4` 溢位）；`TopK <= 0` 或空白查詢回傳空結果（`Degraded = false`）。
- 語意路徑沒有相似度下限：只要有向量就一定回傳最近的前 N 筆，與規格一致，但意味著無關內容也可能以「語意命中」出現在尾端。之後若要加門檻或 reranker 另案處理。
- 排序同分時依語意名次、再依 chunk id，結果可重現。
- 語意路徑任何非取消的例外都視為降級（記錄警告但不含查詢內容）。

**給後續任務的注意事項**

- T13（MCP）／T17（試試看搜尋）：直接注入 `ISearchService`；`Score` 只在同一次回應內可比；`Degraded` 為 true 時可提示「目前只用關鍵字找」。`SearchHit.Kind == TableSummary` 時用 `TableId` 接 `describe_table`／`query_table`。
- `HybridSearchService` 是單例，內含向量快取（約 N×維度×4 位元組）；Mcp 與 Desktop 各自一份。Mcp 是獨立行程，靠 `index_version` 偵測桌面程式的寫入，每次搜尋只多一次輕量查詢。
- 第一次搜尋（或模型改變後）會載入全部向量，10 萬筆 512 維約 200 MB，載入期間該次搜尋會等待。

