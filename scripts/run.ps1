param([switch] $NoBuild, [switch] $Offline, [switch] $NoPause)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $repo '.nuget/packages'
$env:ECHOSUB_WORKER_PATH = Join-Path $repo 'target/debug/echosub-worker.exe'
$logDirectory = Join-Path $repo 'logs'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID
$launcherLog = Join-Path $logDirectory "run-$runId.log"
$env:ECHOSUB_STARTUP_LOG = Join-Path $logDirectory "desktop-$runId.log"
$desktopProject = Join-Path $repo 'apps/EchoSub.Desktop/EchoSub.Desktop.csproj'
$desktopExe = Join-Path $repo 'apps/EchoSub.Desktop/bin/Debug/net10.0/EchoSub.Desktop.exe'
$transcribing = $false

function Resolve-LauncherTool {
    param([string] $Name, [string[]] $Candidates)
    $application = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($application) { return $application.Source }
    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    throw "Cannot find $Name in PATH or its standard installation directories. See README.md for the required SDK versions."
}

Push-Location $repo
try {
    Start-Transcript -Path $launcherLog | Out-Null
    $transcribing = $true
    Write-Host "EchoSub launcher: $repo"
    Write-Host "Launcher log: $launcherLog"
    if (-not $NoBuild) {
        $cargoCandidates = @((Join-Path $env:USERPROFILE '.cargo/bin/cargo.exe'))
        if ($env:CARGO_HOME) { $cargoCandidates = @((Join-Path $env:CARGO_HOME 'bin/cargo.exe')) + $cargoCandidates }
        $dotnetCandidates = @((Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'))
        if ($env:DOTNET_ROOT) { $dotnetCandidates = @((Join-Path $env:DOTNET_ROOT 'dotnet.exe')) + $dotnetCandidates }
        $cargoExe = Resolve-LauncherTool 'cargo.exe' $cargoCandidates
        $dotnetExe = Resolve-LauncherTool 'dotnet.exe' $dotnetCandidates
        # Explorer can retain an older PATH after SDK installation. Update only this process.
        $env:PATH = (Split-Path $cargoExe -Parent) + ';' + (Split-Path $dotnetExe -Parent) + ';' + $env:PATH
        Write-Host "Cargo: $cargoExe"
        Write-Host ".NET: $dotnetExe"
        Write-Host '[1/3] Building Rust worker...'
        $cargoArgs = @('build', '-p', 'echosub-worker', '--locked')
        if ($Offline -or $env:ECHOSUB_OFFLINE -eq '1') { $cargoArgs += '--offline' }
        & $cargoExe @cargoArgs
        if ($LASTEXITCODE -ne 0) { throw "Rust build failed (exit $LASTEXITCODE)" }

        $assets = Join-Path $repo 'apps/EchoSub.Desktop/obj/project.assets.json'
        if (-not (Test-Path -LiteralPath $assets)) {
            Write-Host '[2/3] Restoring .NET packages for the first build...'
            $restoreArgs = @('restore', $desktopProject, '--locked-mode')
            if ($env:ECHOSUB_NUGET_SOURCE) { $restoreArgs += @('--source', $env:ECHOSUB_NUGET_SOURCE) }
            elseif ($Offline) { $restoreArgs += @('--source', $env:NUGET_PACKAGES) }
            & $dotnetExe @restoreArgs
            if ($LASTEXITCODE -ne 0) { throw ".NET restore failed (exit $LASTEXITCODE)" }
        }
        Write-Host '[2/3] Building desktop app using restored packages...'
        & $dotnetExe build $desktopProject --no-restore
        if ($LASTEXITCODE -ne 0) { throw "Desktop build failed (exit $LASTEXITCODE)" }
    }
    if (-not (Test-Path -LiteralPath $desktopExe)) { throw "Desktop executable is missing: $desktopExe" }
    if (-not (Test-Path -LiteralPath $env:ECHOSUB_WORKER_PATH)) { throw "Worker executable is missing: $env:ECHOSUB_WORKER_PATH" }
    Write-Host '[3/3] Opening EchoSub...'
    Write-Host "Desktop log: $env:ECHOSUB_STARTUP_LOG"
    $appProcess = Start-Process -FilePath $desktopExe -WorkingDirectory $repo -WindowStyle Normal -PassThru
    Write-Host "Desktop PID: $($appProcess.Id). This console stays open until the app closes."
    $appProcess.WaitForExit()
    $appProcess.Refresh()
    if ($appProcess.ExitCode -ne 0) { throw "Desktop exited with $($appProcess.ExitCode); see the desktop log" }
    Write-Host 'EchoSub closed normally.'
}
catch {
    Write-Host "EchoSub could not start or exited with an error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Logs: $logDirectory"
    if (-not $NoPause) { Read-Host 'Press Enter to close this launcher' | Out-Null }
    exit 1
}
finally {
    if ($transcribing) { Stop-Transcript | Out-Null }
    Pop-Location
}

