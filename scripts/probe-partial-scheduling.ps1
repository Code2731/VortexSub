param(
    [Parameter(Mandatory = $true)] [string] $WavPath,
    [string] $ModelPath,
    [string] $ReportPath,
    [ValidateSet('cpu', 'cuda')] [string] $Backend = 'cpu',
    [ValidateRange(1, 10)] [int] $Rounds = 1,
    [ValidateRange(0.8, 2.0)] [double] $FirstPartialSeconds = 0.8,
    [switch] $CurrentOnly,
    [string] $TranslationEndpoint,
    [string] $TranslationModel,
    [switch] $Trim
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
$probeEnvNames = @('ECHOSUB_SCHEDULE_BACKEND','ECHOSUB_SCHEDULE_ROUNDS','ECHOSUB_SCHEDULE_FIRST','ECHOSUB_SCHEDULE_CURRENT','ECHOSUB_SCHEDULE_ENDPOINT','ECHOSUB_SCHEDULE_TRANSLATION_MODEL')
$oldProbeEnv = @{}
foreach ($probeEnvName in $probeEnvNames) { $oldProbeEnv[$probeEnvName] = [Environment]::GetEnvironmentVariable($probeEnvName) }
if ($TranslationEndpoint -and ($Trim -or -not $TranslationModel)) { throw 'Translation comparison requires -TranslationModel and cannot use -Trim' }
Push-Location $repo
try {
    $env:ECHOSUB_SCHEDULE_MODEL = (Resolve-Path -LiteralPath $ModelPath).Path
    $env:ECHOSUB_SCHEDULE_WAV = (Resolve-Path -LiteralPath $WavPath).Path
    $env:ECHOSUB_SCHEDULE_REPORT = [IO.Path]::GetFullPath($ReportPath)
    $env:ECHOSUB_SCHEDULE_BACKEND = $Backend
    $env:ECHOSUB_SCHEDULE_ROUNDS = "$Rounds"
    $env:ECHOSUB_SCHEDULE_FIRST = $FirstPartialSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:ECHOSUB_SCHEDULE_CURRENT = if ($CurrentOnly) { '1' } else { '0' }
    $env:ECHOSUB_SCHEDULE_ENDPOINT = $TranslationEndpoint
    $env:ECHOSUB_SCHEDULE_TRANSLATION_MODEL = $TranslationModel
    & "$PSScriptRoot/build-model-probe.ps1" -Backend $Backend -Package echosub-worker -Vad -Offline
    if ($LASTEXITCODE -ne 0) { throw 'Native worker build failed' }
    # Only this explicit, ignored measurement runs. It installs/downloads nothing.
    $probeName = if ($TranslationEndpoint) { 'native_paced_translation_probe' } elseif ($Trim) { 'native_timed_trim_probe' } else { 'native_paced_partial_probe' }
    $probeFeatures = if ($Backend -eq 'cuda') { 'cuda,native-vad' } else { 'native-asr,native-vad' }
    & cargo test -p echosub-worker --release --locked --offline --features $probeFeatures --target-dir (Join-Path $repo "target/model-probe-$Backend") $probeName -- --ignored --nocapture --test-threads=1
    if ($LASTEXITCODE -ne 0) { throw "Native file probe failed: $probeName" }
    Write-Host "Native file report: $($env:ECHOSUB_SCHEDULE_REPORT)"
} finally {
    $env:ECHOSUB_SCHEDULE_MODEL = $oldModel
    $env:ECHOSUB_SCHEDULE_WAV = $oldWav
    $env:ECHOSUB_SCHEDULE_REPORT = $oldReport
    foreach ($probeEnvName in $probeEnvNames) { [Environment]::SetEnvironmentVariable($probeEnvName, $oldProbeEnv[$probeEnvName]) }
    Pop-Location
}
