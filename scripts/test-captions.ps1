param(
    [switch] $SkipRender,
    [string] $OutputDirectory,
    [string] $BaselineReport,
    [string[]] $ReplayInput
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$python = Join-Path $repo 'models/tabby/venv/Scripts/python.exe'
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
    $python = (Get-Command python -ErrorAction Stop).Source
}
$arguments = @('-X', 'utf8', (Join-Path $PSScriptRoot 'test-captions.py'))
if ($SkipRender) { $arguments += '--skip-render' }
if ($OutputDirectory) { $arguments += @('--output-directory', $OutputDirectory) }
if ($BaselineReport) { $arguments += @('--baseline-report', $BaselineReport) }
foreach ($trace in $ReplayInput) { $arguments += @('--replay-input', $trace) }
& $python @arguments
exit $LASTEXITCODE
