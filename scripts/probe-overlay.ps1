param([switch] $NoBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $repo '.nuget/packages'

if (-not $NoBuild) {
    dotnet build (Join-Path $repo 'apps/EchoSub.Desktop/EchoSub.Desktop.csproj') --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed; run check.ps1 first to restore packages' }
}
$probeExe = Join-Path $repo 'apps/EchoSub.Desktop/bin/Debug/net10.0/EchoSub.Desktop.exe'
$reportPath = Join-Path $repo 'docs/evidence/T00-04.3-windows-overlay.json'
$probeProcess = Start-Process -FilePath $probeExe -ArgumentList @('--overlay-probe-report', ('"' + $reportPath + '"')) -WindowStyle Hidden -PassThru
if (-not $probeProcess.WaitForExit(15000)) {
    Stop-Process -Id $probeProcess.Id
    throw 'Overlay probe exceeded 15 seconds'
}
$probeProcess.Refresh()
Get-Content -LiteralPath $reportPath
if ($probeProcess.ExitCode -ne 0) { throw "Overlay probe exited with $($probeProcess.ExitCode)" }
