$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content (Join-Path $repo 'benchmarks/translation-research-runtimes.json') -Raw | ConvertFrom-Json
if ($manifest.asset_status -notin @('download_approved', 'installed_verified')) { throw 'Runtime consent is pending' }
$root = [IO.Path]::GetFullPath((Join-Path $repo $manifest.destination))
$modelsRoot = [IO.Path]::GetFullPath((Join-Path $repo 'models')) + [IO.Path]::DirectorySeparatorChar
if (-not $root.StartsWith($modelsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime must stay within models' }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$curl = Join-Path $env:SystemRoot 'System32/curl.exe'
foreach ($asset in $manifest.assets) {
    if ($asset.file -match '[/\\]' -or $asset.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $asset.url -notlike 'https://github.com/ggml-org/llama.cpp/releases/download/b11146/*') { throw 'Invalid pinned runtime asset' }
    $archive = Join-Path $root $asset.file
    if (-not (Test-Path -LiteralPath $archive)) {
        $partial = "$archive.part"
        Write-Host "Downloading $($asset.file): $($asset.bytes) bytes"
        & $curl --fail --location --retry 2 --silent --show-error --output $partial $asset.url
        if ($LASTEXITCODE -ne 0) { throw 'Runtime download failed' }
        if ((Get-Item -LiteralPath $partial).Length -ne $asset.bytes -or
            (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) { throw 'Runtime size/hash mismatch' }
        Move-Item -LiteralPath $partial -Destination $archive
    }
    if ((Get-Item -LiteralPath $archive).Length -ne $asset.bytes -or
        (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) { throw 'Existing archive mismatch; preserve it for review' }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $root $entry.FullName))
            if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive entry escapes runtime directory' }
        }
    } finally { $zip.Dispose() }
    Expand-Archive -LiteralPath $archive -DestinationPath $root -Force
    Write-Host "Verified and extracted $($asset.file)"
}
Get-ChildItem -LiteralPath $root -Filter llama-server.exe -Recurse | Select-Object -ExpandProperty FullName
