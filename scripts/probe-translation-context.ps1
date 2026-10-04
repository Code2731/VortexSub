param(
    [ValidateRange(1, 10)] [int] $Rounds = 3,
    [ValidateSet('baseline', 'strict', 'isolated', 'readable', 'examples', 'korean', 'production', 'plain', 'gemma', 'exaone', 'hymt2')]
    [string[]] $Profiles = @('baseline', 'production'),
    [switch] $NoBuild,
    [string] $Fixtures,
    [string] $Catalog,
    [string] $ModelId,
    [string[]] $ContextConditions,
    [switch] $OwnerCheck,
    [switch] $PrepareOnly,
    [ValidateSet('existing', 'greedy', 'hymt2-recommended')] [string] $Sampling = 'existing',
    [switch] $CompareSampling,
    [switch] $VerifyInputContract,
    [ValidateSet('existing', 'separated', 'source-only')] [string] $ExaoneInput = 'existing'
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
    if ($Catalog) { $probeArgs += @('--catalog', $Catalog) }
    if ($ModelId) { $probeArgs += @('--model-id', $ModelId) }
    if ($ContextConditions) { $probeArgs += @('--context-conditions') + $ContextConditions }
    if ($OwnerCheck) { $probeArgs += '--owner-check' }
    if ($PrepareOnly) { $probeArgs += '--prepare-only' }
    $probeArgs += @('--sampling', $Sampling)
    if ($CompareSampling) { $probeArgs += '--compare-sampling' }
    $probeArgs += @('--exaone-input', $ExaoneInput)
    if ($VerifyInputContract) { $probeArgs += '--verify-input-contract' }
    & $python -X utf8 "$PSScriptRoot/probe-translation-context.py" @probeArgs
    if ($LASTEXITCODE -ne 0) { throw 'Prompt/context comparison failed; inspect retained results' }
}
finally { Pop-Location }
