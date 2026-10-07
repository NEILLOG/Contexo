# T11 Hybrid 檢索

- **狀態**：待辦
- **波次**：2
- **相依**：T02、T03
- **必讀**：`AGENTS.md`、`plan/03-retrieval-and-mcp.md`、`src/Contexo.Core/Abstractions/Search.cs`、`Storage.cs`、`Embedding.cs`

## 目標

實作 `Search.HybridSearchService : ISearchService`：語意（向量）＋關鍵字（FTS5 trigram）兩路檢索，合併排序。WPF 的「試試看搜尋」與 MCP 的 `search` 共用此服務。

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

（由執行者填寫）
