param(
    [string] $ServerPath,
    [ValidateRange(1,3600)] [double] $Seconds = 302,
    [ValidateRange(0.05,10)] [double] $IntervalSeconds = 0.25,
    [ValidateRange(1024,65535)] [int] $Port = 18084,
    [string[]] $Phases = @('base-alone','small-alone','translation-alone','base-concurrent','small-concurrent')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$asrExe = Join-Path $repo 'target/model-probe-cuda/release/echosub-model-probe.exe'
$translationExe = Join-Path $repo 'benchmarks/EchoSub.TranslationProbe/bin/Debug/net10.0/EchoSub.TranslationProbe.exe'
if (-not (Test-Path -LiteralPath $asrExe) -or -not (Test-Path -LiteralPath $translationExe)) { throw 'Build CUDA model probe and TranslationProbe before measuring.' }
if (-not $ServerPath) {
    $ServerPath = (Get-Command llama-server.exe -ErrorAction Stop).Source
    $file = Get-Item -LiteralPath $ServerPath
    if ($file.LinkType -eq 'SymbolicLink') { $ServerPath = $file.Target }
}
$valid = @('base-alone','small-alone','translation-alone','base-concurrent','small-concurrent')
foreach ($phase in $Phases) { if ($phase -notin $valid) { throw "Unknown phase $phase" } }
$catalogPath = Join-Path $repo 'benchmarks/model-downloads.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
$model = $catalog.models | Where-Object role -eq 'translation' | Select-Object -First 1
$modelPath = [IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'benchmarks') $model.path))
if ((Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $model.sha256) { throw 'Translation model hash mismatch' }
$output = Join-Path $repo ('benchmarks/results/contention-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$oldToken = $env:ECHOSUB_TRANSLATION_TOKEN
$env:ECHOSUB_TRANSLATION_TOKEN = [Guid]::NewGuid().ToString('N')
$invariant = [Globalization.CultureInfo]::InvariantCulture
$durationArg = $Seconds.ToString($invariant)
$intervalArg = $IntervalSeconds.ToString($invariant)
$runtime = [ordered]@{task='T00-04.4';status='RUNNING';output=$output;seconds=$Seconds;interval_s=$IntervalSeconds;pending_capacity=0;threads=8;context_tokens=4096;gpu_layers_requested=99;parallel=1;temperature=0;max_tokens=256;game_running_by_probe=$false;device='Windows RTX 3080 substitute; Mac unavailable';asr_sha256=(Get-FileHash $asrExe).Hash.ToLowerInvariant();translation_client_sha256=(Get-FileHash $translationExe).Hash.ToLowerInvariant();server_sha256=(Get-FileHash $ServerPath).Hash.ToLowerInvariant();model_catalog_sha256=(Get-FileHash $catalogPath).Hash.ToLowerInvariant();phases=@();error=$null}
$runtime.device='Available Windows device substitutes for the reference devices; Mac unavailable'
$runtime.os=[Environment]::OSVersion.ToString()
$runtime.cpu=$env:PROCESSOR_IDENTIFIER
$runtime.logical_processors=[Environment]::ProcessorCount
$runtime.gpu_inventory=if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { @(& nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits) } else { @() }
$runtime.translation_dll_sha256=(Get-FileHash (Join-Path (Split-Path $translationExe -Parent) 'EchoSub.TranslationProbe.dll')).Hash.ToLowerInvariant()
function Quote-Arg([string] $value) { if ($value.Contains('"')) { throw 'Quotes in process argument are unsupported' }; return '"' + $value + '"' }
function Launch([string] $exe, [string[]] $arguments, [string] $name, [string] $directory) {
    Start-Process -FilePath $exe -ArgumentList ($arguments | ForEach-Object { Quote-Arg $_ }) -WorkingDirectory $repo -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $directory "$name-stdout.log") -RedirectStandardError (Join-Path $directory "$name-stderr.log")
}
function Save-Runtime { $runtime | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output 'runtime.json') -Encoding UTF8 }
try {
    foreach ($phase in $Phases) {
        $directory = Join-Path $output $phase
        New-Item -ItemType Directory -Path $directory | Out-Null
        $server = $null; $asr = $null; $translation = $null
        $tokenFile = Join-Path $directory 'api-key.tmp'
        $gate = Join-Path $directory 'start.txt'
        $readyFiles = @(); $clients = @(); $all = @(); $samples = [Collections.Generic.List[object]]::new()
        Write-Host "Phase ${phase}: warming up; output $directory"
        $entry = [ordered]@{name=$phase;status='RUNNING';directory=$directory;pids=@();memory_samples='memory.json';error=$null}
        $runtime.phases += $entry; Save-Runtime
        try {
            if ($phase -like '*translation*' -or $phase -like '*concurrent') {
                [IO.File]::WriteAllText($tokenFile, $env:ECHOSUB_TRANSLATION_TOKEN)
                $server = Launch $ServerPath @('-m',$modelPath,'-c','4096','-ngl','99','--parallel','1','--host','127.0.0.1','--port',"$Port",'--alias',$model.id,'--api-key-file',$tokenFile) 'server' $directory
                $all += $server
                $wait = [Diagnostics.Stopwatch]::StartNew(); $ready = $false
                while ($wait.Elapsed.TotalSeconds -lt 120) {
                    $server.Refresh(); if ($server.HasExited) { throw 'Owned server exited during startup' }
                    try { $health = Invoke-RestMethod "http://127.0.0.1:$Port/health" -Headers @{Authorization="Bearer $env:ECHOSUB_TRANSLATION_TOKEN"} -TimeoutSec 2; if ($health.status -eq 'ok') { $ready=$true; break } } catch { }
                    Start-Sleep -Milliseconds 250
                }
                if (-not $ready) { throw 'Server startup timeout' }
                $readyFile = Join-Path $directory 'translation.ready'; $readyFiles += $readyFile
                $translation = Launch $translationExe @("http://127.0.0.1:$Port/v1/",$model.id,(Join-Path $repo 'benchmarks/translation-fixtures.json'),(Join-Path $directory 'translation.json'),$durationArg,$intervalArg,$readyFile,$gate) 'translation' $directory
                $clients += $translation; $all += $translation
            }
            if ($phase -ne 'translation-alone') {
                $asrModel = if ($phase -like 'base-*') { 'whisper-base' } else { 'whisper-small' }
                $readyFile = Join-Path $directory 'asr.ready'; $readyFiles += $readyFile
                $asr = Launch $asrExe @('load',(Join-Path $repo 'benchmarks/fixtures/local-tts/manifest.json'),$catalogPath,(Join-Path $directory 'asr.json'),'8','cuda',$asrModel,$durationArg,$intervalArg,$readyFile,$gate) 'asr' $directory
                $clients += $asr; $all += $asr
            }
            $entry.pids = @($all | ForEach-Object {$_.Id}); Save-Runtime
            $wait = [Diagnostics.Stopwatch]::StartNew()
            while (@($readyFiles | Where-Object {-not (Test-Path -LiteralPath $_)}).Count -gt 0) {
                foreach ($process in $all) { $process.Refresh(); if ($process.HasExited) { throw "Owned process $($process.Id) exited before gate" } }
                if ($wait.Elapsed.TotalSeconds -gt 120) { throw 'Client warm-up timeout' }
                Start-Sleep -Milliseconds 100
            }
            $start = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()/1000.0 + 2
            $gateTemporary = Join-Path $directory 'start.tmp'
            [IO.File]::WriteAllText($gateTemporary,$start.ToString('R',$invariant))
            Move-Item -LiteralPath $gateTemporary -Destination $gate
            $watch = [Diagnostics.Stopwatch]::StartNew(); $lastProgress = 0
            while ($true) {
                $live = 0; $processSamples = @()
                foreach ($process in $all) {
                    $process.Refresh()
                    if (-not $process.HasExited) {
                        if ($process -ne $server) { $live++ }
                        $processSamples += [ordered]@{pid=$process.Id;working_set_bytes=$process.WorkingSet64;private_bytes=$process.PrivateMemorySize64;peak_working_set_bytes=$process.PeakWorkingSet64;cpu_s=$process.TotalProcessorTime.TotalSeconds}
                    }
                }
                if ($server -and $server.HasExited) { throw 'Owned server exited during measurement' }
                $gpu = @()
                if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { $gpu = @(& nvidia-smi --query-gpu=index,memory.used,utilization.gpu --format=csv,noheader,nounits) }
                $samples.Add([ordered]@{utc_s=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()/1000.0;coordinator_elapsed_s=$watch.Elapsed.TotalSeconds;processes=$processSamples;gpu_device_wide_csv=$gpu})
                if ($live -eq 0) { break }
                if ($watch.Elapsed.TotalSeconds -gt ($Seconds + 150)) { throw 'Measurement timeout' }
                if ($watch.Elapsed.TotalSeconds - $lastProgress -gt 30) { Write-Host "$phase $([Math]::Round($watch.Elapsed.TotalSeconds,1)) s / $Seconds s"; $lastProgress=$watch.Elapsed.TotalSeconds }
                Start-Sleep -Milliseconds 1000
            }
            foreach ($process in $clients) { $process.WaitForExit(); if ($process.ExitCode -ne 0) { throw "Client $($process.Id) exit $($process.ExitCode)" } }
            $reports = @()
            if ($asr) { $reports += Get-Content (Join-Path $directory 'asr.json') -Raw | ConvertFrom-Json }
            if ($translation) { $reports += Get-Content (Join-Path $directory 'translation.json') -Raw | ConvertFrom-Json }
            $entry.overlap_s = ($reports | ForEach-Object {[Math]::Min($_.actual_end_utc_s, $_.actual_start_utc_s + $_.duration_s)} | Measure-Object -Minimum).Minimum - ($reports | ForEach-Object {$_.actual_start_utc_s} | Measure-Object -Maximum).Maximum
            if ($Seconds -ge 302 -and $entry.overlap_s -lt 300) { throw 'Measured overlap is less than 300 s' }
            if (@($reports | ForEach-Object {$_.runs} | Where-Object {$_.error}).Count -gt 0) { throw 'Inference failures recorded; inspect reports' }
            $entry.status = 'PASS'
            Write-Host "Completed $phase; shared measurement window $($entry.overlap_s) s"
        } catch { $entry.status='FAIL'; $entry.error=$_.Exception.Message; throw }
        finally {
            try {
                $samples | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $directory 'memory.json') -Encoding UTF8
            } finally {
                $cleanupErrors = @()
                foreach ($process in $all) {
                    try { $process.Refresh(); if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() } }
                    catch { $cleanupErrors += $_.Exception.Message }
                }
                Remove-Item -LiteralPath $tokenFile -ErrorAction SilentlyContinue
                $entry.cleanup_errors=$cleanupErrors
                Save-Runtime
                if ($cleanupErrors.Count -gt 0) { throw "Owned process cleanup failed: $($cleanupErrors -join '; ')" }
            }
        }
    }
    $runtime.status = 'PASS'
} catch { $runtime.status='FAIL'; $runtime.error=$_.Exception.Message; throw }
finally { Save-Runtime; $env:ECHOSUB_TRANSLATION_TOKEN=$oldToken }
Write-Host "Contention evidence: $output"

