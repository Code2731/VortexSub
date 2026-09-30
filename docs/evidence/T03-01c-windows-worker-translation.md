# T03-01c Windows worker translation

2026-10-01. Windows build 26200, AMD64 16 logical processors, RTX 3080 10 GiB.
Requirements TR-001/003/004 and SEC-001/002 subset. No downloads.

## Implementation and checks

Opt-in worker commands for asynchronous local model discovery/configuration and
disable. Final jobs dispatch to HTTP owner; completion uses the existing full-key
pipeline. Per-decode language is captured; same-language jobs bypass. Pipeline
limits/deadline, failure/source preservation, epoch cancellation and HTTP return
reservation are retained. [Contract](../WORKER_TRANSLATION.md).

- `scripts/check.ps1` with offline Cargo/local NuGet: formatting, **138 Rust tests**,
  workspace and C# builds, C#↔Rust IPC PASS; C# zero warnings/errors.
- New Rust fixtures: two admission/language tests and six real local HTTP worker
  IPC tests. Final-only requests, two-source context, failed/recovered requests,
  Pause/Resume stale completion applied=false, no old-epoch context, Korean bypass,
  queue overflow, Stop terminalization, missing model, opt-in/config validation.
- C# IPC fixture: real local HTTP catalog, typed translated final/full identity,
  401 not retried, final source retained, private error body omitted, disable PASS.
- `scripts/probe-translation.ps1 -Worker -Offline`: native CPU build and actual
  worker Whisper base→owned local Qwen server→history PASS. Three English finals
  translated; one Korean final Bypassed, HTTP completed_jobs=3. No pending records.
  Idle fixture mode disable PASS; server/client shutdown handled by owners.

Ignored report `translation-local-20261001-073244/report.json`: file admission to
terminal history 0.858677/0.723038/0.684020 s (English), 0.672460 s (Korean bypass).
Ping maximum 0.000781 s; server preparation 1.962984 s measured separately.
Fixture manifest SHA-256 `e2a606cf8dc419df501ca20af256e770ce574ae9a8ddda54b4048d676a472d3e`.
ASR/translation model hashes remain those in the pinned model-download manifest;
server hash/backend logs are in the ignored runtime report.

## Corrections and limits

Initial native build found language metadata being read after releasing the task;
fixed by retaining the bounded language string before PCM/task release. Initial
C# fixture blocked on a zero-length GET body; fixed to skip zero-length reads.
Final checks and actual native worker run passed after both corrections.

Semantic quality remains unpassed: en-02 misrenders the gate location, and en-03
changes the return condition into departure. These are valid protocol responses,
not accepted translations. Raw subtitles/results remain ignored.

File-to-history timing excludes real-time audio capture and UI render latency.
Windows live E2E, desktop translated captions/settings, translated export, actual
server-side cancellation, OS secret storage, macOS and full M3 quality/soak gates
remain unverified or unimplemented. Source generation in IPC fixtures is MOCK;
only the native file probe uses actual Whisper inference.

Next T03-02a: desktop local server settings/model readiness and translated
history/overlay, with source retained while translation fails or expires.
