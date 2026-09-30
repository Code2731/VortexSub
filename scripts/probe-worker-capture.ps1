param([switch] $Offline, [switch] $NoBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { $env:PATH = (Join-Path $env:USERPROFILE '.cargo/bin') + ';' + $env:PATH }
$env:NUGET_PACKAGES = Join-Path $repo '.nuget/packages'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
Push-Location $repo
try {
    if (-not $NoBuild) {
        $arguments = @('build','-p','echosub-worker','--release','--locked','--target-dir',(Join-Path $repo 'target'))
        if ($Offline -or $env:ECHOSUB_OFFLINE -eq '1') { $arguments += '--offline' }
        & cargo @arguments
        if ($LASTEXITCODE -ne 0) { throw 'Capture worker build failed' }
    }
    $project = Join-Path $repo 'tests/EchoSub.CaptureSmoke/EchoSub.CaptureSmoke.csproj'
    & dotnet build $project
    if ($LASTEXITCODE -ne 0) { throw 'Capture smoke build failed' }
    $output = Join-Path $repo ('benchmarks/results/worker-capture-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    & dotnet run --project $project --no-build -- (Join-Path $repo 'target/release/echosub-worker.exe') (Join-Path $repo 'benchmarks/fixtures/local-tts/en-01.wav') (Join-Path $output 'report.json')
    if ($LASTEXITCODE -ne 0) { throw 'Worker capture smoke failed' }
    Write-Host "Report: $output/report.json"
}
finally { Pop-Location }
