# T03-02a Windows desktop translation (2026-10-01)

Windows AMD64, existing local dependencies. TR-001/003/004 and UI presentation
subset. No new model/runtime downloads; no capture or desktop interaction probe.

## Changes

- Live launcher enables the existing opt-in HTTP adapter; translation stays off
  until configured. Idle/cleanup guards protect server/model changes.
- UI exposes endpoint, actual catalog model selection, preparation/state/errors
  and disable. Translation settings failures leave source/model state intact.
- History and overlay consume typed snapshots. Overlay requires current UUID,
  internal namespace/epoch and finalized applied revision/request identity.
- Pure presentation helper shares the five-second expiry policy with UI timer;
  late completion never renews lifetime. Translation owners also gate export/clear.

## Commands and results

`scripts/check.ps1` with `ECHOSUB_OFFLINE=1` and local NuGet source: PASS,
Rust formatting/138 tests/workspace build, C# builds (zero warnings/errors),
existing C# HTTP/IPC smoke and 14 new presentation assertions.

`scripts/build-model-probe.ps1 -Backend cpu -Package echosub-worker -Vad -Offline`:
PASS, native CPU ASR + VAD worker build.

Presentation assertions cover pending/failed/bypass source preservation, completed
translation, TTL expiry/no resurrection, applied revision renewal, missing request
ID, stale source revision, partial, old UUID/epoch, Pause and history formatting.

## Not measured

Actual window clicks/rendering/screenshots, multiple-server model selection in UI,
game focus, long text clipping/DPI, live ASR→HTTP→screen latency, server failure
during live capture and macOS. Existing semantic quality failures remain open.
No UI or E2E acceptance is inferred from fixture/build success.

Next: T03-02b server recovery/model selection diagnostics and bounded translation
display presets; collect manual UI evidence when available. Two-card presentation,
OS credential storage and M3 quality/latency acceptance remain separate work.
