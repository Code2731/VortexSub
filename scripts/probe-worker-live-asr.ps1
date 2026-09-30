param([switch] $Offline, [switch] $NoBuild, [switch] $Sessions, [switch] $Partials, [switch] $Boundaries)
$ErrorActionPreference = 'Stop'
if (([int]$Sessions.IsPresent + [int]$Partials.IsPresent + [int]$Boundaries.IsPresent) -gt 1) { throw 'Choose Sessions, Partials or Boundaries' }
$repo = Split-Path $PSScriptRoot -Parent
$oldTarget = $env:CARGO_TARGET_DIR
$env:CARGO_TARGET_DIR = Join-Path $repo 'target/model-probe-cpu'
$env:NUGET_PACKAGES = Join-Path $repo '.nuget/packages'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
Push-Location $repo
try {
    if (-not $NoBuild) { & "$PSScriptRoot/build-model-probe.ps1" -Backend cpu -Package echosub-worker -Vad -Offline:$Offline }
    $project = Join-Path $repo 'tests/EchoSub.CaptureSmoke/EchoSub.CaptureSmoke.csproj'
    & dotnet build $project
    if ($LASTEXITCODE -ne 0) { throw 'Live ASR smoke build failed' }
    $catalogue = Get-Content -LiteralPath 'benchmarks/model-downloads.json' -Raw | ConvertFrom-Json
    $model = $catalogue.models | Where-Object { $_.id -eq 'whisper-base' }
    $output = Join-Path $repo ('benchmarks/results/worker-live-asr-cpu-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $probeArgs = @((Join-Path $env:CARGO_TARGET_DIR 'release/echosub-worker.exe'), ([IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'benchmarks') $model.path))), $model.sha256, (Join-Path $repo 'benchmarks/fixtures/local-tts/en-01.wav'), (Join-Path $output 'report.json'), (Join-Path $repo 'benchmarks/vad-assets.json'))
    if ($Sessions) { $probeArgs = @('--sessions') + $probeArgs }
    if ($Partials) { $probeArgs[3] = Join-Path $repo 'benchmarks/fixtures/local-tts/en-10.wav'; $probeArgs = @('--partials') + $probeArgs }
    if ($Boundaries) { $probeArgs[3] = Join-Path $repo 'benchmarks/fixtures/local-tts/en-10.wav'; $probeArgs = @('--boundaries') + $probeArgs }
    & dotnet run --project $project --no-build -- @probeArgs
    if ($LASTEXITCODE -ne 0) { throw 'Live worker ASR smoke failed' }
    Write-Host "Report: $output/report.json"
}
finally { Pop-Location; $env:CARGO_TARGET_DIR = $oldTarget }
