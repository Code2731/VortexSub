param([Parameter(Mandatory)] [string] $RunDirectory, [Parameter(Mandatory)] [string] $OutputPath)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $RunDirectory).Path
$runtime=Get-Content (Join-Path $root 'runtime.json') -Raw | ConvertFrom-Json
if ($runtime.status -ne 'PASS') { throw 'Only completed runs may be summarized' }
function Distribution([double[]] $values) {
    if (-not $values.Count) { return $null }
    $sorted=@($values | Sort-Object)
    [ordered]@{count=$sorted.Count;mean_s=($values | Measure-Object -Average).Average;p50_s=$sorted[[Math]::Ceiling($sorted.Count*0.50)-1];p95_s=$sorted[[Math]::Ceiling($sorted.Count*0.95)-1];max_s=$sorted[-1]}
}
$phases=@()
foreach ($phase in $runtime.phases) {
    $directory=Join-Path $root $phase.name
    $engines=@(); $start=$null; $end=$null
    foreach ($name in @('asr','translation')) {
        $path=Join-Path $directory "$name.json"
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $report=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($report.offered -ne ($report.runs.Count+$report.skipped)) { throw 'Admission accounting mismatch' }
        $success=@($report.runs | Where-Object {-not $_.error})
        $errors=@($report.runs | Where-Object {$_.error})
        if ($runtime.seconds -ge 302 -and $report.actual_wall_s -lt 300) { throw 'Client measured less than 300 s' }
        if (@($report.runs.arrival_index | Sort-Object -Unique).Count -ne $report.runs.Count) { throw 'Duplicate arrival index' }
        $timing=if ($name -eq 'asr') {'decode_s'} else {'elapsed_s'}
        $first=@($success | Where-Object {$_.start_s -lt 60})
        $last=@($success | Where-Object {$_.start_s -ge ($report.duration_s-60)})
        $engine=[ordered]@{name=$name;model_id=$report.model_id;report_sha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant();actual_wall_s=$report.actual_wall_s;offered=$report.offered;completed=$success.Count;failed=$errors.Count;skipped=$report.skipped;completed_inside_window=@($success | Where-Object {$_.finish_s -le $report.duration_s}).Count;service=Distribution @($success | ForEach-Object {$_.$timing});start_lag=Distribution @($success.start_lag_s);first_60_service=Distribution @($first | ForEach-Object {$_.$timing});last_60_service=Distribution @($last | ForEach-Object {$_.$timing});first_60_lag=Distribution @($first.start_lag_s);last_60_lag=Distribution @($last.start_lag_s);load_s=$report.load_s;warm_up_s=$report.warm_up_s;errors=@($errors.error)}
        $engines+=$engine
        $engine.arrival_to_finish=Distribution @($success | ForEach-Object {$_.finish_s-$_.scheduled_s})
        $engine.response_after_next_arrival_count=@($success | Where-Object {($_.finish_s-$_.scheduled_s) -gt $report.interval_s}).Count
        $fixtureField=if ($name -eq 'asr') {'fixture_id'} else {'id'}
        $engine.fixture_distribution=@($success | Group-Object -Property $fixtureField | ForEach-Object { [ordered]@{id=$_.Name;count=$_.Count;service=Distribution @($_.Group | ForEach-Object {$_.$timing})} })
        if ($null -eq $start -or $report.actual_start_utc_s -gt $start) { $start=$report.actual_start_utc_s }
        $windowEnd=[Math]::Min($report.actual_end_utc_s, $report.actual_start_utc_s+$report.duration_s)
        if ($null -eq $end -or $windowEnd -lt $end) { $end=$windowEnd }
    }
    $samples=Get-Content (Join-Path $directory 'memory.json') -Raw | ConvertFrom-Json
    if ($runtime.seconds -ge 302 -and ($end-$start) -lt 300) { throw 'Actual shared window is less than 300 s' }
    $samples=@($samples | Where-Object {$_.utc_s -ge $start -and $_.utc_s -le $end})
    $private=@($samples | ForEach-Object {($_.processes.private_bytes | Measure-Object -Sum).Sum})
    $working=@($samples | ForEach-Object {($_.processes.working_set_bytes | Measure-Object -Sum).Sum})
    $gpuMemory=@(); $gpuUtil=@()
    foreach ($sample in $samples) { foreach ($row in $sample.gpu_device_wide_csv) { $cols=$row.Split(','); if ($cols.Length -eq 3 -and $cols[1].Trim() -match '^\d+$') { $gpuMemory += [int]$cols[1].Trim(); $gpuUtil += [int]$cols[2].Trim() } } }
    $asrProof=if (Test-Path (Join-Path $directory 'asr-stderr.log')) { [bool](Select-String -Path (Join-Path $directory 'asr-stderr.log') -SimpleMatch 'whisper_backend_init_gpu: using CUDA0 backend' -Quiet) } else {$null}
    $translationProof=if (Test-Path (Join-Path $directory 'server-stderr.log')) { [bool](Select-String -Path (Join-Path $directory 'server-stderr.log') -Pattern 'offloaded 37/37 layers to GPU' -Quiet) } else {$null}
    $phases += [ordered]@{name=$phase.name;status=$phase.status;measured_overlap_s=$end-$start;memory_samples=$samples.Count;sampled_owned_process_sum_private_peak_bytes=($private | Measure-Object -Maximum).Maximum;sampled_owned_process_sum_working_set_peak_bytes=($working | Measure-Object -Maximum).Maximum;private_first_bytes=$private[0];private_last_bytes=$private[-1];device_wide_gpu_sampled_peak_mib=($gpuMemory | Measure-Object -Maximum).Maximum;device_wide_gpu_utilization_mean_percent=($gpuUtil | Measure-Object -Average).Average;asr_cuda_log_verified=$asrProof;translation_full_offload_log_verified=$translationProof;engines=$engines}
    $phases[-1].memory_sha256=(Get-FileHash -LiteralPath (Join-Path $directory 'memory.json')).Hash.ToLowerInvariant()
    $phases[-1].native_log_sha256=[ordered]@{}
    foreach ($log in @('asr-stderr.log','server-stderr.log')) {
        $logPath=Join-Path $directory $log
        if (Test-Path -LiteralPath $logPath) { $phases[-1].native_log_sha256[$log]=(Get-FileHash -LiteralPath $logPath).Hash.ToLowerInvariant() }
    }
}
$summary=[ordered]@{task='T00-04.4';platform='Windows 26200 x64';cpu='AMD64 Family 25 Model 33; 16 logical processors';gpu='RTX 3080 10240 MiB; driver 617.14';mac='BLOCKED';input_scope='Repeated local synthetic EN/KO speech and authored EN/JA translation, independently driven; no audio-to-translation pipeline';run_directory=$root;runtime_sha256=(Get-FileHash (Join-Path $root 'runtime.json')).Hash.ToLowerInvariant();runtime=$runtime;quantiles='nearest-rank';pending_capacity=0;waiting_policy='Skip expired arrivals, no pending queue; start lag plus skipped demand measure overload. No unbounded queue growth test.';memory_scope='Sampled simultaneous sums of owned processes; GPU metrics are device-wide, include unrelated processes, are not process VRAM peaks.';phases=$phases}
$buildPath=Join-Path $root 'build-inputs.json'
if (Test-Path -LiteralPath $buildPath) { $summary.build_inputs=Get-Content -LiteralPath $buildPath -Raw | ConvertFrom-Json }
$summary.platform=if ($runtime.os) {$runtime.os} elseif ($summary.build_inputs.os) {$summary.build_inputs.os} else {'Not recorded'}
$summary.cpu=if ($runtime.cpu) {$runtime.cpu} elseif ($summary.build_inputs.cpu) {$summary.build_inputs.cpu} else {'Not recorded'}
$summary.gpu=if ($runtime.gpu_inventory) {$runtime.gpu_inventory} elseif ($summary.build_inputs.gpu_inventory) {$summary.build_inputs.gpu_inventory} else {'Not recorded'}
$summary.requirement_ids=@('NF-002','NF-005','NF-008')
$summary.test_id='P0-CONTENTION'
$summary | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "Summary: $OutputPath"
