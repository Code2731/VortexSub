# EchoSub

EchoSub is a planned Windows/macOS desktop app for captions and Korean translation of system audio. The repository contains the **M0/T00-01 IPC scaffold**, a standalone Windows WASAPI loopback probe for T00-02, standalone ASR/translation model probes for T00-04.1, and a MOCK caption overlay for T00-04.3. Capture and model inference are not yet connected to the worker or UI; real subtitles are not implemented.

The product specification is in [`docs/01_CONCEPT.md`](docs/01_CONCEPT.md) through [`docs/09_DECISIONS_SOURCES.md`](docs/09_DECISIONS_SOURCES.md). Implementation order and status are in [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md), [`docs/M0_EXECUTION_PLAN.md`](docs/M0_EXECUTION_PLAN.md), and [`docs/STATUS.md`](docs/STATUS.md).

## Build and run on Windows

Requires the pinned Rust 1.90.0 toolchain and .NET SDK 10.0.102. The Avalonia package version is pinned in the desktop project. Run from the repository root:

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
.\scripts\check.ps1
.\scripts\run.ps1
```

You can also double-click `run.cmd` at the repository root. The launcher prints each build stage, reuses restored .NET packages, and waits until the desktop app closes. Errors stay visible. Launcher and desktop startup logs are written under the ignored `logs/` directory. Use `scripts/run.ps1 -NoBuild` to launch already-built binaries, or `-Offline` for cached dependencies. `run.cmd` uses a process-local execution-policy override; it does not change the user's persistent PowerShell policy.

If Explorer's PATH does not contain the SDKs, the launcher also searches `CARGO_HOME/bin`, `%USERPROFILE%/.cargo/bin`, `DOTNET_ROOT`, and `%ProgramFiles%/dotnet`. It prints the selected executables and adds their directories only to the launcher's process PATH.

`check.ps1` formats/checks Rust, runs worker protocol tests, builds both projects, and runs the C# client against the worker. `run.ps1` builds the worker and opens the mock UI. On macOS, run `bash scripts/check.sh` after installing the pinned SDKs. macOS is unverified until it is run on a real Mac.

## Windows loopback probe

```powershell
cargo run -p echosub-capture-windows -- --list
cargo run -p echosub-capture-windows -- --seconds 600
cargo run -p echosub-capture-windows -- --seconds 600 --device-id '{0.0.0.00000000}.{device-guid}'
```

The default mode follows the console render endpoint; `--device-id` pins one render endpoint. `--list` prints active endpoint IDs and names and marks the default. The probe prints the actual mix format, packet/frame counts, device and QPC positions, silence/discontinuity flags, timeouts, and one-second peak/RMS levels. It does not save PCM. It polls endpoint state and reopens after a default-device change or capture failure; a fixed ID never falls back to another endpoint. Use a known system playback source to check level changes. See [`docs/STATUS.md`](docs/STATUS.md) for the measured result and remaining physical device checks.

## Model baseline probes

`benchmarks/model-downloads.json` pins Whisper base/small and Qwen3 4B
Instruct 2507 Q4_K_M by revision, size, and SHA-256. Model downloads require
agreement to their size and licenses. Standalone ASR and local translation
commands, native build prerequisites, fixture formats, and measurement limits
are in [`benchmarks/README.md`](benchmarks/README.md). These probes do not
enable real subtitles in the desktop app.

`scripts/probe-cancellation.ps1 -Backend cpu|cuda` measures actual native
cancellation, same-context recovery, normal release, and owned-child force
termination/recovery. All new ASR and cancellation durations use seconds.
See [`docs/evidence/T00-04.2-windows-cancellation.md`](docs/evidence/T00-04.2-windows-cancellation.md)
for phase-specific measurements and lifetime policy.

`scripts/probe-contention.ps1` repeats ASR alone, translation alone, and both
concurrently for 302 seconds per condition. It records seconds, skipped
requests, failures and sampled memory. See `benchmarks/README.md` for builds,
reproduction and scope.

## MOCK overlay

Run `scripts/run.ps1` and use **샘플 오버레이 표시** in the main window. The overlay has sample English/Japanese and Korean text, a move handle, a resize grip, and main-window controls for width, card opacity, hiding, and resetting placement. It closes with the main window. Windows uses a native adapter to prevent activation and mark the overlay as a tool window.

Run `scripts/probe-overlay.ps1` for the short Windows window probe. It writes a JSON report and rendered PNG under `docs/evidence/`. Exit code 0 covers the executable window checks; inspect `focus_result` separately because a missing foreground HWND is reported as BLOCKED. Actual typing, mouse interaction, game composition, and Mac Spaces remain unverified. See [`docs/OVERLAY_PROBE.md`](docs/OVERLAY_PROBE.md).

For an offline NuGet environment, set `ECHOSUB_NUGET_SOURCE` to a local NuGet feed and `ECHOSUB_OFFLINE=1` for Cargo before running the check script. Package caches are kept under the ignored `.nuget/` directory. The check and desktop launcher scripts do not download models. The proposed future settings are illustrated in [`examples/config.example.toml`](examples/config.example.toml); the current worker does not load that file.

The IPC messages are specified in [`docs/05_CONTRACTS.md`](docs/05_CONTRACTS.md); the implemented v1 envelope has a machine-readable schema in [`schemas/worker-protocol-v1.schema.json`](schemas/worker-protocol-v1.schema.json). The worker responds to `hello`, `ping`, `get_state`, and `shutdown`. Other commands return `UNSUPPORTED_CAPABILITY`.
