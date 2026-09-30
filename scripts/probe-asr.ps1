param(
    [ValidateSet('cpu', 'cuda')] [string] $Backend = 'cpu',
    [string] $Fixtures = 'benchmarks/fixtures/local-tts/manifest.json',
    [ValidateRange(1, 64)] [int] $Threads = 8,
    [switch] $Offline,
    [switch] $NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$oldTarget = $env:CARGO_TARGET_DIR
# Separate binaries let CPU and CUDA measurements coexist without executable locks.
$env:CARGO_TARGET_DIR = Join-Path $repo "target/model-probe-$Backend"
Push-Location $repo
try {
    if (-not $NoBuild) { & "$PSScriptRoot/build-model-probe.ps1" -Backend $Backend -Offline:$Offline }
    $binary = Join-Path $env:CARGO_TARGET_DIR 'release/echosub-model-probe.exe'
    if (-not (Test-Path -LiteralPath $binary)) { throw "Probe binary missing: $binary" }
    $output = Join-Path $repo ('benchmarks/results/asr-' + $Backend + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $report = Join-Path $output 'report.json'
    $nativeLog = Join-Path $output 'native.log'
    [ordered]@{binary_sha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant();backend_requested=$Backend;threads=$Threads;fixture_manifest=(Resolve-Path -LiteralPath $Fixtures).Path;command="asr $Fixtures benchmarks/model-downloads.json $report $Threads $Backend";started_utc=[DateTime]::UtcNow.ToString('o')} |
        ConvertTo-Json | Set-Content (Join-Path $output 'runtime.json') -Encoding UTF8
    Write-Host "Measuring base and small ($Backend); progress: $nativeLog"
    & $binary asr $Fixtures benchmarks/model-downloads.json $report $Threads $Backend > $nativeLog 2>&1
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $nativeLog -Tail 30; throw 'ASR probe failed; native log retained' }
    Write-Host "Report: $report; inspect native.log to verify actual backend"
}
finally {
    Pop-Location
    $env:CARGO_TARGET_DIR = $oldTarget
}
