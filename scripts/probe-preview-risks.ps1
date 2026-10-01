param([ValidateRange(1, 10)] [int] $Rounds = 3, [switch] $NoBuild)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    $python = Join-Path $repo 'models/tabby/venv/Scripts/python.exe'
    $texts = Join-Path $repo 'benchmarks/preview-risk-texts.json'
    $fixtures = Join-Path $repo 'benchmarks/fixtures/local-tts/preview-risks'
    $manifest = Join-Path $fixtures 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifest)) {
        & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$PSScriptRoot/generate-diagnostic-fixtures.ps1" -TextsPath $texts -OutputDirectory $fixtures -NoSilence
        if ($LASTEXITCODE -ne 0) { throw 'Controlled fixture generation failed' }
    }
    if (-not $NoBuild) {
        & "$PSScriptRoot/build-model-probe.ps1" -Backend cuda -Package echosub-worker -Vad -Offline
        & cargo build -p echosub-translation --locked --offline
        if ($LASTEXITCODE -ne 0) { throw 'HTTP warmup build failed' }
    }
    $generated = Get-Content -LiteralPath $manifest -Encoding UTF8 -Raw | ConvertFrom-Json
    foreach ($item in (Get-Content -LiteralPath $texts -Encoding UTF8 -Raw | ConvertFrom-Json).texts) {
        $fixture = @($generated.fixtures | Where-Object id -eq $item.id)
        if ($fixture.Count -ne 1 -or $fixture[0].reference -ne $item.text) { throw "Fixture missing or outdated: $($item.id)" }
    }
    $output = Join-Path $repo ('benchmarks/results/preview-risks-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID)
    New-Item -ItemType Directory -Path $output | Out-Null
    foreach ($item in (Get-Content -LiteralPath $texts -Encoding UTF8 -Raw | ConvertFrom-Json).texts) {
        $caseOutput = Join-Path $output $item.id
        & $python -X utf8 "$PSScriptRoot/probe-paced-translation.py" --backend cuda --rounds $Rounds --supported-preview --fixture-manifest $manifest --fixture-id $item.id --output-dir $caseOutput
        if ($LASTEXITCODE -ne 0) { throw "Comparison failed for $($item.id); completed reports retained: $output" }
    }
    & $python -X utf8 "$PSScriptRoot/summarize-preview-risks.py" $output --catalog $texts
    if ($LASTEXITCODE -ne 0) { throw 'Risk summary failed' }
    Write-Host "Preview controls: $output"
}
finally { Pop-Location }
