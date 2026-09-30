param(
    [ValidateSet('cpu', 'cuda')] [string] $Backend = 'cpu',
    [string] $Fixtures = 'benchmarks/fixtures/local-tts/manifest.json',
    [ValidateRange(1, 64)] [int] $Threads = 8,
    [ValidateRange(10, 100)] [int] $Iterations = 10,
    [switch] $Offline,
    [switch] $NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$oldTarget = $env:CARGO_TARGET_DIR
$env:CARGO_TARGET_DIR = Join-Path $repo "target/model-probe-$Backend"
Push-Location $repo
try {
    if (-not $NoBuild) { & "$PSScriptRoot/build-model-probe.ps1" -Backend $Backend -Offline:$Offline }
    $binary = Join-Path $env:CARGO_TARGET_DIR 'release/echosub-model-probe.exe'
    if (-not (Test-Path -LiteralPath $binary)) { throw "Probe missing: $binary" }
    $runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $output = Join-Path $repo ('benchmarks/results/cancellation-' + $Backend + '-' + $runId)
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $report = Join-Path $output 'report.json'
    $log = Join-Path $output 'native.log'
    [ordered]@{binary_sha256=(Get-FileHash -LiteralPath $binary).Hash.ToLowerInvariant();backend_requested=$Backend;threads=$Threads;iterations=$Iterations;fixture_manifest=(Resolve-Path -LiteralPath $Fixtures).Path;started_utc=[DateTime]::UtcNow.ToString('o');command="cancel $Fixtures benchmarks/model-downloads.json $report $Threads $Backend $Iterations"} |
        ConvertTo-Json | Set-Content (Join-Path $output 'runtime.json') -Encoding UTF8
    Write-Host "Actual cancellation/recovery/shutdown ($Backend); all durations in seconds. Progress: $log"
    & $binary cancel $Fixtures benchmarks/model-downloads.json $report $Threads $Backend $Iterations > $log 2>&1
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $log -Tail 30; throw "Cancellation probe failed; evidence retained at $output" }
    Write-Host "Report: $report; inspect native.log for actual backend. This is not a capture Stop measurement."
}
finally {
    Pop-Location
    $env:CARGO_TARGET_DIR = $oldTarget
}
