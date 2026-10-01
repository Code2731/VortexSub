param(
    [string] $PythonPath = 'models/tabby/venv/Scripts/python.exe',
    [string] $Fixtures = 'benchmarks/fixtures/local-tts/manifest.json',
    [ValidateRange(1, 20)] [int] $Rounds = 3,
    [ValidateRange(1, 64)] [int] $Threads = 8,
    [string] $CudaArchitecture,
    [switch] $NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$previousPath = $env:PATH
$previousArchitecture = $env:CMAKE_CUDA_ARCHITECTURES
Push-Location $repo
try {
    $python = (Resolve-Path -LiteralPath $PythonPath).Path
    $fixturesPath = (Resolve-Path -LiteralPath $Fixtures).Path
    $output = Join-Path $repo ('benchmarks/results/asr-candidates-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID)
    New-Item -ItemType Directory -Path $output | Out-Null
    $senseReport = Join-Path $output 'sensevoice.json'
    $whisperReport = Join-Path $output 'whisper.json'
    # No setup/download here: assets require prior explicit consent and installation.
    & $python -X utf8 "$PSScriptRoot/probe-sensevoice.py" --fixtures $fixturesPath --report $senseReport --rounds $Rounds --threads $Threads
    if ($LASTEXITCODE -ne 0) { throw 'SenseVoice comparison failed' }
    if ($CudaArchitecture) { $env:CMAKE_CUDA_ARCHITECTURES = $CudaArchitecture }
    if (-not $NoBuild) { & "$PSScriptRoot/build-model-probe.ps1" -Backend cuda -Offline }
    $binary = Join-Path $repo 'target/model-probe-cuda/release/echosub-model-probe.exe'
    $model = (Get-Content -LiteralPath 'benchmarks/model-downloads.json' -Raw | ConvertFrom-Json).models | Where-Object id -eq 'whisper-base'
    if (-not $model) { throw 'Whisper base manifest entry missing' }
    if ($env:CUDA_PATH) { $env:PATH = (Join-Path $env:CUDA_PATH 'bin') + ';' + $env:PATH }
    & $binary prefix $fixturesPath (Join-Path $repo 'models/ggml-base.bin') $model.sha256 $whisperReport $Threads cuda $Rounds > (Join-Path $output 'whisper-native.log') 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Whisper comparison failed; see $output/whisper-native.log" }
    & $python -X utf8 "$PSScriptRoot/summarize-asr-candidates.py" $senseReport $whisperReport --output (Join-Path $output 'summary.json')
    if ($LASTEXITCODE -ne 0) { throw 'Candidate summary failed' }
    [ordered]@{binary_sha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant();native_profile=(Get-Content -LiteralPath 'target/model-probe-cuda/echosub-native-profile.json' -Raw | ConvertFrom-Json);execution_order='SenseVoice CPU all rounds, then Whisper CUDA all rounds';threads=$Threads;rounds=$Rounds;python=$python;completed_utc=[DateTime]::UtcNow.ToString('o')} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'runtime.json') -Encoding UTF8
    Write-Host "Candidate comparison: $output"
}
finally {
    $env:PATH = $previousPath
    $env:CMAKE_CUDA_ARCHITECTURES = $previousArchitecture
    Pop-Location
}
