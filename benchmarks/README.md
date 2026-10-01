# Model baseline probes (T00-04.1)

For the growing-prefix CPU Whisper and actual HTTP partial-translation experiment,
run `scripts/probe-streaming-translation.ps1`. It uses existing assets and Python 3.12
without downloads, capture, playback, or UI rendering. Source admission is a
precomputed MOCK replay; see [scope and results](../docs/STREAMING_TRANSLATION.md).

These are standalone diagnostics. The desktop still displays MOCK captions;
neither probe is connected to capture, worker IPC, or the overlay.

## Windows reproduction

To call WASAPI initialization alone, without starting capture or loading models:

```powershell
cargo run -p echosub-capture-windows --locked --offline -- --initialize-only
```

This prints the selected endpoint, mix format, and elapsed seconds around a
single `IAudioClient::Initialize` call with the worker's shared/event loopback
parameters. It has no worker startup deadline; a successful standalone call
does not establish that worker startup is reliable.

```powershell
# Download only after agreeing to the size and licenses in model-downloads.json.
.\scripts\download-probe-models.ps1 -Scope all
# Windows PowerShell 5.1 is required for System.Speech.
powershell.exe -NoProfile -File scripts/generate-diagnostic-fixtures.ps1
cargo run -p echosub-model-probe -- validate benchmarks/fixtures/local-tts/manifest.json
.\scripts\probe-asr.ps1 -Backend cpu -Offline
.\scripts\probe-asr.ps1 -Backend cuda -Offline
.\scripts\probe-translation.ps1
.\scripts\probe-translation.ps1 -Contract -Offline
.\scripts\probe-translation.ps1 -Worker -Offline
```

ASR native builds require MSVC, CMake, LLVM `libclang.dll`, and, for CUDA,
`CUDA_PATH`. `build-model-probe.ps1` finds the VS 2022 Community CMake and
`Program Files/LLVM/bin`; other installations need PATH/`LIBCLANG_PATH`.
It generates Windows ABI bindings. CUDA uses Ninja and overrides the binding's
Unix `-fPIC` flag for MSVC; Visual Studio's CUDA compiler identification failed
in the initial attempt on this machine.
Windows native Release explicitly uses `/O2 /DNDEBUG` because the binding's
initial generated configuration omitted optimization. Ninja is also selected
for CPU when available. Inspect the generated build commands for SIMD flags.
CPU and CUDA runner builds use separate `target/model-probe-*` folders.
CUDA defaults to `CMAKE_CUDA_ARCHITECTURES=native`; set that environment
variable to a specific compute target if detection fails (RTX 3080: `86`).
The builder records its native configuration and cleans `whisper-rs-sys`
when that configuration changes; Cargo does not track every upstream CMake
environment variable. First use of a preexisting target also rebuilds native
code. The target folder must be inside this repository.
`-NoBuild` reuses that backend's executable. Default workspace builds do not
enable native inference and do not require native tooling.

Translation requires an installed llama-server supporting the model's GGUF.
`-Contract` selects the Rust HTTP owner with an eight-second job deadline,
bounded responses and strict completion checks. The earlier C# baseline remains
the default. Rust reports include fixture SHA-256 and preserve per-case failures;
all successful HTTP responses still require semantic review.
`-Worker` instead builds the native CPU worker and checks three English file
finals through ASR→HTTP→history plus one Korean same-language bypass. It preserves
per-fixture checkpoints and measures file admission to terminal history in
seconds, excluding live capture and UI rendering.
Pass `-ServerPath <exe>` if unavailable on PATH. The script uses loopback,
an ephemeral API key, a 4096-token context, and requests GPU offload. It stops
its own server on completion. Inspect logs for actual layer placement.

## Inputs and interpretation

The worker VAD probe now checks an exact-zero 8-second file 75 times (600 seconds
of file PCM), with zero VAD/ASR/history expected before the speech corpus. This is
not a real-time loopback silence acceptance result. Native completion diagnostics
include input/nonzero sample counts and valid timed tokens. See
[token alignment evidence](../docs/evidence/T02-04c-windows-token-alignment.md).

`model-downloads.json` pins repository revisions, filenames, sizes, hashes,
and license sources. Downloads use temporary files and verify before rename.
Models, generated audio, and full results are ignored by Git.

Audio manifests use schema version 1 and `fixtures`: unique `id`, relative
WAV `path`, SHA-256, `language` (en/ja/ko), exact `reference`, `source`, `usage`,
`kind` (speech/synthetic_tts/silence/background), and sorted, nonoverlapping
`speech_segments_ms` pairs. WAV must be mono 16 kHz PCM16 or finite normalized
float32, at most 120 seconds, with a 128 MiB total PCM budget. Provide permitted natural speech for quality
evaluation. Local TTS rights are not established for redistribution.

Generated English/Korean TTS and silence test execution only; Japanese speech
is absent. TTS segment bounds cover whole utterances, including padding.
English uses WER; Japanese/Korean use Unicode-scalar CER without NFC/NFKC.
Empty references report unexpected text separately. Translation references
require meaning review; string equality is not a quality test.

Reports distinguish load, warm-up, decode, and request times. They are not
caption latency. Working-set peaks are cumulative per process; VRAM peaks,
game coexistence and Mac execution remain untested. Joint load is covered by the separate T00-04.4 probe below.

## Cancellation and lifetime probe (T00-04.2)

```powershell
.\scripts\probe-cancellation.ps1 -Backend cuda -Offline
.\scripts\probe-cancellation.ps1 -Backend cpu -Offline
```

Use `-NoBuild` only with a current executable. Default is ten iterations per
model for each cancellation phase (encoder entry and a native abort check),
restarting on one warmed context; ten iterations to cancel and
release a warmed context normally; kill an owned busy child and transcribe in
a fresh child. Queues hold one job/result. Model/state and immutable PCM stay
with the owner until the synchronous native call returns. Each job receives a
new one-shot atomic cancellation token; a used token cannot be reset/reused.

The probe repeats/crops verified speech PCM to 120 seconds to keep native work
active. It observes encoder entry or native abort checks before requesting cancellation, checks
callback acknowledgement, rejects cancelled output, compares recovery text to
warm-up, and confirms the next decode starts after the old call returns.
Pre-cancel performs no native work, and reused tokens are rejected. Normal
shutdown requests cancellation after encoder entry. Force termination waits
for a native abort check and only targets child
handles created by this probe. Readiness snapshots and full-return marker files
bracket that termination; they are not an instruction-level trace.
Pre-encoder spectrogram processing is not separately instrumented. The pinned
GPU path may check cancellation after a compute phase completes, so late-phase
timings must not be presented as a bound for early-phase cancellation.

All new ASR/cancellation duration fields and progress messages use seconds
(`decode_s`, `cancel_to_return_s`, etc.). Historical T00-04.1 raw reports retain
their original units. New ASR segment coordinates are also seconds; worker IPC
timestamp contracts are unaffected. Cancellation reports do not establish
capture Stop timing, UI stale-result rejection, or a guaranteed abort deadline.
Verify the requested backend against parent and child native logs.

## Concurrent load probe (T00-04.4)

Build the CUDA ASR harness first with `scripts/build-model-probe.ps1 -Backend
cuda -Offline` (set `CMAKE_CUDA_ARCHITECTURES` for the actual GPU), and build
`benchmarks/EchoSub.TranslationProbe/EchoSub.TranslationProbe.csproj`.
Then run:

```powershell
.\scripts\probe-contention.ps1 -ServerPath 'C:\path\to\llama-server.exe'
.\scripts\summarize-contention.ps1 -RunDirectory 'benchmarks/results/contention-RUN-ID' -OutputPath 'benchmarks/results/summary.json'
```

Default phases are base alone, small alone, translation alone, base with
translation, and small with translation: each lasts 302 seconds, with at
least 300 seconds of measured overlap required. `-Seconds 5 -Phases
base-concurrent` is a harness check, not an acceptance run. Load and one
warm-up request are excluded. Both clients wait for the same future UTC gate
and then measure durations with monotonic clocks. A final in-flight request
may finish after the window; summaries count these separately.

Each engine receives a synthetic arrival every 0.25 seconds. It executes
one request at a time, with no pending queue; expired arrivals are skipped
and counted. Fixture selection follows arrival index. Reports include service
time, admission lag, first/last-minute distributions, failures and counts.
This measures bounded overload, not unbounded backlog or the future partial
ASR scheduler. ASR and translation inputs are independent fixed fixtures;
translation is not fed live ASR output. Repeated translation prompts can
benefit from the native runtime's default cache behavior.

Owned processes are hidden, translation binds only to authenticated loopback,
and cleanup targets only process handles created by the script. Full results
and native logs remain ignored. Memory samples contain simultaneous process
sums and device-wide GPU readings; device-wide memory includes unrelated
applications and is not process VRAM peak. The wrapper checks exit status,
inference failures and duration. Confirm CUDA/full-offload logs separately.
Natural speech, macOS, game coexistence and two-hour stability remain separate
gates. All new timing outputs use seconds.

## Native worker ASR integration

For startup isolation without inference or playback, run
`scripts/probe-capture-startup.ps1 -Offline`. It compares fresh worker processes
using default and pinned render endpoints, records failed cases and cleanup,
and continues the matrix before returning failure. Reports and endpoint manifests
remain ignored. See [startup probe scope](../docs/CAPTURE_STARTUP_PROBE.md).

Run `scripts/probe-worker-asr.ps1 -Backend cpu -Offline` to build the optional
native worker and exercise it through the C# IPC client. It uses existing Whisper
base and local TTS fixtures, checks source history, silence/hash rejection,
10 epoch cancellation/restart rounds, and shutdown during inference. Reports
under `benchmarks/results/worker-asr-*/` omit transcripts. No capture, Silero VAD,
translation, or subtitle UI is involved. See [the contract](../docs/WORKER_ASR.md)
and [Windows CPU evidence](../docs/evidence/T02-01a-windows-worker-asr.md).

`scripts/probe-worker-vad.ps1 -Offline` adds actual Silero v6.0 CPU segmentation.
Pinned model/runtime assets and hashes are in `vad-assets.json`. Obtain download
consent before `scripts/download-vad-assets.ps1 -Consent`; without that switch,
the script verifies existing assets only. Reports retain counts/timings, including
empty-ASR failures; a passing smoke result is not a quality gate. See
[worker VAD](../docs/WORKER_VAD.md).

`scripts/probe-worker-capture.ps1 -Offline` plays the existing en-01 WAV briefly
and checks real worker loopback PCM normalization, three Start/Stop cycles,
shutdown and parent EOF. It saves counts/timings only and stops its own playback.
This is a PCM diagnostic, with live ASR disabled. See
[capture scope and bounds](../docs/WORKER_CAPTURE.md).

Add `-Rounds 20` (1–100) for repeated startup measurements. Reports include
opening durations and bounded phase observations, immediate Stop after Start,
and failure state when a probe aborts. The worker fails unready Opening at
10 seconds and keeps ownership until actual join; this does not bound native
API return. See [startup evidence](../docs/evidence/T02-02c-windows-capture-startup.md).

`scripts/probe-worker-live-asr.ps1 -Offline` uses the same consented assets and
plays whole English TTS after capture ready, with 0.5 seconds of leading and
1 second of trailing silence. Other system audio is not isolated. It checks real loopback
VAD→Whisper final history, cancellation/restart, monotonic gaps, VAD hash failure,
post-playback observation, active shutdown and parent EOF. Reports omit transcripts; generated
WAVs stay under ignored results. No download is performed. Subtitle UI, translation,
partial inference and the quality gate remain pending. See [live contracts](../docs/WORKER_LIVE_ASR.md).

Add `-Sessions` to the live ASR probe to exercise UUID start/pause/resume/stop, retained history, a fresh session, source TXT/SRT export after cleanup, and scoped history clearing with saved-file preservation. This uses existing consented assets and performs no downloads. This mode's ignored report directory includes source records and exported files; keep it out of Git. It does not verify UI rendering or natural-speech quality. See [session controls](../docs/SESSION_CONTROL.md) and [export](../docs/HISTORY_EXPORT.md).

부분 전사 통합 확인: `scripts/probe-worker-live-asr.ps1 -NoBuild -Offline -Partials` (기존 영어 en-10 TTS 재생). Sessions와 동시에 지정하지 않는다. 동일 구간 부분→확정 revision·Pause/Resume/Stop·export/clear를 검사하며 화면/자연 음성 품질 수용은 별도다. [계약](../docs/WORKER_PARTIAL_ASR.md).

Use `-Boundaries` for digital silence and a long utterance built from four copies of the existing en-10 TTS fixture with edge padding trimmed. This mode observes actual chunking/continuation and reports `overlap_segments_removed`. It preserves silence failures and does not establish natural-speech quality or effective dedup. Choose one of Sessions/Partials/Boundaries. See [reconciliation](../docs/ASR_RECONCILIATION.md).


## Translation engine comparison

Use scripts/probe-translation-engines.ps1 -Warmup 1 -Rounds 3 after installing the consented Tabby environment. Both configurations receive paired plain/context cases through the Rust HTTP owner. See [conditions and limits](../docs/TRANSLATION_ENGINES.md).

## Paced partial ASR scheduling

`scripts/probe-partial-scheduling.ps1 -WavPath <absolute mono 16 kHz WAV>`
uses an existing Whisper base model (override `-ModelPath`) and a 1–8 second file.
It compares legacy/current admission through the real CPU native owner at 1.0 s
and 0.25 s intervals. JSON includes applied/ignored completions, source updates,
decode time, first partial and final time in seconds. Results/transcripts stay
under ignored `benchmarks/results/`; no models or runtimes are downloaded.
Known file end replaces VAD; HTTP, capture and UI are outside this measurement.

Use `-Backend cpu|cuda -CurrentOnly -Rounds 3 -FirstPartialSeconds 1.0` to compare
the current scheduler with repeated native owners. Separate target directories
keep CPU and CUDA builds independent. CUDA requires the installed toolkit and
CUDA-capable build; verify `using CUDA0 backend` in native output before interpreting
the report as GPU execution. `load_s` is excluded from replay timings. There is no
warmup, and first-request timing is a file-probe control, not a product setting.
The default first request remains 0.8 seconds and default rounds remain one.
Final-source correctness, partial corrections, HTTP/game contention and rendered
caption latency must be assessed separately from decode throughput.

`scripts/probe-paced-translation.ps1 -Backend cuda -Rounds 3` compares 1.0/0.5-second
requests through the actual native owner, HTTP owner and production scheduler.
It verifies existing consented assets, joins three local English TTS files and
owns a private llama server on port 18087. No capture or download occurs.
Interval order alternates between rounds. Reports include first source/stable
prefix/translation times, actual request texts and native/HTTP completion events.
Translation is warmed once; native owners are fresh per condition. Results,
transcripts, logs and device-wide GPU samples remain Git-ignored.
An already installed Python can be supplied with `-PythonPath`; the default is
the consented Tabby environment. The wrapper builds `translation-probe.exe`
offline for warmup. Completed conditions
are saved before subsequent runs. No quality or screen-latency gate is implied.
An existing local server can instead be used with `probe-partial-scheduling.ps1`
`-TranslationEndpoint <numeric loopback URL> -TranslationModel <id>`; the token
comes from `ECHOSUB_TRANSLATION_TOKEN`, never the report. This mode compares
current admission at 1.0/0.5 seconds and ignores `-CurrentOnly`.

Add `-Trim` for the explicit timed-token feasibility probe using the existing
7.605-second joined English fixture beginning “We should take the left path.”
It compares exact token boundaries in 3/4-second prefixes and rejects missing,
zero-length word or inconsistent times. A successful command means the diagnostic
completed, not that trimming is safe; `trimmed: null` records rejection.
Production audio ranges are unchanged.

For actual Qwen translation using the same source trace on two worker versions,
run `scripts/probe-streaming-translation.ps1 -ReferenceWorker <existing worker.exe>`.
The reference binary must implement the same replay protocol. Reports include
trace/binary hashes, final-only controls and execution order. This is offline
MOCK ASR admission with actual HTTP, not native scheduling or screen latency.
See [comparison and trim evidence](../docs/evidence/translation-units-trim-windows-20261001.md).

## SenseVoice candidate file comparison

After explicit download consent, install the pinned model and private Windows CPU
runtime with `models/tabby/venv/Scripts/python.exe -X utf8 scripts/probe-sensevoice.py
--download-approved`. Normal probe runs never download. Run without that flag for
three whole/prefix passes over the local fixture manifest. The native model probe's
`prefix` command uses identical 0.8/0.256-second input boundaries for Whisper.
`scripts/summarize-asr-candidates.py <sense-report> <whisper-report> --output <json>`
checks matching fixtures/rounds/boundaries and applies the same character scoring.
All time values are seconds. Prefix availability is a file simulation, not live
caption latency. See [configuration and limits](../docs/ASR_CANDIDATE_COMPARISON.md).
