param([string] $ServerPath, [ValidateRange(1024, 65535)] [int] $Port = 18083)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$env:NUGET_PACKAGES = Join-Path $repo '.nuget/packages'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $env:PATH = (Join-Path $env:ProgramFiles 'dotnet') + ';' + $env:PATH }
if (-not $ServerPath) {
    $command = Get-Command llama-server.exe -CommandType Application -ErrorAction SilentlyContinue
    if (-not $command) { throw 'Specify -ServerPath for an installed llama-server.exe' }
    $ServerPath = $command.Source
    $serverFile = Get-Item -LiteralPath $ServerPath
    if ($serverFile.LinkType -eq 'SymbolicLink') { $ServerPath = $serverFile.Target }
}
$catalog = Get-Content (Join-Path $repo 'benchmarks/model-downloads.json') -Raw | ConvertFrom-Json
$model = $catalog.models | Where-Object role -eq 'translation' | Select-Object -First 1
$modelPath = [IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'benchmarks') $model.path))
if ((Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $model.sha256) { throw 'Translation model hash mismatch' }
$project = Join-Path $repo 'benchmarks/EchoSub.TranslationProbe/EchoSub.TranslationProbe.csproj'
dotnet build $project
if ($LASTEXITCODE -ne 0) { throw 'Translation probe build failed' }
$output = Join-Path $repo ('benchmarks/results/translation-local-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $output | Out-Null
$tokenFile = Join-Path $output 'api-key.tmp'
$oldToken = $env:ECHOSUB_TRANSLATION_TOKEN
$env:ECHOSUB_TRANSLATION_TOKEN = [Guid]::NewGuid().ToString('N')
$server = $null
$readyTimer = [Diagnostics.Stopwatch]::StartNew()
try {
    [IO.File]::WriteAllText($tokenFile, $env:ECHOSUB_TRANSLATION_TOKEN)
    $arguments = @('-m', ('"' + $modelPath + '"'), '-c', '4096', '-ngl', '99', '--parallel', '1', '--host', '127.0.0.1', '--port', "$Port", '--alias', $model.id, '--api-key-file', ('"' + $tokenFile + '"'))
    $server = Start-Process -FilePath $ServerPath -ArgumentList $arguments -WorkingDirectory $repo -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $output 'server-stdout.log') -RedirectStandardError (Join-Path $output 'server-stderr.log')
    $headers = @{ Authorization = "Bearer $env:ECHOSUB_TRANSLATION_TOKEN" }
    $ready = $false
    while ($readyTimer.Elapsed.TotalSeconds -lt 120) {
        $server.Refresh()
        if ($server.HasExited) { throw "Owned llama-server exited with $($server.ExitCode); see native logs" }
        try {
            $health = Invoke-RestMethod "http://127.0.0.1:$Port/health" -Headers $headers -TimeoutSec 2
            if ($health.status -eq 'ok') { $ready = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'Owned llama-server was not ready within 120 seconds' }
    $readyTimer.Stop()
    Write-Host "Local server ready in $($readyTimer.Elapsed.TotalSeconds) s; running authored translation fixtures"
    $reportPath = Join-Path $output 'report.json'
    dotnet run --project $project --no-build -- "http://127.0.0.1:$Port/v1/" $model.id (Join-Path $repo 'benchmarks/translation-fixtures.json') $reportPath
    if ($LASTEXITCODE -ne 0) { throw 'Translation measurement failed' }
    $server.Refresh()
    $gpuSnapshot = if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { & nvidia-smi --query-compute-apps=pid,used_gpu_memory --format=csv,noheader,nounits } else { @() }
    [ordered]@{server_path=$ServerPath;server_sha256=(Get-FileHash -LiteralPath $ServerPath -Algorithm SHA256).Hash.ToLowerInvariant();pid=$server.Id;model_id=$model.id;model_sha256=$model.sha256;context_tokens=4096;gpu_layers_requested=99;actual_backend='Inspect server-stderr.log; no automatic backend claim';process_ready_s=$readyTimer.Elapsed.TotalSeconds;process_peak_working_set_bytes=$server.PeakWorkingSet64;gpu_memory_snapshot_after=$gpuSnapshot;gpu_peak_measured=$false} |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $output 'runtime.json') -Encoding UTF8
    Write-Host "Report: $reportPath; semantic review is still required"
}
finally {
    if ($server) {
        $server.Refresh()
        if (-not $server.HasExited) { Stop-Process -Id $server.Id; $server.WaitForExit() }
    }
    Remove-Item -LiteralPath $tokenFile -ErrorAction SilentlyContinue
    $env:ECHOSUB_TRANSLATION_TOKEN = $oldToken
}
