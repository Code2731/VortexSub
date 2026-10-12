param(
    [Parameter(Mandatory = $true)] [string] $ReportPath,
    [Parameter(Mandatory = $true)] [ValidateSet('Warmup', 'Measurement')] [string] $Phase,
    [Parameter(Mandatory = $true)] [ValidateRange(0, 17302)] [double] $PositionSeconds,
    [Parameter(Mandatory = $true)] [DateTimeOffset] $ObservedUtc
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$reportFile = if ([IO.Path]::IsPathRooted($ReportPath)) { [IO.Path]::GetFullPath($ReportPath) } else { [IO.Path]::GetFullPath((Join-Path $repo $ReportPath)) }
$resultRoot = [IO.Path]::GetFullPath((Join-Path $repo 'benchmarks/results')) + [IO.Path]::DirectorySeparatorChar
if (-not $reportFile.StartsWith($resultRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Report must be under benchmarks/results.' }
if ((Get-Item -LiteralPath $reportFile).Length -gt 4194304) { throw 'Observation report exceeds 4 MiB.' }
$report = Get-Content -LiteralPath $reportFile -Raw | ConvertFrom-Json
$now = [DateTimeOffset]::UtcNow
if ($report.status -ne 'AwaitPlayback' -or $report.phase -ne "Await${Phase}Playback") { throw 'Observation is not waiting for this playback phase.' }
if ($report.run_id -notmatch '^[a-f0-9]{32}$' -or $report.source_url -ne 'https://www.youtube.com/watch?v=lNGFAI7R0PE') { throw 'Unexpected observation identity/source.' }
if (($now - [DateTimeOffset]$report.updated_utc).TotalSeconds -gt 10 -or [DateTimeOffset]$report.updated_utc -gt $now.AddSeconds(2)) { throw 'Observation report is stale.' }
if ($ObservedUtc -lt [DateTimeOffset]$report.capture_ready_utc -or $ObservedUtc -gt $now.AddSeconds(2) -or ($now - $ObservedUtc).TotalSeconds -gt 10) { throw 'Playback observation must follow capture readiness and be recent.' }
if ($Phase -eq 'Measurement' -and [Math]::Abs($PositionSeconds - [double]$report.source_start_s) -gt 5) { throw 'Measurement playback must start within 5 seconds of the planned position.' }
$marker = @{ RunId = $report.run_id; Phase = $Phase; SourceUrl = $report.source_url;
    PositionSeconds = $PositionSeconds; ObservedUtc = $ObservedUtc.ToUniversalTime().ToString('o') }
$markerFile = $reportFile + '.' + $Phase.ToLowerInvariant() + '.playback.json'
$temporary = $markerFile + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($marker | ConvertTo-Json))
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    # Move without overwrite. Earlier evidence must remain intact.
    [IO.File]::Move($temporary, $markerFile)
    Write-Host "Recorded $Phase playback declaration at $PositionSeconds seconds. This does not verify continuous playback."
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
