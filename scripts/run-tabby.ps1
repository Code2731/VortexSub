param([switch] $NoBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$python = Join-Path $repo 'models/tabby/venv/Scripts/python.exe'
if (-not (Test-Path -LiteralPath $python)) { throw 'Consented Tabby runtime is missing. See docs/TRANSLATION_ENGINES.md.' }
$arguments = @((Join-Path $repo 'scripts/run-tabby.py'))
if ($NoBuild) { $arguments += '--no-build' }
& $python @arguments
exit $LASTEXITCODE
