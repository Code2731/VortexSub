# Repository Guidelines

## Project Structure

The repository has an Avalonia C# UI in `apps/EchoSub.Desktop/`, a Rust process in `crates/worker/`, WASAPI owner/probe in `crates/capture-windows/`, reusable native adapter in `crates/asr-whisper/`, ASR harness in `crates/model-probe/`, and pure audio core in `crates/audio-core/`, state/queue core in `crates/pipeline-core/`, and VAD adapter in `crates/vad-silero/`. Translation diagnostics and model/fixture manifests live in `benchmarks/`. C# IPC smoke checks live in `tests/EchoSub.ProtocolSmoke/`; worker integration tests in `crates/worker/tests/`. IPC schema lives in `schemas/`; plans, requirements, status, and evidence live in `docs/`. Put the native ScreenCaptureKit bridge under `native/macos-capture/` only after its platform probe.

## Build, Test, and Development

Run `scripts/check.ps1` on Windows for formatting, Rust tests, both builds, and the C#↔Rust smoke check; run `bash scripts/check.sh` on macOS once a Mac is available. `scripts/run.ps1` builds the worker and opens the mock UI on Windows. Use `cargo run -p echosub-capture-windows -- --list` to find render endpoints and `--seconds 600` for the loopback probe. The equivalent individual checks are `cargo test --workspace --locked` and `dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj`. The scripts pin the local package cache and disable Avalonia build telemetry. Record which OS and hardware each command actually covers.

For diagnostics, see `benchmarks/README.md`. Use `scripts/probe-asr.ps1 -Backend cpu|cuda`, `scripts/probe-translation.ps1`, `scripts/probe-cancellation.ps1`, `scripts/probe-contention.ps1`, `scripts/probe-worker-asr.ps1`, `scripts/probe-worker-vad.ps1`, `scripts/probe-worker-capture.ps1`, `scripts/probe-worker-live-asr.ps1`, or `scripts/probe-capture-startup.ps1`. Native ASR requires CMake/LLVM/MSVC and CUDA Toolkit for CUDA; default builds omit native inference. Obtain download consent before model/runtime downloads. Keep models, generated audio, and full benchmark results out of Git.

## Coding Style

Use Rust's standard formatting and idiomatic `snake_case` for functions/modules and `UpperCamelCase` for types. In C#, use four-space indentation, `UpperCamelCase` for public types and members, and `camelCase` for locals and parameters. Keep pure state and data contracts platform-independent; isolate WASAPI and ScreenCaptureKit behind platform adapters. Keep worker stdout reserved for NDJSON protocol messages and send diagnostics to stderr. Bound queues and avoid blocking, allocation-heavy, or inference work in audio callbacks.

## Tests and Validation

For IPC changes, extend `crates/worker/tests/protocol.rs` and `tests/EchoSub.ProtocolSmoke/`; run the check script. Add fixture tests with the audio pipeline in M1, including stale-result identity (`session_id`, `epoch`, `segment_id`, `source_revision`). No coverage percentage is specified. Label mocks clearly and record unrun platform/device checks explicitly in `docs/STATUS.md`.

## Commits and Pull Requests

Use short imperative Conventional Commit subjects such as `feat: add worker handshake` or `fix: reject stale translations`. Pull requests should explain scope and affected requirement IDs, list exact validation commands and results, identify untested platforms, and include screenshots for UI changes.
