param(
    [ValidateSet('cpu', 'cuda')] [string] $Backend = 'cuda',
    [ValidateRange(1, 10)] [int] $Rounds = 3,
    [string] $ServerPath,
    [switch] $Adaptive,
    [switch] $DecodeWindow,
    [switch] $PadShortPartials,
    [switch] $SupportedPreview,
    [string] $PythonPath,
    [ValidateSet('qwen', 'hymt2')] [string] $TranslationModel = 'qwen',
    [string] $FixtureManifest,
    [string] $FixtureId,
    [string] $OutputDir,
    [ValidateSet('base', 'small')] [string] $AsrModel = 'base'
)
$ErrorActionPreference = 'Stop'
if ($PadShortPartials -and ($Adaptive -or $DecodeWindow)) { throw 'Padding comparison cannot combine with -Adaptive or -DecodeWindow' }
if ($SupportedPreview -and ($Adaptive -or $DecodeWindow -or $PadShortPartials)) { throw 'Supported preview comparison cannot combine with other comparisons' }
$repo = Split-Path $PSScriptRoot -Parent
if (-not $PythonPath) { $PythonPath = Join-Path $repo 'models/tabby/venv/Scripts/python.exe' }
if (-not (Test-Path -LiteralPath $PythonPath)) { throw 'Supply an existing Python executable. This probe installs nothing.' }
Push-Location $repo
try {
    & "$PSScriptRoot/build-model-probe.ps1" -Backend $Backend -Package echosub-worker -Vad -Offline
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
    & cargo build -p echosub-translation --locked --offline
    if ($LASTEXITCODE -ne 0) { throw 'HTTP warmup probe build failed' }
    $arguments = @((Join-Path $repo 'scripts/probe-paced-translation.py'), '--backend', $Backend, '--rounds', "$Rounds")
    $arguments += @('--translation-model', $TranslationModel)
    $arguments += @('--asr-model', $AsrModel)
    if ($FixtureManifest) { $arguments += @('--fixture-manifest', $FixtureManifest) }
    if ($FixtureId) { $arguments += @('--fixture-id', $FixtureId) }
    if ($OutputDir) { $arguments += @('--output-dir', $OutputDir) }
    if ($Adaptive) { $arguments += '--adaptive' }
    if ($DecodeWindow) { $arguments += '--decode-window' }
    if ($PadShortPartials) { $arguments += '--pad-short-partials' }
    if ($SupportedPreview) { $arguments += '--supported-preview' }
    if ($ServerPath) { $arguments += @('--server', $ServerPath) }
    & $PythonPath -X utf8 @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Paced translation measurement failed; completed reports/logs retained.' }
} finally { Pop-Location }
