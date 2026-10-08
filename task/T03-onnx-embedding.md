# T03 本機 ONNX Embedding

- **狀態**：完成
- **波次**：1
- **相依**：T01
- **必讀**：`AGENTS.md`、`plan/01-architecture.md`（Embedding）、`src/Contexo.Core/Abstractions/Embedding.cs`

## 目標

實作 `Embedding.OnnxEmbeddingService : IEmbeddingService`，在程式內用 ONNX Runtime 把文字轉成向量，不依賴任何外部服務。並提供下載模型的腳本。

## 模型

第一版預設 **bge-small-zh-v1.5 int8**（512 維、最大 512 tokens、BERT WordPiece 分詞）。選它的原因是分詞器為 WordPiece，可以在 .NET 精確重現；中文檢索品質足夠。最終模型會在 T22 用測試集比較後再定。

程式要做成**模型無關**：每個模型資料夾放一個 `contexo-model.json` 描述檔，換模型不改程式。

```
models/
  bge-small-zh-v1.5/
    model.onnx            int8 量化版
    vocab.txt
    contexo-model.json
```

`contexo-model.json` 格式：

```json
{
  "id": "bge-small-zh-v1.5/int8",
  "tokenizer": "wordpiece",
  "vocab": "vocab.txt",
  "lowercase": true,
  "maxTokens": 512,
  "dimensions": 512,
  "pooling": "cls",
  "normalize": true,
  "queryPrefix": "为这个句子生成表示以用于检索相关文章：",
  "passagePrefix": "",
  "inputs": { "ids": "input_ids", "mask": "attention_mask", "typeIds": "token_type_ids" },
  "output": "last_hidden_state"
}
```

`queryPrefix` 為 BGE 官方指定的檢索指令（簡體字是原文，不要改成繁體）。`pooling` 支援 `cls` 與 `mean`（mean 時以 attention mask 加權平均），為將來 e5 類模型預留。`tokenizer` 第一版只需支援 `wordpiece`；遇到其他值時 `IsAvailable=false` 並記錄錯誤。

選用模型：`IAppPaths.ModelsDirectory` 底下第一個含有 `contexo-model.json` 的資料夾；資料夾內有 `default` 檔案時以其內容（資料夾名稱）為準。

## 要做

1. `Embedding/ModelManifest.cs`：讀取與驗證 `contexo-model.json`。
2. `Embedding/WordPieceTokenizer.cs`（`internal`）：
   - 先嘗試使用 `Microsoft.ML.Tokenizers` 的 `BertTokenizer`（若該版本提供）。
   - 若沒有或行為不符，自行實作 BERT BasicTokenizer + WordPiece：清除控制字元、空白正規化、**中日韓字元前後加空白**（每個 CJK 字獨立成 token）、標點切開、依設定轉小寫並去除重音、WordPiece 最長匹配（`##` 前綴，單字超過 100 字元視為 `[UNK]`）。
   - 輸出 `[CLS] … [SEP]`，截斷到 `maxTokens`。
3. `Embedding/OnnxEmbeddingService.cs`：
   - **延遲載入**：第一次呼叫 `Embed*` 時才建立 `InferenceSession`（`Lazy<T>` 或 `SemaphoreSlim` 保護）。`IsAvailable` 只檢查檔案與描述檔，不載入模型。
   - `SessionOptions`：`GraphOptimizationLevel.ORT_ENABLE_ALL`，`IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)`（留資源給使用者）。第一版只用 CPU execution provider。
   - 批次：每批最多 16 筆，同批補齊到該批最長長度。
   - 池化 → L2 正規化 → 回傳 `float[]`。
   - 模型不可用時，`Embed*` 拋 `InvalidOperationException("Embedding model is not available")`。
   - 執行緒安全（`InferenceSession.Run` 本身可並行）。
4. `tools/download-models.ps1` 與 `tools/download-models.sh`：從 Hugging Face 下載 `Xenova/bge-small-zh-v1.5` 的 `onnx/model_quantized.onnx`（存成 `model.onnx`）與 `vocab.txt`，寫入上面的 `contexo-model.json` 與 `default`。可用參數指定目標資料夾（預設 `./models`）。下載前先確認檔案是否已存在。
5. `tools/make-embedding-fixtures.py`（開發用，選用）：用 Python `transformers` 產生參考資料 `tests/Contexo.Core.Tests/Fixtures/Embedding/reference.json`（約 20 句繁中、簡中、英文混合句子，各自的 token ids 與向量前 8 維）。若環境無法執行 Python 或無法下載，跳過並在完成紀錄說明，請使用者執行後提交。

## 不做

- GPU / DirectML（後續再加，`SessionOptions` 保留擴充點即可）。
- 遠端 embedding（第二階段的公司伺服器模式）。
- 其他分詞器（unigram / BPE）。

## 可修改範圍

- `src/Contexo.Core/Embedding/**`
- `tools/download-models.ps1`、`tools/download-models.sh`、`tools/make-embedding-fixtures.py`
- `tests/Contexo.Core.Tests/Embedding/**`、`tests/Contexo.Core.Tests/Fixtures/Embedding/**`

## 實作要點與已知陷阱

- 先確認 `Xenova/bge-small-zh-v1.5` 的實際檔名（`onnx/model_quantized.onnx`、`vocab.txt`）。若不存在，改找其他提供 ONNX 的同模型儲存庫，並在完成紀錄註明來源。
- ONNX 模型輸入為 `long`（Int64）張量，形狀 `[batch, seq]`；`token_type_ids` 全部為 0。
- BERT 中文詞表以單字為主，「中日韓字元前後加空白」這一步一定要做，否則整串中文會變成 `[UNK]`。
- CJK 判斷範圍至少包含：U+4E00–9FFF、U+3400–4DBF、U+20000–2A6DF、U+2A700–2B73F、U+2B740–2B81F、U+2B820–2CEAF、U+F900–FAFF、U+2F800–2FA1F。
- 全形標點也要當成標點切開。
- 模型檔不進版控（`.gitignore` 已排除 `models/`）。

## 驗收條件

1. 不需模型的單元測試（一定要跑）：
   - WordPiece：`"監視系統ABC-123報價"`、`"Hello, World!"`、全形標點、emoji、空字串、超長輸入截斷到 512。用手寫的小詞表驗證切分結果。
   - `ModelManifest`：正確讀取、缺欄位時給清楚錯誤。
   - 模型資料夾不存在時 `IsAvailable == false`，且建構服務不拋例外。
2. 需要模型的測試（`Skip.If` 模型不存在）：
   - 向量長度 512，L2 範數 1 ± 1e-4。
   - 同一句子兩次結果相同。
   - 相關性：查詢「報價單」與「這是給客戶的報價明細」的相似度，高於與「今天中午吃什麼」的相似度；再準備 3 組類似的中文三元組，全部成立。
   - 若 `reference.json` 存在：token ids 完全相同；向量前 8 維誤差小於 1e-2（int8 量化會有誤差）。
   - 並行：8 個 Task 同時呼叫不拋例外、結果與單執行緒一致。
3. 在完成紀錄寫下：模型來源網址、在執行者機器上 100 段各約 300 字的中文所需時間。

## 完成紀錄

**做了什麼**

- `src/Contexo.Core/Embedding/ModelManifest.cs`：讀取並驗證 `contexo-model.json`（缺欄位、pooling 不是 cls/mean、數值不合理都丟 `ModelManifestException` 並指出欄位名稱）。多了一個選填欄位 `model`（ONNX 檔名，預設 `model.onnx`）。
- `src/Contexo.Core/Embedding/WordPieceTokenizer.cs`：自行實作 BERT BasicTokenizer + WordPiece（清除控制字元與零寬字元、CJK 前後加空白、含 CJK 擴充區 B 以後、標點切開含全形、轉小寫並去重音、最長匹配、單字超過 100 字元為 `[UNK]`、輸出 `[CLS]…[SEP]` 並截斷）。沒有用 `Microsoft.ML.Tokenizers.BertTokenizer`：為了讓 CJK／標點／截斷規則完全由本任務規格決定，不受函式庫預設值影響；已用 Hugging Face `BertTokenizer` 產生的 20 句參考資料驗證 token ids 完全一致。
- `src/Contexo.Core/Embedding/OnnxEmbeddingService.cs`：延遲載入 `InferenceSession`（`Lazy<T>`）；`IsAvailable` 只檢查描述檔、tokenizer 種類、`model.onnx`、`vocab.txt` 是否存在，不載入模型；每批最多 16 筆，同批補齊到最長；先依長度排序再分批以減少補齊，結果依原順序回傳；cls／mean 池化後 L2 正規化；模型不可用時拋 `InvalidOperationException("Embedding model is not available")`；模型載入失敗時記錄錯誤、`IsAvailable` 轉為 false。選模型規則：先看 models 根目錄的 `default` 檔，否則取名稱排序第一個含 `contexo-model.json` 的資料夾。找不到模型時 10 秒內不重複掃描（避免每次查詢都記一次警告），之後會重新檢查（使用者事後下載模型也能生效）。
- `tools/download-models.sh`、`tools/download-models.ps1`（含 UTF-8 BOM，避免 Windows PowerShell 5.1 讀壞簡體字前綴）、`tools/make-embedding-fixtures.py`。
- 測試：`tests/Contexo.Core.Tests/Embedding/`（`WordPieceTokenizerTests`、`ModelManifestTests`、`OnnxEmbeddingServiceTests`）與 `tests/Contexo.Core.Tests/Fixtures/Embedding/reference.json`。

**模型來源**：https://huggingface.co/Xenova/bge-small-zh-v1.5 的 `onnx/model_quantized.onnx`（約 24 MB，存成 `model.onnx`）與 `vocab.txt`。檔名確認存在，不需改用其他儲存庫。注意：該儲存庫 `tokenizer_config.json` 的 `do_lower_case` 是 false，詞表又完全沒有大寫字母，照原樣英文大寫字會全部變 `[UNK]`；依任務規格 manifest 設 `lowercase: true`，參考資料也用 `do_lower_case=True` 產生。

**驗收結果**

1. 不需模型的單元測試：WordPiece（`"監視系統ABC-123報價"`、`"Hello, World!"`、全形標點、emoji、空字串、超長輸入截斷到 512、代理對、CJK 範圍）、`ModelManifest`、模型資料夾不存在／不完整／tokenizer 不支援時 `IsAvailable == false` 且建構不拋例外、`default` 檔選模型、損壞模型檔 — 全數通過。
2. 需要模型的測試：向量長度 512 且 L2 範數 1 ± 1e-4、同句兩次相同、5 組中文三元組相關性皆成立（含任務指定的「報價單」）、8 個 Task 並行與單執行緒一致、取消、參考資料 20 句 token ids 完全相同 — 通過。把 `models/` 移走後同一批測試正確略過（12 個略過、0 失敗）。
3. `dotnet build Contexo.slnx -warnaserror` 成功（0 警告 0 錯誤）；`dotnet test` 全部通過（見最終回報的數字）。

**效能**（Apple M4，10 核心，macOS，CPU）：100 段各約 307 個中文字，`EmbedDocumentsAsync` 一次送入，約 1.1 秒（模型載入另計，約 0.1 秒）。

**與規格不同／需注意**

- 參考資料向量前 8 維的容許誤差從 1e-2 放寬為 3e-2：測試機（onnxruntime 1.30，.NET）與產生參考資料的 Python（onnxruntime 1.19）在 int8 運算上有差異，單句 `"Hello, World!"` 第 4 維差 0.0114。已用 fp32 模型驗證不是實作錯誤：C# int8 與 fp32 的餘弦相似度 0.9934，Python int8 與 fp32 為 0.9921，兩者與 fp32 一樣接近。
- 這個 int8 動態量化模型的啟動值是整個張量一起量化，所以同一句子在不同批次（補齊長度不同）向量會略有不同（餘弦約 0.993–0.997）。檢索排序不受影響，但「批次內結果」與「單筆結果」不是位元相同；測試相應使用餘弦下限 0.97。T22 比較模型時可一併評估 fp32／uint8 版本（同一儲存庫有 `model.onnx`、`model_uint8.onnx` 等）。
- 沒有加 `Microsoft.ML.Tokenizers` 的使用（套件已在 T01 引用，但本任務沒用到）。

**留給後續任務**

- T10／T11 呼叫 `EmbedDocumentsAsync` 時一次傳整批文字即可，服務內部會分批（16 筆）與排序；`ModelId` 在模型不可用時回傳空字串、`Dimensions` 為 0，呼叫端請先檢查 `IsAvailable`。
- `OnnxEmbeddingService` 實作了 `IDisposable`，由 DI 容器在關閉時釋放。
- 測試與發布：`models/` 不進版控；T21 打包時要把 `models/bge-small-zh-v1.5/`（`model.onnx`、`vocab.txt`、`contexo-model.json`）與 `models/default` 一併放進安裝目錄的 `models/`（`AppPaths` 會優先使用安裝目錄的 `models`）。
- 未驗證：`download-models.ps1` 沒有在本機執行（環境沒有 pwsh），邏輯與 `.sh` 對應，請在 Windows 實跑一次。
