param(
    [ValidateSet('cpu', 'cuda')] [string] $Backend = 'cpu',
    [string] $Fixtures = 'benchmarks/fixtures/local-tts/manifest.json',
    [switch] $Offline,
    [switch] $NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$oldTarget = $env:CARGO_TARGET_DIR
$env:CARGO_TARGET_DIR = Join-Path $repo "target/model-probe-$Backend"
$env:NUGET_PACKAGES = Join-Path $repo '.nuget/packages'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
Push-Location $repo
try {
    if (-not $NoBuild) {
        & "$PSScriptRoot/build-model-probe.ps1" -Backend $Backend -Package echosub-worker -Offline:$Offline
    }
    $project = Join-Path $repo 'tests/EchoSub.NativeAsrSmoke/EchoSub.NativeAsrSmoke.csproj'
    & dotnet build $project
    if ($LASTEXITCODE -ne 0) { throw 'Native worker smoke build failed' }
    $catalogue = Get-Content -LiteralPath 'benchmarks/model-downloads.json' -Raw | ConvertFrom-Json
    $model = $catalogue.models | Where-Object { $_.id -eq 'whisper-base' }
    if (-not $model) { throw 'Whisper base catalogue entry is missing' }
    $modelPath = [IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'benchmarks') $model.path))
    $output = Join-Path $repo ('benchmarks/results/worker-asr-' + $Backend + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $worker = Join-Path $env:CARGO_TARGET_DIR 'release/echosub-worker.exe'
    $report = Join-Path $output 'report.json'
    & dotnet run --project $project --no-build -- $worker $modelPath $model.sha256 (Resolve-Path -LiteralPath $Fixtures).Path $Backend $report
    if ($LASTEXITCODE -ne 0) { throw 'Native worker ASR smoke failed' }
    Write-Host "Report: $report (durations in seconds; file fixtures, no capture/VAD/translation)"
}
finally {
    Pop-Location
    $env:CARGO_TARGET_DIR = $oldTarget
}
