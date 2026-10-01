param(
    [ValidateSet('cpu', 'cuda')] [string] $Backend = 'cuda',
    [ValidateRange(1, 10)] [int] $Rounds = 3,
    [string] $ServerPath,
    [switch] $Adaptive,
    [switch] $DecodeWindow,
    [string] $PythonPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $PythonPath) { $PythonPath = Join-Path $repo 'models/tabby/venv/Scripts/python.exe' }
if (-not (Test-Path -LiteralPath $PythonPath)) { throw 'Supply an existing Python executable. This probe installs nothing.' }
Push-Location $repo
try {
    & "$PSScriptRoot/build-model-probe.ps1" -Backend $Backend -Package echosub-worker -Vad -Offline
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
    & cargo build -p echosub-translation --locked --offline
    if ($LASTEXITCODE -ne 0) { throw 'HTTP warmup probe build failed' }
    $arguments = @((Join-Path $repo 'scripts/probe-paced-translation.py'), '--backend', $Backend, '--rounds', "$Rounds")
    if ($Adaptive) { $arguments += '--adaptive' }
    if ($DecodeWindow) { $arguments += '--decode-window' }
    if ($ServerPath) { $arguments += @('--server', $ServerPath) }
    & $PythonPath @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Paced translation measurement failed; completed reports/logs retained.' }
} finally { Pop-Location }
