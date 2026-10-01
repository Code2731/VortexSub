param([switch] $NoBuild, [switch] $Offline, [switch] $NoPause, [switch] $Live,
    [ValidateSet('cpu', 'cuda')] [string] $AsrBackend = 'cpu', [switch] $FastPartials)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $repo '.nuget/packages'
$env:ECHOSUB_WORKER_PATH = Join-Path $repo 'target/debug/echosub-worker.exe'
$env:ECHOSUB_LIVE_UI = if ($Live) { '1' } else { '0' }
$env:ECHOSUB_WORKER_ARGUMENTS = $null
$env:ECHOSUB_ENDPOINTS = $null
if ($Live) { $env:ECHOSUB_WORKER_PATH = Join-Path $repo "target/model-probe-$AsrBackend/release/echosub-worker.exe" }
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
    if (-not $Live -and $AsrBackend -ne 'cpu') { throw '-AsrBackend cuda requires -Live' }
    if ($FastPartials -and (-not $Live -or $AsrBackend -ne 'cuda')) { throw '-FastPartials requires -Live -AsrBackend cuda' }
    if ($Live) {
        if (-not [Environment]::Is64BitProcess) { throw 'Live diagnostics require Windows x64 PowerShell' }
        $catalogue = Get-Content -LiteralPath (Join-Path $repo 'benchmarks/model-downloads.json') -Raw | ConvertFrom-Json
        $vadCatalogue = Get-Content -LiteralPath (Join-Path $repo 'benchmarks/vad-assets.json') -Raw | ConvertFrom-Json
        $asr = $catalogue.models | Where-Object { $_.id -eq 'whisper-base' }
        $vad = $vadCatalogue.assets | Where-Object { $_.id -eq 'silero-v6' }
        $runtime = $vadCatalogue.assets | Where-Object { $_.id -eq 'ort-win-x64' }
        $asrPath = [IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'benchmarks') $asr.path))
        $vadPath = [IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'benchmarks') $vad.path))
        $runtimePath = [IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'benchmarks') $runtime.path))
        foreach ($assetPath in @($asrPath,$vadPath,$runtimePath)) {
            if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) { throw "Consented local asset is missing: $assetPath. This launcher does not download models." }
        }
        $workerArguments = @('--diagnostic-translation', '--diagnostic-capture','--live-asr','--session-control','--diagnostic-asr','--asr-model',$asrPath,'--asr-sha256',$asr.sha256,'--asr-backend',$AsrBackend,
            '--diagnostic-vad','--vad-model',$vadPath,'--vad-sha256',$vad.sha256,'--vad-runtime',$runtimePath,'--vad-runtime-sha256',$runtime.sha256)
        $env:ECHOSUB_WORKER_ARGUMENTS = ConvertTo-Json -InputObject $workerArguments -Compress
        if ($FastPartials) {
            $workerArguments += '--fast-partials'
            $env:ECHOSUB_WORKER_ARGUMENTS = ConvertTo-Json -InputObject $workerArguments -Compress
            Write-Host 'Fast partial request interval: 0.5 seconds; enable partial ASR/translation in the UI before Start.'
        }
        Write-Host "Live $AsrBackend source diagnostics: optional local translation; partial disabled by default; select capture Start in the UI."
    }
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
        $cargoArgs = @('build', '-p', 'echosub-worker', '--locked', '--target-dir', (Join-Path $repo 'target'))
        if ($Offline -or $env:ECHOSUB_OFFLINE -eq '1') { $cargoArgs += '--offline' }
        if ($Live) {
            $previousTarget = $env:CARGO_TARGET_DIR
            try {
                $env:CARGO_TARGET_DIR = Join-Path $repo "target/model-probe-$AsrBackend"
                & (Join-Path $PSScriptRoot 'build-model-probe.ps1') -Backend $AsrBackend -Package echosub-worker -Vad -Offline:$Offline
            } finally { $env:CARGO_TARGET_DIR = $previousTarget }
            $endpointArgs = @('build','-p','echosub-capture-windows','--release','--locked','--target-dir',(Join-Path $repo 'target'))
            if ($Offline -or $env:ECHOSUB_OFFLINE -eq '1') { $endpointArgs += '--offline' }
            & $cargoExe @endpointArgs
        } else { & $cargoExe @cargoArgs }
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
    if ($Live) {
        $endpoints = @(@{ Id = $null; Name = 'Default render endpoint (selected at Start)' })
        $endpointProbe = Join-Path $repo 'target/release/echosub-capture-windows.exe'
        if (Test-Path -LiteralPath $endpointProbe -PathType Leaf) {
            $previousOutputEncoding = [Console]::OutputEncoding
            try {
                [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
                $inventory = & $endpointProbe --list
            } finally { [Console]::OutputEncoding = $previousOutputEncoding }
            if ($LASTEXITCODE -ne 0) { throw 'Render endpoint enumeration failed' }
            foreach ($line in $inventory) {
                if ($line -match '^(?:default|render)\s+(\S+) name=(.+)$') { $endpoints += @{ Id = $Matches[1]; Name = $Matches[2] } }
            }
        }
        $env:ECHOSUB_ENDPOINTS = ConvertTo-Json -InputObject $endpoints -Compress
    }
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

