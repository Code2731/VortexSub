param([switch] $Offline, [switch] $NoBuild, [ValidateRange(1,20)][int] $Rounds = 2, [string[]] $DeviceId)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { $env:PATH = (Join-Path $env:USERPROFILE '.cargo/bin') + ';' + $env:PATH }
$env:NUGET_PACKAGES = Join-Path $repo '.nuget/packages'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
Push-Location $repo
try {
    if (-not $NoBuild) {
        $buildArgs = @('build','-p','echosub-worker','-p','echosub-capture-windows','--release','--locked','--target-dir',(Join-Path $repo 'target'))
        if ($Offline -or $env:ECHOSUB_OFFLINE -eq '1') { $buildArgs += '--offline' }
        & cargo @buildArgs
        if ($LASTEXITCODE -ne 0) { throw 'Startup probe build failed' }
    }
    $inventory = & (Join-Path $repo 'target/release/echosub-capture-windows.exe') --list
    if ($LASTEXITCODE -ne 0) { throw 'Render endpoint enumeration failed' }
    $endpoints = @(@{ Id = $null; Name = 'console-default' })
    foreach ($line in $inventory) {
        if ($line -match '^(?:default|render)\s+(\S+) name=(.+)$') {
            $endpoints += @{ Id = $Matches[1]; Name = $Matches[2] }
        }
    }
    if ($DeviceId) {
        $endpoints = @($endpoints | Where-Object { $_.Id -in $DeviceId -or ($_.Id -eq $null -and 'default' -in $DeviceId) })
        if ($endpoints.Count -ne ($DeviceId | Select-Object -Unique).Count) { throw 'Use active endpoint IDs or default; requested endpoint was not found' }
    }
    if ($endpoints.Count -lt 2 -and -not $DeviceId) { throw 'No active render endpoints found' }
    $project = Join-Path $repo 'tests/EchoSub.CaptureSmoke/EchoSub.CaptureSmoke.csproj'
    & dotnet build $project
    if ($LASTEXITCODE -ne 0) { throw 'Startup smoke build failed' }
    $output = Join-Path $repo ('benchmarks/results/capture-startup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $manifest = Join-Path $output 'endpoints.json'
    ConvertTo-Json -InputObject $endpoints | Set-Content -LiteralPath $manifest -Encoding UTF8
    Write-Host "Report: $output/report.json"
    & dotnet run --project $project --no-build -- --startup (Join-Path $repo 'target/release/echosub-worker.exe') (Join-Path $output 'report.json') $manifest $Rounds
    if ($LASTEXITCODE -ne 0) { throw 'Capture startup comparison found failures; all completed cases are retained in report.json' }
}
finally { Pop-Location }
