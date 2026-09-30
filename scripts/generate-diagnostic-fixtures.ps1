$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path $repo 'benchmarks/fixtures/local-tts'
New-Item -ItemType Directory -Force -Path $root | Out-Null
$texts = Get-Content (Join-Path $repo 'benchmarks/diagnostic-texts.json') -Encoding UTF8 -Raw | ConvertFrom-Json
$synthesizer = New-Object System.Speech.Synthesis.SpeechSynthesizer
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$fixtures = @()
try {
    foreach ($item in $texts.texts) {
        $voice = $synthesizer.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.TwoLetterISOLanguageName -eq $item.language } | Select-Object -First 1
        if (-not $voice) { Write-Host "SKIPPED: no installed voice for $($item.language)"; continue }
        $synthesizer.SelectVoice($voice.VoiceInfo.Name)
        $file = Join-Path $root "$($item.id).wav"
        $synthesizer.SetOutputToWaveFile($file, $format)
        $synthesizer.Speak($item.text)
        $synthesizer.SetOutputToNull()
        # SpeechSynthesizer writes PCM16 mono. Determine RIFF data length from its chunk table.
        $bytes = [IO.File]::ReadAllBytes($file)
        $offset = 12
        $dataBytes = 0
        while ($offset + 8 -le $bytes.Length) {
            $chunk = [Text.Encoding]::ASCII.GetString($bytes, $offset, 4)
            $length = [BitConverter]::ToUInt32($bytes, $offset + 4)
            if ($chunk -eq 'data') { $dataBytes = $length; break }
            $offset += 8 + $length + ($length % 2)
        }
        if ($dataBytes -eq 0) { throw "No WAV data for $($item.id)" }
        $durationMs = [uint64][Math]::Floor($dataBytes * 1000 / 32000)
        $fixtures += [ordered]@{
            id=$item.id; path="$($item.id).wav"; sha256=(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
            language=$item.language; reference=$item.text; source="local Windows TTS: $($voice.VoiceInfo.Name)"
            usage='Local diagnostics only; TTS output redistribution rights not established'
            kind='synthetic_tts'; speech_segments_ms=,@(0,$durationMs)
        }
    }
    $silenceFile = Join-Path $root 'silence.wav'
    $stream = [IO.File]::Create($silenceFile)
    $writer = New-Object IO.BinaryWriter($stream)
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $writer.Write([uint32](36 + 96000))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $writer.Write([uint32]16)
        $writer.Write([uint16]1); $writer.Write([uint16]1); $writer.Write([uint32]16000)
        $writer.Write([uint32]32000); $writer.Write([uint16]2); $writer.Write([uint16]16)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data')); $writer.Write([uint32]96000)
        $writer.Write((New-Object byte[] 96000))
    } finally { $writer.Dispose(); $stream.Dispose() }
    $fixtures += [ordered]@{id='silence';path='silence.wav';sha256=(Get-FileHash $silenceFile -Algorithm SHA256).Hash.ToLowerInvariant();language='en';reference='';source='generated zero PCM';usage='Project-generated diagnostic data';kind='silence';speech_segments_ms=@()}
    $manifestJson = [ordered]@{schema_version=1;note='Synthetic TTS only. Whole-utterance speech ranges include TTS padding; not a human-annotated quality corpus.';fixtures=$fixtures} | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText((Join-Path $root 'manifest.json'), $manifestJson, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Generated $($fixtures.Count) diagnostic fixtures in $root"
} finally { $synthesizer.Dispose() }
