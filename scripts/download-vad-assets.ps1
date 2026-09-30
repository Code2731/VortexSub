param([switch] $Consent)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$catalogue = Get-Content (Join-Path $repo 'benchmarks/vad-assets.json') -Raw | ConvertFrom-Json
$root = Join-Path $repo 'models'
New-Item -ItemType Directory -Path $root -Force | Out-Null
$curl = Join-Path $env:SystemRoot 'System32/curl.exe'
foreach ($entry in $catalogue.assets) {
    $archive = $entry.id -eq 'ort-win-x64'
    $file = if ($archive) { $entry.archive } else { $entry.file }
    $hash = if ($archive) { $entry.archive_sha256 } else { $entry.sha256 }
    $size = if ($archive) { $entry.archive_bytes } else { $entry.bytes }
    if ($file -match '[/\\]' -or $hash -notmatch '^[0-9a-f]{64}$') { throw 'Invalid VAD asset catalogue' }
    $path = Join-Path $root $file
    if (-not (Test-Path -LiteralPath $path)) {
        if (-not $Consent) { throw 'Obtain download consent, then invoke with -Consent (about 74.70 MB)' }
        $part = "$path.part"
        try {
            & $curl --fail --location --retry 2 --silent --show-error --output $part $entry.url
            if ($LASTEXITCODE -ne 0) { throw 'VAD asset download failed' }
            if ((Get-Item -LiteralPath $part).Length -ne $size -or (Get-FileHash -LiteralPath $part -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'VAD asset size/hash mismatch' }
            Move-Item -LiteralPath $part -Destination $path
        }
        finally { if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part } }
    }
    if ((Get-Item -LiteralPath $path).Length -ne $size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw 'Existing VAD asset differs; preserve it for review' }
    if ($archive) {
        $dll = [IO.Path]::GetFullPath((Join-Path (Join-Path $repo 'benchmarks') $entry.path))
        if (-not $dll.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime target escapes models directory' }
        if (-not (Test-Path -LiteralPath $dll)) { Expand-Archive -LiteralPath $path -DestinationPath $root }
        if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { throw 'Runtime DLL hash mismatch' }
    }
    Write-Host "Verified $($entry.id) ($($entry.license))"
}
