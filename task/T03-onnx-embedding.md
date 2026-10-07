# T03 本機 ONNX Embedding

- **狀態**：待辦
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

（由執行者填寫）
