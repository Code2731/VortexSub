param([ValidateRange(1,10)] [int] $Rounds = 3, [ValidateRange(1,5)] [int] $Warmup = 1,
    [ValidateSet('llama-first','tabby-first')] [string] $Order = 'llama-first', [string] $ServerPath, [switch] $Worker,
    [ValidateSet('current','untruncated')] [string] $SamplingProfile = 'current')
$ErrorActionPreference = 'Stop'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$repo = Split-Path $PSScriptRoot -Parent
$python = Join-Path $repo 'models/tabby/venv/Scripts/python.exe'
if (-not (Test-Path -LiteralPath $python)) { throw 'Install the consented Tabby environment first. See docs/TRANSLATION_ENGINES.md.' }
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { $env:PATH = (Join-Path $env:USERPROFILE '.cargo/bin') + ';' + $env:PATH }
Push-Location $repo
try {
    cargo build -p echosub-translation --bin translation-probe --locked --offline
    if ($LASTEXITCODE -ne 0) { throw 'Translation adapter probe build failed' }
    if ($Worker) {
        & (Join-Path $repo 'scripts/build-model-probe.ps1') -Backend cpu -Package echosub-worker -Vad -Offline
        if ($LASTEXITCODE -ne 0) { throw 'Native worker build failed' }
        dotnet build (Join-Path $repo 'tests/EchoSub.NativeAsrSmoke/EchoSub.NativeAsrSmoke.csproj') --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Native file smoke client build failed' }
    }
    $arguments = @((Join-Path $repo 'scripts/compare-translation-engines.py'), '--rounds', "$Rounds", '--warmup', "$Warmup", '--order', $Order, '--sampling-profile', $SamplingProfile)
    if ($ServerPath) { $arguments += @('--llama-server', $ServerPath) }
    if ($Worker) { $arguments += '--worker' }
    & $python @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Engine comparison failed; partial reports are retained under benchmarks/results.' }
} finally { Pop-Location }
