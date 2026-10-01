param([string] $ServerPath, [string] $PythonPath, [switch] $NoBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
if (-not $PythonPath) { $PythonPath = Join-Path $repo 'models/tabby/venv/Scripts/python.exe' }
if (-not (Test-Path -LiteralPath $PythonPath)) { throw 'Supply -PythonPath with an existing Python 3.12 executable. This probe installs nothing.' }
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { $env:PATH = (Join-Path $env:USERPROFILE '.cargo/bin') + ';' + $env:PATH }
Push-Location $repo
try {
    if (-not $NoBuild) {
        cargo build -p echosub-worker -p echosub-translation --locked --offline
        if ($LASTEXITCODE -ne 0) { throw 'Worker/HTTP probe build failed' }
        & "$PSScriptRoot/build-model-probe.ps1" -Backend cpu -Package echosub-worker -Vad -Offline
        if ($LASTEXITCODE -ne 0) { throw 'Native prefix worker build failed' }
        dotnet build tests/EchoSub.NativeAsrSmoke/EchoSub.NativeAsrSmoke.csproj --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Replay client build failed' }
    }
    $arguments = @((Join-Path $repo 'scripts/probe-streaming-translation.py'))
    if ($ServerPath) { $arguments += @('--server', $ServerPath) }
    & $PythonPath @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Streaming experiment failed; partial reports retained under benchmarks/results.' }
} finally { Pop-Location }
