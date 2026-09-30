$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $repo '.nuget/packages'
$desktop = Join-Path $repo 'apps/EchoSub.Desktop/EchoSub.Desktop.csproj'
$smoke = Join-Path $repo 'tests/EchoSub.ProtocolSmoke/EchoSub.ProtocolSmoke.csproj'

function Invoke-Checked {
    param([string] $Program, [string[]] $Arguments)
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program exited with $LASTEXITCODE" }
}

Push-Location $repo
try {
    $cargoArgs = @()
    if ($env:ECHOSUB_OFFLINE -eq '1') { $cargoArgs += '--offline' }
    Invoke-Checked cargo (@('fmt', '--all', '--', '--check'))
    Invoke-Checked cargo (@('test', '--workspace', '--locked') + $cargoArgs)
    Invoke-Checked cargo (@('build', '--workspace', '--locked') + $cargoArgs)

    $restoreArgs = @('restore', $desktop, '--locked-mode')
    if ($env:ECHOSUB_NUGET_SOURCE) { $restoreArgs += @('--source', $env:ECHOSUB_NUGET_SOURCE) }
    Invoke-Checked dotnet $restoreArgs
    Invoke-Checked dotnet @('build', $desktop, '--no-restore')
    Invoke-Checked dotnet @('build', $smoke)
    Invoke-Checked dotnet @('build', (Join-Path $repo 'benchmarks/EchoSub.TranslationProbe/EchoSub.TranslationProbe.csproj'))
    $worker = Join-Path $repo 'target/debug/echosub-worker.exe'
    Invoke-Checked dotnet @('run', '--project', $smoke, '--no-build', '--', $worker)
}
finally {
    Pop-Location
}

