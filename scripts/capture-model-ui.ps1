param([string] $OutputDirectory = 'benchmarks/results/ui-model-capture', [switch] $NoBuild, [switch] $Sequence, [switch] $Revisions)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$server = $null
$keyFile = $null
$oldToken = $env:ECHOSUB_TRANSLATION_TOKEN
$oldModel = $env:ECHOSUB_TRANSLATION_LAUNCH_MODEL_ID
try {
    if ($Sequence -and $Revisions) { throw 'Choose Sequence or Revisions for separate observations.' }
    Set-Location $repo
    $output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    $catalog = Get-Content benchmarks/translation-research-models.json -Raw | ConvertFrom-Json
    $model = $catalog.models | Where-Object id -eq 'hy-mt2-1.8b-q4_k_m' | Select-Object -First 1
    if (-not $model -or $model.asset_status -ne 'installed_verified') { throw 'Verified installed Hy-MT2 model required.' }
    $modelPath = [IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'benchmarks') $model.path))
    if ((Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $model.sha256) {
        throw 'Installed model hash mismatch.'
    }
    $runtime = Join-Path $repo 'models/runtime-b11146/llama-server.exe'
    if (-not (Test-Path -LiteralPath $runtime -PathType Leaf)) { throw 'Installed b11146 runtime required.' }
    $worker = Join-Path $repo 'target/debug/echosub-worker.exe'
    if (-not (Test-Path -LiteralPath $worker -PathType Leaf)) { throw 'Build the offline mock worker first.' }
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    $env:NUGET_PACKAGES = Join-Path $repo '.nuget/packages'
    if (-not $NoBuild) {
        dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    }
    # Let the OS select a free port. Readiness checks below also verify the owned process.
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); $port = $listener.LocalEndpoint.Port } finally { $listener.Stop() }
    $env:ECHOSUB_TRANSLATION_TOKEN = [Guid]::NewGuid().ToString('N')
    $env:ECHOSUB_TRANSLATION_LAUNCH_MODEL_ID = $model.id
    $keyFile = Join-Path $output 'server-key.tmp'
    [IO.File]::WriteAllText($keyFile, $env:ECHOSUB_TRANSLATION_TOKEN)
    $arguments = @('-m', ('"' + $modelPath + '"'), '-c', '4096', '-ngl', '99', '--parallel', '1',
        '--host', '127.0.0.1', '--port', "$port", '--alias', $model.id, '--jinja',
        '--api-key-file', ('"' + $keyFile + '"'))
    $server = Start-Process -FilePath $runtime -ArgumentList $arguments -WorkingDirectory $repo -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $output 'server.stdout.log') -RedirectStandardError (Join-Path $output 'server.stderr.log')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $ready = $false
    while ($clock.Elapsed.TotalSeconds -lt 120) {
        $server.Refresh()
        if ($server.HasExited) { throw 'Owned model server exited. Read server.stderr.log.' }
        try {
            $health = Invoke-RestMethod "http://127.0.0.1:$port/health" -Headers @{Authorization="Bearer $env:ECHOSUB_TRANSLATION_TOKEN"} -TimeoutSec 1
            if ($health.status -eq 'ok') { $ready = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'Model server did not become ready within 120 seconds.' }
    @{model_id=$model.id; model_sha256=$model.sha256; runtime_sha256=(Get-FileHash $runtime -Algorithm SHA256).Hash.ToLowerInvariant();
      scope='Real HTTP model translation; synthetic ASR; no audio capture; Avalonia renders'; startup_s=$clock.Elapsed.TotalSeconds} |
        ConvertTo-Json | Set-Content (Join-Path $output 'model.json') -Encoding UTF8
    $captureArguments = @('--ui-capture-worker', $worker, '--ui-capture-dir', $output,
        '--ui-model-capture-endpoint', "http://127.0.0.1:$port/v1/")
    if ($Sequence) { $captureArguments += '--ui-model-capture-sequence' }
    if ($Revisions) { $captureArguments += '--ui-model-capture-revisions' }
    dotnet run --project apps/EchoSub.Desktop --no-build --no-restore -- @captureArguments
    if ($LASTEXITCODE -ne 0) { throw 'Model UI capture failed.' }
} finally {
    if ($server) {
        $server.Refresh()
        if (-not $server.HasExited) { $server.Kill(); [void]$server.WaitForExit(5000) }
        $server.Dispose()
    }
    if ($keyFile -and (Test-Path -LiteralPath $keyFile)) { Remove-Item -LiteralPath $keyFile -Force }
    $env:ECHOSUB_TRANSLATION_TOKEN = $oldToken
    $env:ECHOSUB_TRANSLATION_LAUNCH_MODEL_ID = $oldModel
}
