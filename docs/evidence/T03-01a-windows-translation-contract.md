# T03-01a Windows translation contract

2026-10-01. Windows build 26200, AMD64 16 logical processors. Related requirements:
TR-001/002/004, SEC-001, UT-007/012 and IT-003 schema subset.

## Changes

Added `crates/translation/` to the workspace using existing pinned dependencies.
Implemented numeric loopback endpoint normalization, bounded model catalog,
TranslationJob-to-request conversion, same-language bypass, remaining deadline,
Unicode/context budgets, escaped JSON messages and complete plain-text response
validation. Existing pipeline key and request ID are retained. No secrets or
full subtitle content are logged. [Contract](../TRANSLATION_CONTRACT.md).

## Executed checks

- `cargo test -p echosub-translation --offline`: eight new fixtures PASS.
- `scripts/check.ps1` with `ECHOSUB_OFFLINE=1` and local NuGet source:
  Rust formatting, **118 Rust tests**, workspace build, Desktop/ProtocolSmoke/
  TranslationProbe builds and C#↔Rust IPC smoke PASS. C# builds: zero warnings/errors.
- `scripts/build-model-probe.ps1 -Backend cpu -Package echosub-worker -Vad -Offline`:
  final native worker build PASS, including the T02-04c token changes.
- NativeAsrSmoke and CaptureSmoke `dotnet build --no-restore`: PASS,
  zero warnings/errors.

## Scope and next work

Tests are deterministic contract fixtures; no HTTP server is contacted here.
Endpoint validation is not a network redirect/proxy test. Actual LM Studio/oMLX
compatibility, HTTP cancellation/retry, model selection, worker final-job dispatch,
OS secret storage, UI translation and macOS execution remain unimplemented or
unverified. A valid response shape is not a translation quality judgment.

T03-01b will connect a bounded HTTP owner with redirects/proxies disabled, actual
model selection/connectivity diagnostics and deadline-aware request handling.
Subsequent integration applies results through the existing full-key pipeline
and retains source on translation failure. M3/M4 progression gates remain open.
