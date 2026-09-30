param([switch] $Offline, [switch] $NoBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$oldTarget = $env:CARGO_TARGET_DIR
$env:CARGO_TARGET_DIR = Join-Path $repo 'target/model-probe-cpu'
$env:NUGET_PACKAGES = Join-Path $repo '.nuget/packages'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
Push-Location $repo
try {
    if (-not $NoBuild) { & "$PSScriptRoot/build-model-probe.ps1" -Backend cpu -Package echosub-worker -Vad -Offline:$Offline }
    $project = Join-Path $repo 'tests/EchoSub.NativeAsrSmoke/EchoSub.NativeAsrSmoke.csproj'
    & dotnet build $project
    if ($LASTEXITCODE -ne 0) { throw 'VAD smoke build failed' }
    $catalogue = Get-Content -LiteralPath 'benchmarks/model-downloads.json' -Raw | ConvertFrom-Json
    $model = $catalogue.models | Where-Object { $_.id -eq 'whisper-base' }
    $output = Join-Path $repo ('benchmarks/results/worker-vad-cpu-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    & dotnet run --project $project --no-build -- (Join-Path $env:CARGO_TARGET_DIR 'release/echosub-worker.exe') ([IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'benchmarks') $model.path))) $model.sha256 (Join-Path $repo 'benchmarks/fixtures/local-tts/manifest.json') cpu (Join-Path $output 'report.json') (Join-Path $repo 'benchmarks/vad-assets.json')
    if ($LASTEXITCODE -ne 0) { throw 'Native worker VAD smoke failed' }
    Write-Host "Report: $output/report.json"
}
finally { Pop-Location; $env:CARGO_TARGET_DIR = $oldTarget }
