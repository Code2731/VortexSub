param([string] $ServerPath, [switch] $NoBuild,
    [ValidateSet('cpu', 'cuda')] [string] $AsrBackend = 'cpu', [switch] $FastPartials, [switch] $CaptionTiming, [switch] $DecodeWindow, [switch] $PadShortPartials, [switch] $SupportedPreview, [switch] $IsolatedTranslationContext,
    [ValidateSet('qwen', 'hymt2')] [string] $TranslationModel = 'qwen',
    [ValidateRange(10, 300)] [int] $RuntimeVersionTimeoutSeconds = 60)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$server = $null
$tokenFile = $null
$oldToken = $env:ECHOSUB_TRANSLATION_TOKEN
$oldProfile = $env:ECHOSUB_TRANSLATION_INPUT_PROFILE
$oldModelId = $env:ECHOSUB_TRANSLATION_LAUNCH_MODEL_ID
$exitCode = 0

try {
    if ($FastPartials -and $AsrBackend -ne 'cuda') { throw '-FastPartials requires -AsrBackend cuda' }
    if ($DecodeWindow -and -not $FastPartials) { throw '-DecodeWindow requires -FastPartials' }
    if ($PadShortPartials -and (-not $FastPartials -or $DecodeWindow)) { throw '-PadShortPartials requires -FastPartials and cannot combine with -DecodeWindow' }
    if ($SupportedPreview -and (-not $FastPartials -or $DecodeWindow)) { throw '-SupportedPreview requires -FastPartials and cannot combine with -DecodeWindow' }
    if ($TranslationModel -eq 'hymt2' -and $IsolatedTranslationContext) { throw 'Hy-MT2 uses its own context input; cannot combine with -IsolatedTranslationContext.' }
    if ($TranslationModel -eq 'hymt2' -and -not $ServerPath) {
        $ServerPath = Join-Path $repo 'models/runtime-b11146/llama-server.exe'
    }
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
    $catalogFile = if ($TranslationModel -eq 'hymt2') { 'benchmarks/translation-research-models.json' } else { 'benchmarks/model-downloads.json' }
    $modelId = if ($TranslationModel -eq 'hymt2') { 'hy-mt2-1.8b-q4_k_m' } else { 'qwen3-4b-instruct-2507-q4_k_m' }
    $inputProfile = if ($TranslationModel -eq 'hymt2') { 'hymt2-greedy' } else { 'standard' }
    $catalog = Get-Content (Join-Path $repo $catalogFile) -Raw | ConvertFrom-Json
    $model = $catalog.models | Where-Object id -eq $modelId | Select-Object -First 1
    if (-not $model) { throw 'Translation model is missing from the manifest.' }
    if ($TranslationModel -eq 'hymt2' -and $model.asset_status -ne 'installed_verified') { throw 'Hy-MT2 is not marked installed_verified. No download was attempted.' }
    $modelPath = [IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'benchmarks') $model.path))
    Write-Host '[1/3] Checking the existing translation model...'
    if ((Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $model.sha256) {
        throw 'Translation model hash mismatch. No download was attempted.'
    }
    $serverHash = (Get-FileHash -LiteralPath $ServerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $logs = Join-Path $repo 'logs'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    $runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID
    if ($TranslationModel -eq 'hymt2') {
        $versionLog = Join-Path $logs "translation-$runId.version.json"
        $versionClock = [Diagnostics.Stopwatch]::StartNew()
        $versionStatus = 'Starting'
        $versionFailure = $null
        $versionExitCode = $null
        $versionOutput = $null
        $versionError = $null
        Write-Host "Checking translation runtime version (up to $RuntimeVersionTimeoutSeconds seconds)..."
        Write-Host "Version log: $versionLog"
        $versionProcess = New-Object Diagnostics.Process
        $versionProcess.StartInfo.FileName = $ServerPath
        $versionProcess.StartInfo.Arguments = '--version'
        $versionProcess.StartInfo.UseShellExecute = $false
        $versionProcess.StartInfo.CreateNoWindow = $true
        $versionProcess.StartInfo.RedirectStandardOutput = $true
        $versionProcess.StartInfo.RedirectStandardError = $true
        try {
            [void]$versionProcess.Start()
            $versionOutput = $versionProcess.StandardOutput.ReadToEndAsync()
            $versionError = $versionProcess.StandardError.ReadToEndAsync()
            $nextVersionNotice = 10
            while (-not $versionProcess.WaitForExit(1000)) {
                if ($versionClock.Elapsed.TotalSeconds -ge $RuntimeVersionTimeoutSeconds) {
                    $versionStatus = 'TimedOut'
                    $versionProcess.Kill()
                    [void]$versionProcess.WaitForExit(2000)
                    throw "Translation runtime version check timed out after $RuntimeVersionTimeoutSeconds seconds. See $versionLog"
                }
                if ($versionClock.Elapsed.TotalSeconds -ge $nextVersionNotice) {
                    Write-Host ('Waiting for runtime version: {0:F1} seconds' -f $versionClock.Elapsed.TotalSeconds)
                    $nextVersionNotice += 10
                }
            }
            $versionExitCode = $versionProcess.ExitCode
            $version = $versionOutput.Result + $versionError.Result
            if ($versionProcess.ExitCode -ne 0 -or $version -notmatch '\bbuild\s+11146\b') {
                $versionStatus = 'Rejected'
                throw "Hy-MT2 live comparison requires the separately installed b11146 runtime. See $versionLog"
            }
            $versionStatus = 'Verified'
        } catch {
            $versionFailure = $_.Exception.Message
            if ($versionStatus -eq 'Starting') { $versionStatus = 'Failed' }
            throw
        } finally {
            $versionClock.Stop()
            $capturedOutput = if ($versionOutput -and $versionOutput.IsCompleted -and -not $versionOutput.IsFaulted) { $versionOutput.Result } else { $null }
            $capturedError = if ($versionError -and $versionError.IsCompleted -and -not $versionError.IsFaulted) { $versionError.Result } else { $null }
            @{ server_path = $ServerPath; server_sha256 = $serverHash; status = $versionStatus;
               elapsed_s = $versionClock.Elapsed.TotalSeconds; timeout_s = $RuntimeVersionTimeoutSeconds;
               exit_code = $versionExitCode; error = $versionFailure;
               stdout = $capturedOutput; stderr = $capturedError } |
                ConvertTo-Json | Set-Content -LiteralPath $versionLog -Encoding UTF8
            $versionProcess.Dispose()
        }
    }

    # Refuse an occupied port; do not adopt or terminate an unrelated server.
    $listener = New-Object System.Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 1234)
    try { $listener.Start() } finally { $listener.Stop() }
    $tokenFile = Join-Path $logs "translation-$runId.key.tmp"
    $env:ECHOSUB_TRANSLATION_TOKEN = [Guid]::NewGuid().ToString('N')
    $env:ECHOSUB_TRANSLATION_INPUT_PROFILE = $inputProfile
    $env:ECHOSUB_TRANSLATION_LAUNCH_MODEL_ID = $model.id
    [IO.File]::WriteAllText($tokenFile, $env:ECHOSUB_TRANSLATION_TOKEN)
    $stdout = Join-Path $logs "translation-$runId.stdout.log"
    $stderr = Join-Path $logs "translation-$runId.stderr.log"
    $arguments = @('-m', ('"' + $modelPath + '"'), '-c', '4096', '-ngl', '99', '--parallel', '1',
        '--host', '127.0.0.1', '--port', '1234', '--alias', $model.id,
        '--api-key-file', ('"' + $tokenFile + '"'))
    if ($TranslationModel -eq 'hymt2') { $arguments += '--jinja' }
    @{ model_id = $model.id; model_sha256 = $model.sha256; input_profile = $inputProfile;
       server_sha256 = $serverHash; experimental = ($TranslationModel -eq 'hymt2');
       note = 'Launcher-owned model file hash verified; changing the UI model ID does not reload weights.' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logs "translation-$runId.config.json") -Encoding UTF8
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
    Write-Host "Loaded model: $($model.id); input profile: $inputProfile. To change weights, close the app and restart this launcher."
    Write-Host 'Closing the app also stops this launcher-owned translation server.'
    $launcherArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'scripts/run.ps1'), '-Live', '-Offline', '-NoPause', '-AsrBackend', $AsrBackend)
    if ($NoBuild) { $launcherArgs += '-NoBuild' }
    if ($FastPartials) { $launcherArgs += '-FastPartials' }
    if ($DecodeWindow) { $launcherArgs += '-DecodeWindow' }
    if ($PadShortPartials) { $launcherArgs += '-PadShortPartials' }
    if ($SupportedPreview) { $launcherArgs += '-SupportedPreview' }
    if ($IsolatedTranslationContext) { $launcherArgs += '-IsolatedTranslationContext' }
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
    $env:ECHOSUB_TRANSLATION_INPUT_PROFILE = $oldProfile
    $env:ECHOSUB_TRANSLATION_LAUNCH_MODEL_ID = $oldModelId
}
exit $exitCode
