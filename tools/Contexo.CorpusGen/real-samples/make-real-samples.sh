#!/usr/bin/env bash
# 在 macOS 上用「真實軟體」產生中文範例檔，檢查 Contexo 的解析器處理真實輸出的狀況。
# 這不是自己組出來的檔案：docx 由 macOS 內建的 textutil 寫出，PDF 由 Google Chrome 列印（內嵌系統中文字型）。
# 產生的檔案不進版控（*.pdf、*.docx 已在 .gitignore）。
#
# 用法：bash tools/Contexo.CorpusGen/real-samples/make-real-samples.sh <輸出資料夾>
# 然後：dotnet run --project tools/Contexo.CorpusGen -- parse <輸出資料夾>/*.pdf <輸出資料夾>/*.docx
set -euo pipefail

OUT="${1:?請指定輸出資料夾}"
HERE="$(cd "$(dirname "$0")" && pwd)"
CHROME="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
mkdir -p "$OUT"

textutil -convert docx "$HERE/regulation.html" -output "$OUT/textutil-規章.docx"

if [ -x "$CHROME" ]; then
    "$CHROME" --headless=new --disable-gpu --print-to-pdf="$OUT/chrome-規章.pdf" "file://$HERE/regulation.html" >/dev/null 2>&1
    "$CHROME" --headless=new --disable-gpu --print-to-pdf="$OUT/chrome-手冊.pdf" "file://$HERE/handbook.html" >/dev/null 2>&1
else
    echo "找不到 Google Chrome，略過 PDF。" >&2
fi

ls -la "$OUT"
