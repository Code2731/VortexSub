# EchoSub

[한국어](README.md) | **English**

EchoSub aims to provide system-audio transcription and Korean translation in a Windows/macOS desktop app.

**M0 probes, the M1 core, and M2 transcription paths are in development.** A diagnostic worker connects real Windows loopback→Silero VAD→Whisper→source history. File transcription is also supported. A diagnostic source history/overlay UI is connected. UUID session start/pause/resume/stop control, history UUIDs, and session-relative audio times are connected. Translation and full product wire migration are pending. macOS has not been verified on a real device.

## Build and run on Windows

Requires the pinned **Rust 1.90.0** toolchain and **.NET SDK 10.0.102**. The Avalonia version is pinned in the desktop project. Run from the repository root:

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
.\scripts\check.ps1
.\scripts\run.ps1
```

- `check.ps1` checks Rust formatting, runs existing tests, builds the Rust/C# projects, and runs the C#↔Rust IPC smoke check.
- `run.ps1` builds the worker and desktop app, then opens the MOCK UI.
- You can also **double-click `run.cmd`** at the root. It displays build progress and errors, then waits for the app to close. Startup logs are written to the ignored `logs/` directory.
- Use `scripts/run.ps1 -NoBuild` for existing binaries, or `-Offline` for cached dependencies.

If the SDKs are missing from PATH, the launcher also searches `CARGO_HOME/bin`, `%USERPROFILE%/.cargo/bin`, `DOTNET_ROOT`, and `%ProgramFiles%/dotnet`. It displays the selected SDK paths and adds them to its process PATH. The PowerShell execution-policy override in `run.cmd` also applies only to that process.

For offline NuGet, set `ECHOSUB_NUGET_SOURCE` to a local package source and `ECHOSUB_OFFLINE=1` for Cargo before running the check script. Package caches live in the ignored `.nuget/` directory. The check and app launcher scripts do not download models.

On macOS, install the pinned SDKs and run `bash scripts/check.sh`. Platform support remains unverified until results are obtained on a real Mac.

## Live source diagnostic UI

Run `./run.cmd -Live -Offline`, wait for model Ready, select an output device and source language, then click **세션 시작** (Start session). Final source history and an overlay are connected; translation and partial output are pending. After Pause/Stop and owner cleanup, select a retained session and use **TXT 저장 / 원문 SRT 저장** (Save TXT / source SRT). See [export, UTC, and timing](docs/HISTORY_EXPORT.md). You can also clear in-memory history for the selected session after confirmation. Saved files remain intact. Actual clicks and source rendering remain unverified. See [execution and verification scope](docs/LIVE_UI.md).

## Windows system-audio capture probe

```powershell
cargo run -p echosub-capture-windows -- --list
cargo run -p echosub-capture-windows -- --seconds 600
cargo run -p echosub-capture-windows -- --seconds 600 --device-id '{0.0.0.00000000}.{device-guid}'
```

`--list` prints active render endpoint IDs/names and marks the default. Default mode follows the console render endpoint; `--device-id` pins one endpoint and never falls back to another device.

The WASAPI loopback probe prints the actual audio format, packet/frame counts, device/QPC positions, silence/discontinuity flags, timeouts, and one-second peak/RMS levels. It does not save PCM. It polls device state and reopens after a default-device change or capture failure. Use a known system playback source to check level changes. See [implementation status](docs/STATUS.md) for measured results and remaining device-switch/detach checks.

## ASR and translation model probes

The [model catalogue](benchmarks/model-downloads.json) pins Whisper base/small and Qwen3-4B-Instruct-2507 Q4_K_M by revision, size, and SHA-256. Downloads require agreement to their size and licenses. Model files and generated audio are excluded from Git.

See the [benchmark guide](benchmarks/README.md) for build tools, model/fixture preparation, commands, and measurement scope. Native ASR requires CMake/LLVM/MSVC; CUDA execution also requires CUDA Toolkit. Default workspace builds omit native inference.

- `scripts/probe-asr.ps1`: standalone ASR measurements on CPU or CUDA.
- `scripts/probe-translation.ps1`: translation measurements using an installed local `llama-server`.
- `scripts/probe-cancellation.ps1`: actual native cancellation, same-context recovery, normal release, and owned-child force termination/recovery. [Cancellation and lifetime report](docs/evidence/T00-04.2-windows-cancellation.md)
- `scripts/probe-contention.ps1`: base/small alone, translation alone, and each ASR model with translation for 302 seconds per condition. [Concurrent load report](docs/evidence/T00-04.4-windows-contention.md)

New ASR, translation, cancellation, and contention timing outputs use **seconds**. Model loading and processing times are distinct from caption latency. Synthetic speech and authored translation fixtures are diagnostic data. Natural-speech quality, game coexistence, long-term stability, and macOS need separate validation; production model adoption remains deferred.

## Common audio core (M1 in progress)

`crates/audio-core/` converts 44.1/48 kHz mono/stereo to 16 kHz mono and
provides 512-sample frames, a session sample timeline, a 12-second rolling
buffer, and bounded immutable PCM snapshots. Run fixtures without OS/model
dependencies using `cargo test -p echosub-audio-core`. Segmentation, eight-second
chunking, a packet-stop watchdog, and actual Silero worker file inference are
implemented. Continuous live VAD is also connected; diagnostic source UI is connected; visual acceptance is unverified. See the [audio core](docs/AUDIO_CORE.md) and [VAD contract and validation](docs/VAD_CORE.md).

## Pipeline state and job queues (M1 in progress)

`crates/pipeline-core/` provides latest-partial replacement, final-priority queues,
stale-result rejection, translation deadlines and terminal states, and versioned
history capped at 1,000 records. Run mock fixtures with
`cargo test -p echosub-pipeline-core`. Bounded worker events, versioned history
snapshots, and the C# client are connected. Diagnostic history UI is connected.
Actual HTTP integration and visual acceptance are pending. Default history is empty; generated diagnostic events
require `--mock-pipeline`. See the [state and queue contract](docs/PIPELINE_CORE.md)
and [worker delivery and recovery](docs/WORKER_DELIVERY.md).

## Native worker file transcription (M2 in progress)

Run `scripts/probe-worker-asr.ps1 -Backend cpu -Offline` with the existing Whisper
base model and synthetic WAV fixtures. It checks real native ASR→IPC history,
epoch cancellation/restart, and shutdown during inference. A dedicated inference
thread reuses its context while control requests stay responsive. Input is 16 kHz
mono WAV, at most 8 seconds; capture, VAD, and translation are disabled. All measured
durations are seconds. See the [owner contract](docs/WORKER_ASR.md) and
[CPU evidence](docs/evidence/T02-01a-windows-worker-asr.md).

Run `scripts/probe-worker-vad.ps1 -Offline` with the consented Silero v6.0 model
and ONNX Runtime 1.22.0 CPU assets. It checks real speech segmentation, silence
and generated tone/noise suppression, two utterances, epoch resets, and hash
errors. One extra Korean fixture candidate produced an empty ASR result and
remains Failed; **the quality gate has not passed**. See the
[VAD contract](docs/WORKER_VAD.md) and [evidence](docs/evidence/T02-01b-windows-worker-vad.md).

## Live worker source diagnostics (M2 in progress)

`scripts/probe-worker-live-asr.ps1 -Offline` briefly plays existing English TTS
and checks real loopback→continuous Silero→Whisper→history. It checks Stop/restart
during inference, timeline gaps, hash failures, and active termination. Subtitle UI,
translation, and partial inference are pending; game/natural-speech quality gates
remain unpassed. See [live execution and contracts](docs/WORKER_LIVE_ASR.md).

## MOCK caption overlay

`scripts/probe-worker-capture.ps1 -Offline` briefly plays an existing TTS WAV and
checks real WASAPI reception, 16 kHz normalization, repeated Start/Stop, and active
shutdown. Capture diagnostics report `live_asr=false` and produce no captions or
history. See [the owner contract and unverified scope](docs/WORKER_CAPTURE.md).

`scripts/probe-capture-startup.ps1 -Offline` compares fresh-worker startup and
cleanup across render endpoints without models or playback. Use
`-DeviceId default -Rounds 10` for the default selection only.
See [execution and measurement limits](docs/CAPTURE_STARTUP_PROBE.md).

Use `-Rounds 20` for additional startup/stop measurements. Unready startup fails
at 10 seconds with API phase and elapsed seconds recorded; native termination
must be checked separately. See [startup observations](docs/evidence/T02-02c-windows-capture-startup.md).

Run `scripts/run.ps1` and select **샘플 오버레이 표시** (show sample overlay) in the main window. It shows sample English/Japanese source text and Korean translations, with move/resize handles and controls for width, opacity, hiding, and resetting placement. It closes with the main window. The Windows adapter applies non-activation and tool-window properties.

`scripts/probe-overlay.ps1` runs the short Windows window probe and writes JSON/rendered PNG evidence under `docs/evidence/`. Exit code 0 covers executable window-property checks. Inspect `focus_result` separately: unavailable foreground HWND is reported as `BLOCKED`. Actual typing/mouse interaction, game composition, and Mac Spaces remain unverified. See the [overlay validation procedure](docs/OVERLAY_PROBE.md).

## Documentation and protocol

- Product specification: `docs/01`–`09`, from [concept](docs/01_CONCEPT.md) to [decisions and sources](docs/09_DECISIONS_SOURCES.md)
- Implementation order: [implementation plan](docs/IMPLEMENTATION_PLAN.md), [M0 execution plan](docs/M0_EXECUTION_PLAN.md)
- Results and unverified items: [implementation status](docs/STATUS.md)
- Contributor guide: [Repository Guidelines](AGENTS.md)
- IPC contract: [contract document](docs/05_CONTRACTS.md), [v1 JSON schema](schemas/worker-protocol-v1.schema.json)

The worker supports `hello`, `ping`, `get_state`, `get_history`, and `shutdown`. Mock and native file diagnostics require their explicit modes; normal MOCK mode has no session control. The live launcher enables UUID `start_session`/`pause_session`/`resume_session`/`stop_session`. See [control contract and verification](docs/SESSION_CONTROL.md). The [configuration example](examples/config.example.toml) illustrates future settings; the current worker does not load it.


