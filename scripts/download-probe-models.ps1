param([ValidateSet('all', 'asr')] [string] $Scope = 'all')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$catalog = Get-Content (Join-Path $repo 'benchmarks/model-downloads.json') -Raw | ConvertFrom-Json
$destinationRoot = Join-Path $repo 'models'
New-Item -ItemType Directory -Force -Path $destinationRoot | Out-Null
$curl = Join-Path $env:SystemRoot 'System32/curl.exe'
foreach ($entry in $catalog.models) {
    if ($Scope -eq 'asr' -and $entry.role -ne 'asr') { continue }
    if ($entry.file -match '[/\\]' -or $entry.revision -notmatch '^[0-9a-f]{40}$' -or
        $entry.sha256 -notmatch '^[0-9a-f]{64}$' -or $entry.repository -notmatch '^[\w.-]+/[\w.-]+$') {
        throw "Invalid pinned download entry: $($entry.id)"
    }
    $destination = Join-Path $destinationRoot $entry.file
    if (Test-Path -LiteralPath $destination) {
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -eq $entry.sha256) {
            Write-Host "Verified existing $($entry.id)"; continue
        }
        throw "Existing file has a different hash; preserve/review it before replacing: $destination"
    }
    $partial = "$destination.part"
    $url = "https://huggingface.co/$($entry.repository)/resolve/$($entry.revision)/$($entry.file)?download=true"
    Write-Host "Downloading $($entry.id): $($entry.bytes) bytes, $($entry.license)"
    & $curl --fail --location --retry 2 --silent --show-error --output $partial $url
    if ($LASTEXITCODE -ne 0) { throw "Download failed ($LASTEXITCODE): $($entry.id)" }
    if ((Get-Item -LiteralPath $partial).Length -ne $entry.bytes -or
        (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {
        throw "Download size/hash mismatch: $partial"
    }
    Move-Item -LiteralPath $partial -Destination $destination
    Write-Host "Verified $($entry.id) SHA-256: $($entry.sha256)"
}
