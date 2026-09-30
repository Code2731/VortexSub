# EchoSub

[한국어](README.md) | **English**

EchoSub aims to provide system-audio transcription and Korean translation in a Windows/macOS desktop app.

**M0 probes and the M1 common core are currently in development.** The repository includes UI↔Rust worker IPC, a standalone Windows capture probe, independent ASR/translation/cancellation/contention probes, and a MOCK caption overlay. Capture, inference, and translation are not connected to the worker/UI yet; the app does not provide real-time subtitles. macOS has not been verified on a real device.

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
dependencies using `cargo test -p echosub-audio-core`. Probability-driven segmentation, eight-second chunking, and a packet-stop watchdog are implemented. Actual Silero inference and worker/UI
integration are not implemented yet. See the [audio core](docs/AUDIO_CORE.md) and [VAD contract and validation](docs/VAD_CORE.md).

## MOCK caption overlay

Run `scripts/run.ps1` and select **샘플 오버레이 표시** (show sample overlay) in the main window. It shows sample English/Japanese source text and Korean translations, with move/resize handles and controls for width, opacity, hiding, and resetting placement. It closes with the main window. The Windows adapter applies non-activation and tool-window properties.

`scripts/probe-overlay.ps1` runs the short Windows window probe and writes JSON/rendered PNG evidence under `docs/evidence/`. Exit code 0 covers executable window-property checks. Inspect `focus_result` separately: unavailable foreground HWND is reported as `BLOCKED`. Actual typing/mouse interaction, game composition, and Mac Spaces remain unverified. See the [overlay validation procedure](docs/OVERLAY_PROBE.md).

## Documentation and protocol

- Product specification: `docs/01`–`09`, from [concept](docs/01_CONCEPT.md) to [decisions and sources](docs/09_DECISIONS_SOURCES.md)
- Implementation order: [implementation plan](docs/IMPLEMENTATION_PLAN.md), [M0 execution plan](docs/M0_EXECUTION_PLAN.md)
- Results and unverified items: [implementation status](docs/STATUS.md)
- Contributor guide: [Repository Guidelines](AGENTS.md)
- IPC contract: [contract document](docs/05_CONTRACTS.md), [v1 JSON schema](schemas/worker-protocol-v1.schema.json)

The current worker responds to `hello`, `ping`, `get_state`, and `shutdown`. Other commands return `UNSUPPORTED_CAPABILITY`. The [configuration example](examples/config.example.toml) illustrates future settings; the current worker does not load it.


