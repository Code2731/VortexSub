param([string] $ServerPath, [switch] $NoBuild,
    [ValidateSet('cpu', 'cuda')] [string] $AsrBackend = 'cpu', [switch] $FastPartials, [switch] $CaptionTiming, [switch] $DecodeWindow, [switch] $PadShortPartials, [switch] $SupportedPreview)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$server = $null
$tokenFile = $null
$oldToken = $env:ECHOSUB_TRANSLATION_TOKEN
$exitCode = 0

try {
    if ($FastPartials -and $AsrBackend -ne 'cuda') { throw '-FastPartials requires -AsrBackend cuda' }
    if ($DecodeWindow -and -not $FastPartials) { throw '-DecodeWindow requires -FastPartials' }
    if ($PadShortPartials -and (-not $FastPartials -or $DecodeWindow)) { throw '-PadShortPartials requires -FastPartials and cannot combine with -DecodeWindow' }
    if ($SupportedPreview -and (-not $FastPartials -or $DecodeWindow)) { throw '-SupportedPreview requires -FastPartials and cannot combine with -DecodeWindow' }
    Write-Host 'EchoSub: starting local translation server and live subtitle UI.'
    if (-not $ServerPath) {
        $command = Get-Command llama-server.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { $ServerPath = $command.Source }
        else {
            $candidate = Join-Path $env:LOCALAPPDATA 'Microsoft/WinGet/Links/llama-server.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $ServerPath = $candidate }
            else { throw 'Cannot find installed llama-server.exe. Use run-live.bat -ServerPath "C:\path\llama-server.exe".' }
        }
    }
    $serverFile = Get-Item -LiteralPath $ServerPath
    if ($serverFile.LinkType -eq 'SymbolicLink') { $ServerPath = $serverFile.Target }
    $ServerPath = (Resolve-Path -LiteralPath $ServerPath).Path
    $catalog = Get-Content (Join-Path $repo 'benchmarks/model-downloads.json') -Raw | ConvertFrom-Json
    $model = $catalog.models | Where-Object role -eq 'translation' | Select-Object -First 1
    if (-not $model) { throw 'Translation model is missing from the manifest.' }
    $modelPath = [IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'benchmarks') $model.path))
    Write-Host '[1/3] Checking the existing translation model...'
    if ((Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $model.sha256) {
        throw 'Translation model hash mismatch. No download was attempted.'
    }

    # Refuse an occupied port; do not adopt or terminate an unrelated server.
    $listener = New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 1234)
    try { $listener.Start() } finally { $listener.Stop() }
    $logs = Join-Path $repo 'logs'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    $runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID
    $tokenFile = Join-Path $logs "translation-$runId.key.tmp"
    $env:ECHOSUB_TRANSLATION_TOKEN = [Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($tokenFile, $env:ECHOSUB_TRANSLATION_TOKEN)
    $stdout = Join-Path $logs "translation-$runId.stdout.log"
    $stderr = Join-Path $logs "translation-$runId.stderr.log"
    $arguments = @('-m', ('"' + $modelPath + '"'), '-c', '4096', '-ngl', '99', '--parallel', '1',
        '--host', '127.0.0.1', '--port', '1234', '--alias', $model.id,
        '--api-key-file', ('"' + $tokenFile + '"'))
    Write-Host '[2/3] Loading local translation server (up to 120 seconds)...'
    Write-Host "Server log: $stderr"
    $server = Start-Process -FilePath $ServerPath -ArgumentList $arguments -WorkingDirectory $repo -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $ready = $false
    $nextNotice = 10
    while ($clock.Elapsed.TotalSeconds -lt 120) {
        $server.Refresh()
        if ($server.HasExited) { throw "Local server exited with $($server.ExitCode). See $stderr" }
        try {
            $response = Invoke-RestMethod -Uri 'http://127.0.0.1:1234/v1/models' -Headers @{ Authorization = "Bearer $env:ECHOSUB_TRANSLATION_TOKEN" } -TimeoutSec 1
            $health = Invoke-RestMethod -Uri 'http://127.0.0.1:1234/health' -Headers @{ Authorization = "Bearer $env:ECHOSUB_TRANSLATION_TOKEN" } -TimeoutSec 1
            if ($health.status -eq 'ok' -and @($response.data | Where-Object id -eq $model.id).Count -gt 0) { $ready = $true; break }
        } catch { }
        if ($clock.Elapsed.TotalSeconds -ge $nextNotice) {
            Write-Host ('Waiting for model: {0:F1} seconds' -f $clock.Elapsed.TotalSeconds)
            $nextNotice += 10
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw "Translation server was not ready within 120 seconds. See $stderr" }
    Write-Host '[3/3] Opening live UI; translation endpoint: http://127.0.0.1:1234/v1/'
    Write-Host 'In the UI, click server/model lookup, wait for translation Ready, then Start session.'
    Write-Host 'Closing the app also stops this launcher-owned translation server.'
    $launcherArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'scripts/run.ps1'), '-Live', '-Offline', '-NoPause', '-AsrBackend', $AsrBackend)
    if ($NoBuild) { $launcherArgs += '-NoBuild' }
    if ($FastPartials) { $launcherArgs += '-FastPartials' }
    if ($DecodeWindow) { $launcherArgs += '-DecodeWindow' }
    if ($PadShortPartials) { $launcherArgs += '-PadShortPartials' }
    if ($SupportedPreview) { $launcherArgs += '-SupportedPreview' }
    if ($CaptionTiming) { $launcherArgs += '-CaptionTiming' }
    & (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') @launcherArgs
    $exitCode = $LASTEXITCODE
} catch {
    Write-Host "EchoSub live launcher failed: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
} finally {
    if ($server) {
        $server.Refresh()
        if (-not $server.HasExited) { Stop-Process -InputObject $server -Force -ErrorAction SilentlyContinue }
        $server.Dispose()
    }
    if ($tokenFile -and (Test-Path -LiteralPath $tokenFile)) { Remove-Item -LiteralPath $tokenFile -Force }
    $env:ECHOSUB_TRANSLATION_TOKEN = $oldToken
}
exit $exitCode
