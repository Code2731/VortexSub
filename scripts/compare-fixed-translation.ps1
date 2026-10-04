param(
    [Parameter(Mandatory)] [string] $Trace,
    [ValidateRange(1, 3)] [int] $Rounds = 3,
    [switch] $NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
        $env:PATH = (Join-Path $env:USERPROFILE '.cargo/bin') + ';' + $env:PATH
    }
    $env:NUGET_PACKAGES = Join-Path $repo '.nuget/packages'
    $env:AVALONIA_TELEMETRY_OPTOUT = '1'
    if (-not $NoBuild) {
        cargo build -p echosub-worker -p echosub-translation --locked --offline
        if ($LASTEXITCODE -ne 0) { throw 'Replay worker build failed' }
        dotnet build tests/EchoSub.NativeAsrSmoke/EchoSub.NativeAsrSmoke.csproj --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Replay client build failed' }
    }
    & "$repo/models/tabby/venv/Scripts/python.exe" -X utf8 "$PSScriptRoot/compare-fixed-translation.py" --trace $Trace --rounds $Rounds
    if ($LASTEXITCODE -ne 0) { throw 'Fixed replay comparison failed; inspect retained output' }
}
finally { Pop-Location }
