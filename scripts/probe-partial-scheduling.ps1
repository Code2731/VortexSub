param(
    [Parameter(Mandatory = $true)] [string] $WavPath,
    [string] $ModelPath,
    [string] $ReportPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $ModelPath) { $ModelPath = Join-Path $repo 'models/ggml-base.bin' }
if (-not $ReportPath) {
    $results = Join-Path $repo ('benchmarks/results/partial-scheduling-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID)
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    $ReportPath = Join-Path $results 'report.json'
}
$oldModel = $env:ECHOSUB_SCHEDULE_MODEL
$oldWav = $env:ECHOSUB_SCHEDULE_WAV
$oldReport = $env:ECHOSUB_SCHEDULE_REPORT
Push-Location $repo
try {
    $env:ECHOSUB_SCHEDULE_MODEL = (Resolve-Path -LiteralPath $ModelPath).Path
    $env:ECHOSUB_SCHEDULE_WAV = (Resolve-Path -LiteralPath $WavPath).Path
    $env:ECHOSUB_SCHEDULE_REPORT = [IO.Path]::GetFullPath($ReportPath)
    & "$PSScriptRoot/build-model-probe.ps1" -Backend cpu -Package echosub-worker -Vad -Offline
    if ($LASTEXITCODE -ne 0) { throw 'Native worker build failed' }
    # Only this explicit, ignored measurement runs. It installs/downloads nothing.
    & cargo test -p echosub-worker --release --locked --offline --features native-asr,native-vad --target-dir (Join-Path $repo 'target/model-probe-cpu') native_paced_partial_probe -- --ignored --nocapture --test-threads=1
    if ($LASTEXITCODE -ne 0) { throw 'Paced native scheduler comparison failed' }
    Write-Host "Scheduler report: $($env:ECHOSUB_SCHEDULE_REPORT)"
} finally {
    $env:ECHOSUB_SCHEDULE_MODEL = $oldModel
    $env:ECHOSUB_SCHEDULE_WAV = $oldWav
    $env:ECHOSUB_SCHEDULE_REPORT = $oldReport
    Pop-Location
}
