#!/usr/bin/env bash
# 下載 Contexo 預設的 embedding 模型（bge-small-zh-v1.5 int8）。
# 用法：bash tools/download-models.sh [目標資料夾，預設 ./models]
# 來源：https://huggingface.co/Xenova/bge-small-zh-v1.5（MIT 授權）
set -euo pipefail

TARGET_ROOT="${1:-./models}"
MODEL_NAME="bge-small-zh-v1.5"
BASE_URL="https://huggingface.co/Xenova/bge-small-zh-v1.5/resolve/main"
MODEL_DIR="$TARGET_ROOT/$MODEL_NAME"

mkdir -p "$MODEL_DIR"

download() {
    local url="$1" dest="$2"
    if [ -s "$dest" ]; then
        echo "已存在，略過：$dest"
        return
    fi
    echo "下載 $url"
    curl --fail --location --silent --show-error --retry 3 -o "$dest.part" "$url"
    mv "$dest.part" "$dest"
}

download "$BASE_URL/onnx/model_quantized.onnx" "$MODEL_DIR/model.onnx"
download "$BASE_URL/vocab.txt" "$MODEL_DIR/vocab.txt"

MANIFEST="$MODEL_DIR/contexo-model.json"
if [ ! -s "$MANIFEST" ]; then
    cat > "$MANIFEST" <<'JSON'
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
JSON
fi

DEFAULT_FILE="$TARGET_ROOT/default"
if [ ! -s "$DEFAULT_FILE" ]; then
    printf '%s' "$MODEL_NAME" > "$DEFAULT_FILE"
fi

echo "完成：$MODEL_DIR"
