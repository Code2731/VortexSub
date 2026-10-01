param(
    [ValidateRange(1, 10)] [int] $Rounds = 3,
    [ValidateSet('cuda', 'cpu')] [string] $Device = 'cuda'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$python = Join-Path $repo 'models/tabby/venv/Scripts/python.exe'
if (-not (Test-Path -LiteralPath $python)) { throw 'Existing diagnostic Python runtime is required' }
& $python -X utf8 "$PSScriptRoot/probe-laya-translation.py" --rounds $Rounds --device $Device
if ($LASTEXITCODE -ne 0) { throw 'Laya diagnostic failed; inspect benchmarks/results/laya-translation-*' }
