param(
    [ValidateSet('en', 'ja')] [string] $Language = 'en',
    [string] $AudioPath,
    [string] $SourceUrl,
    [ValidateRange(0, 86400)] [int] $DeclaredDurationSeconds = 0,
    [ValidateRange(0, 86400)] [int] $SourceStartSeconds = 600,
    [string] $OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($AudioPath -and $SourceUrl) { throw 'Specify a local file or a playback URL, not both.' }
if ($DeclaredDurationSeconds -gt 0 -and $SourceStartSeconds + 1800 -gt $DeclaredDurationSeconds) {
    throw 'Selected 30-minute interval exceeds declared media duration.'
}
if (-not $OutputDirectory) { $OutputDirectory = 'benchmarks/results/natural-preparation-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID }
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $repo 'benchmarks/results')) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($resultsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be under Git-excluded benchmarks/results.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null

function Read-CatalogAsset([string] $Manifest, [string] $Collection, [string] $Id) {
    $catalog = Get-Content -LiteralPath (Join-Path $repo $Manifest) -Raw | ConvertFrom-Json
    $asset = $catalog.$Collection | Where-Object id -eq $Id | Select-Object -First 1
    if (-not $asset) { throw "Asset missing from manifest: $Id" }
    $path = [IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'benchmarks') $asset.path))
    $exists = Test-Path -LiteralPath $path -PathType Leaf
    $actual = if ($exists) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null }
    [pscustomobject]@{id=$Id; manifest=$Manifest; path=$asset.path; exists=$exists;
        expected_sha256=$asset.sha256; actual_sha256=$actual; hash_matches=($exists -and $actual -eq $asset.sha256)}
}

$assets = @(
    Read-CatalogAsset 'benchmarks/model-downloads.json' 'models' 'whisper-base'
    Read-CatalogAsset 'benchmarks/translation-research-models.json' 'models' 'hy-mt2-1.8b-q4_k_m'
    Read-CatalogAsset 'benchmarks/vad-assets.json' 'assets' 'silero-v6'
    Read-CatalogAsset 'benchmarks/vad-assets.json' 'assets' 'ort-win-x64'
)
$binaries = @('models/runtime-b11146/llama-server.exe', 'target/model-probe-cuda/release/echosub-worker.exe',
    'apps/EchoSub.Desktop/bin/Debug/net10.0/EchoSub.Desktop.exe',
    'apps/EchoSub.Desktop/bin/Debug/net10.0/EchoSub.Desktop.dll') | ForEach-Object {
    $path = Join-Path $repo $_
    $exists = Test-Path -LiteralPath $path -PathType Leaf
    [pscustomobject]@{path=$_; exists=$exists;
        sha256= $(if ($exists) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null });
        last_write_utc=$(if ($exists) { (Get-Item -LiteralPath $path).LastWriteTimeUtc.ToString('o') } else { $null })}
}
$mediaInput = [ordered]@{kind='not_provided'; declared_duration_s=$DeclaredDurationSeconds; measured_duration_s=$null;
    natural_speech_verified=$false; source=$null; sha256=$null;
    selected_start_s=$SourceStartSeconds; selected_end_s=($SourceStartSeconds + 1800); selection_status='candidate_not_audited'}
if ($AudioPath) {
    $audio = Get-Item -LiteralPath $AudioPath
    if ($audio.PSIsContainer) { throw 'Audio input must be a file.' }
    $mediaInput.kind = 'local_file'; $mediaInput.source = $audio.FullName
    $mediaInput.sha256 = (Get-FileHash -LiteralPath $audio.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
} elseif ($SourceUrl) {
    $url = [Uri]$SourceUrl
    if (-not $url.IsAbsoluteUri -or $url.Scheme -notin @('http', 'https')) { throw 'Playback URL must use HTTP or HTTPS.' }
    $mediaInput.kind = 'user_playback_url'; $mediaInput.source = $SourceUrl
}
$settings = $null
$settingsStatus = 'not_found'
$settingsPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'EchoSub/settings.json'
if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
    try {
        if ((Get-Item -LiteralPath $settingsPath).Length -gt 16384) { throw 'Settings exceed app limit.' }
        $saved = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        # Read only the nonsecret options required by this evaluation profile.
        $settings = [ordered]@{Language=$saved.Language; OverlayWidth=$saved.OverlayWidth; CardOpacity=$saved.CardOpacity;
            ShowSource=$saved.ShowSource; Partial=$saved.Partial; PartialTranslation=$saved.PartialTranslation; CosmeticRevisions=$saved.CosmeticRevisions}
        $settingsStatus = 'read_only_snapshot'
    } catch { $settingsStatus = 'unreadable_or_invalid' }
}
$expected = [ordered]@{Language=$Language; OverlayWidth=760; CardOpacity=0.75; ShowSource=$false;
    Partial=$true; PartialTranslation=$true; CosmeticRevisions=$true}
$differences = @($expected.Keys | Where-Object { $null -eq $settings -or $settings[$_] -ne $expected[$_] })
$ignoreFile = Join-Path $repo '.gitignore'
$head = & git -c "core.excludesFile=$ignoreFile" -c "safe.directory=$repo" -C $repo rev-parse --short HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot read repository HEAD.' }
$dirty = @(& git -c "core.excludesFile=$ignoreFile" -c "safe.directory=$repo" -C $repo status --porcelain).Count -gt 0
if ($LASTEXITCODE -ne 0) { throw 'Cannot read repository status.' }
$report = [ordered]@{
    schema_version=1; prepared_utc=[DateTime]::UtcNow.ToString('o'); purpose='Windows 30-minute natural-speech preparation; not a completed test'
    git_head=$head; working_tree_dirty=$dirty; os=[Environment]::OSVersion.VersionString; process_x64=[Environment]::Is64BitProcess
    planned_duration_s=1800; input=$mediaInput; assets=$assets; binaries=$binaries
    installed_files_match=(@($assets | Where-Object {-not $_.hash_matches}).Count -eq 0 -and @($binaries | Where-Object {-not $_.exists}).Count -eq 0)
    runtime_profile=[ordered]@{asr='whisper-base'; asr_backend='cuda'; adaptive_partials=$true; translation='hy-mt2-1.8b-q4_k_m'; input_profile='hymt2-greedy';
        decode_window=$false; short_padding=$false; supported_preview=$false; isolated_context=$false; caption_timing=$true}
    expected_ui=$expected; saved_ui=$settings; saved_ui_status=$settingsStatus; ui_changes_required=$differences
    launch_command='powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-live.ps1 -TranslationModel hymt2 -AsrBackend cuda -FastPartials -CaptionTiming'
    pending=@('Natural speech/provenance and measured duration', 'Apply and verify UI options before Start',
        'Actual model profile and device selection', '30-minute capture/ASR/translation/reading observation', 'Qualified quality/latency evaluation')
    note='No download, audio playback, inference or capture. Personal settings are not changed. Binary hashes identify files but do not prove they match current source.'
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'preparation.json') -Encoding UTF8
Write-Host "Preparation saved: $output"
Write-Host "Installed files match: $($report.installed_files_match); input: $($mediaInput.kind); UI changes required: $($differences.Count)"
