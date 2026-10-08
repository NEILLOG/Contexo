<#
.SYNOPSIS
  下載 Contexo 預設的 embedding 模型（bge-small-zh-v1.5 int8）。
.DESCRIPTION
  來源：https://huggingface.co/Xenova/bge-small-zh-v1.5（MIT 授權）
  用法：pwsh tools/download-models.ps1 [-TargetRoot <資料夾>]
#>
param(
    [string]$TargetRoot = "./models"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$modelName = "bge-small-zh-v1.5"
$baseUrl = "https://huggingface.co/Xenova/bge-small-zh-v1.5/resolve/main"
$modelDir = Join-Path $TargetRoot $modelName
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

New-Item -ItemType Directory -Force -Path $modelDir | Out-Null

function Get-ModelFile([string]$Url, [string]$Destination) {
    if ((Test-Path $Destination) -and ((Get-Item $Destination).Length -gt 0)) {
        Write-Host "已存在，略過：$Destination"
        return
    }
    Write-Host "下載 $Url"
    $part = "$Destination.part"
    Invoke-WebRequest -Uri $Url -OutFile $part -MaximumRetryCount 3 -RetryIntervalSec 2
    Move-Item -Force -Path $part -Destination $Destination
}

Get-ModelFile "$baseUrl/onnx/model_quantized.onnx" (Join-Path $modelDir "model.onnx")
Get-ModelFile "$baseUrl/vocab.txt" (Join-Path $modelDir "vocab.txt")

$manifestPath = Join-Path $modelDir "contexo-model.json"
if (-not (Test-Path $manifestPath) -or ((Get-Item $manifestPath).Length -eq 0)) {
    $manifest = @'
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
'@
    [System.IO.File]::WriteAllText($manifestPath, $manifest, $utf8NoBom)
}

$defaultPath = Join-Path $TargetRoot "default"
if (-not (Test-Path $defaultPath) -or ((Get-Item $defaultPath).Length -eq 0)) {
    [System.IO.File]::WriteAllText($defaultPath, $modelName, $utf8NoBom)
}

Write-Host "完成：$modelDir"
