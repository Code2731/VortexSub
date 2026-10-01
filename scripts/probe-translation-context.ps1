param(
    [ValidateRange(1, 10)] [int] $Rounds = 3,
    [ValidateSet('baseline', 'strict', 'isolated', 'readable', 'examples', 'korean', 'production', 'plain')]
    [string[]] $Profiles = @('baseline', 'production'),
    [switch] $NoBuild,
    [string] $Fixtures,
    [switch] $OwnerCheck
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
        $env:PATH = (Join-Path $env:USERPROFILE '.cargo/bin') + ';' + $env:PATH
    }
    if (-not $NoBuild) {
        cargo build -p echosub-translation --locked --offline
        if ($LASTEXITCODE -ne 0) { throw 'Translation request export build failed' }
    }
    $python = Join-Path $repo 'models/tabby/venv/Scripts/python.exe'
    if (-not (Test-Path -LiteralPath $python)) { throw 'Existing local Python runtime is required' }
    $probeArgs = @('--rounds', "$Rounds", '--profiles') + $Profiles
    if ($Fixtures) { $probeArgs += @('--fixtures', $Fixtures) }
    if ($OwnerCheck) { $probeArgs += '--owner-check' }
    & $python -X utf8 "$PSScriptRoot/probe-translation-context.py" @probeArgs
    if ($LASTEXITCODE -ne 0) { throw 'Prompt/context comparison failed; inspect retained results' }
}
finally { Pop-Location }
