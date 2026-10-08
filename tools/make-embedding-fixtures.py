#!/usr/bin/env python3
"""產生 embedding 參考資料（開發用，選用）。

用 Hugging Face 的 BertTokenizer 與 onnxruntime 對一批固定句子算出 token ids 與向量前 8 維，
寫到 tests/Contexo.Core.Tests/Fixtures/Embedding/reference.json。
C# 端的測試會拿它比對自己的分詞器與推論結果。

需要：pip install transformers onnxruntime numpy
先執行 tools/download-models.sh（或 .ps1）下載模型。

用法：python tools/make-embedding-fixtures.py [--models ./models] [--model-name bge-small-zh-v1.5]
"""
import argparse
import json
import os
from pathlib import Path

import numpy as np
import onnxruntime as ort
from transformers import BertTokenizer

# (文字, 模式) 模式為 passage 或 query；query 會加上模型的 queryPrefix。
SENTENCES = [
    ("監視系統ABC-123報價", "passage"),
    ("Hello, World!", "passage"),
    ("這是給客戶的報價明細，含稅總額為新台幣１２０，０００元。", "passage"),
    ("今天中午吃什麼？", "passage"),
    ("报价单", "query"),
    ("報價單", "query"),
    ("请问这份合同的付款条件是什么？", "passage"),
    ("The quarterly sales report shows a 12% increase over last year.", "passage"),
    ("Café résumé naïve façade", "passage"),
    ("第3季營收成長 12.5%（較去年同期）", "passage"),
    ("供應商：台灣積體電路製造股份有限公司", "passage"),
    ("Contexo 會讀取您的 Word、Excel 與 PDF 檔案。", "passage"),
    ("會議紀錄：2025/03/14 專案進度與風險", "passage"),
    ("使用者手冊 第二章 安裝與設定", "query"),
    ("日本語のテキストも混ざっています", "passage"),
    ("emoji 😀 should be unknown", "passage"),
    ("   多餘   空白　與全形空白  ", "passage"),
    ("SELECT * FROM orders WHERE total > 1000;", "passage"),
    ("退貨流程與保固條款", "query"),
    ("", "passage"),
]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--models", default="./models")
    parser.add_argument("--model-name", default="bge-small-zh-v1.5")
    parser.add_argument("--output", default="tests/Contexo.Core.Tests/Fixtures/Embedding/reference.json")
    args = parser.parse_args()

    model_dir = Path(args.models) / args.model_name
    manifest = json.loads((model_dir / "contexo-model.json").read_text(encoding="utf-8"))

    tokenizer = BertTokenizer(
        vocab_file=str(model_dir / manifest["vocab"]),
        do_lower_case=manifest["lowercase"],
        tokenize_chinese_chars=True,
    )
    session = ort.InferenceSession(str(model_dir / "model.onnx"), providers=["CPUExecutionProvider"])
    input_names = {i.name for i in session.get_inputs()}

    cases = []
    for text, mode in SENTENCES:
        prefix = manifest["queryPrefix"] if mode == "query" else manifest["passagePrefix"]
        encoded = tokenizer(prefix + text, truncation=True, max_length=manifest["maxTokens"], return_tensors="np")
        feeds = {
            manifest["inputs"]["ids"]: encoded["input_ids"].astype(np.int64),
            manifest["inputs"]["mask"]: encoded["attention_mask"].astype(np.int64),
        }
        type_name = manifest["inputs"].get("typeIds")
        if type_name and type_name in input_names:
            feeds[type_name] = np.zeros_like(encoded["input_ids"], dtype=np.int64)
        hidden = session.run([manifest["output"]], feeds)[0]
        if manifest["pooling"] == "cls":
            vector = hidden[0, 0]
        else:
            m = encoded["attention_mask"][0][:, None].astype(np.float32)
            vector = (hidden[0] * m).sum(axis=0) / m.sum()
        if manifest["normalize"]:
            vector = vector / np.linalg.norm(vector)
        cases.append(
            {
                "text": text,
                "mode": mode,
                "ids": [int(i) for i in encoded["input_ids"][0]],
                "vector8": [round(float(v), 6) for v in vector[:8]],
            }
        )

    output = {"modelId": manifest["id"], "cases": cases}
    out_path = Path(args.output)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {out_path} ({len(cases)} cases)")


if __name__ == "__main__":
    main()
