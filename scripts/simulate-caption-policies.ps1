param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repo ('benchmarks/results/caption-simulation-' +
        [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
$outputPath = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repo $OutputDirectory }))
if (Test-Path -LiteralPath $outputPath) { throw "Output directory already exists: $outputPath" }
$oldCache = $env:NUGET_PACKAGES
$oldTelemetry = $env:AVALONIA_TELEMETRY_OPTOUT
$oldDotnetTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
Push-Location $repo
try {
    $env:NUGET_PACKAGES = Join-Path $repo '.nuget/packages'
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    & dotnet build tests/EchoSub.ProtocolSmoke/EchoSub.ProtocolSmoke.csproj --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Simulation build failed' }
    & dotnet tests/EchoSub.ProtocolSmoke/bin/Debug/net10.0/EchoSub.ProtocolSmoke.dll --caption-simulation $outputPath
    if ($LASTEXITCODE -ne 0) { throw 'Simulation oracle failed its negative controls' }
    @{
        head = (& git rev-parse HEAD)
        branch = (& git branch --show-current)
        dirty_paths = @(& git status --short)
        sdk = (& dotnet --version)
        processor = $env:PROCESSOR_IDENTIFIER
        command = 'scripts/simulate-caption-policies.ps1'
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputPath 'execution.json') -Encoding UTF8
} finally {
    Pop-Location
    $env:NUGET_PACKAGES = $oldCache
    $env:AVALONIA_TELEMETRY_OPTOUT = $oldTelemetry
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $oldDotnetTelemetry
}
