# T03-01b Windows local HTTP translation

2026-10-01. Windows build 26200, AMD64 16 logical processors, RTX 3080 10 GiB.
Related requirements: TR-001/002/003/004, SEC-001/002; UT-012, IT-003 subset.

## Implementation

Pinned reqwest 0.12.28 (default features off, JSON only) and Tokio 1.53.1;
resolved dependencies were available in the local cache. No model/runtime downloads.
Numeric loopback HTTP, no proxies or redirect following, bounded streaming body,
2 s connection limit and shared remaining deadline. One retry maximum for
connection/5xx; numeric Retry-After for 429 only if within budget. Authentication,
schema and incomplete output failures are terminal. Error diagnostics omit
server bodies, subtitle text and credentials.

HTTP owner: dedicated thread, one command/result slot, reservation through
completion poll; cancellation and shutdown drop the HTTP future and join.
Source key/request ID is preserved. [Contract](../TRANSLATION_CONTRACT.md).

## Executed validation

- `scripts/check.ps1` with offline Cargo/local NuGet: formatting, **130 Rust tests**,
  workspace builds and C#↔Rust IPC PASS; C# builds zero warnings/errors.
- Translation subset: 20 fixtures PASS, including real local sockets, redirect
  target not contacted, 401/404/invalid JSON, 5xx retry maximum, 429 budget,
  delayed headers/body, pre-cancel/in-flight cancel, fixed/chunked oversized
  responses, same-language bypass and active owner shutdown.
- `scripts/probe-translation.ps1 -Contract -Offline`: installed llama-server
  and consented Qwen3 4B Instruct 2507 Q4_K_M, **20/20 complete responses**.
  Ignored report: `translation-local-20261001-065936/report.json`.
  Mean HTTP-owner completion 0.132907 s, maximum 0.349789 s; cold first request
  included. Server preparation 2.739289 s measured separately. Model selection
  used an actual `/v1/models` ID. No context was included in this corpus run.
  Model SHA-256 `3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597`;
  server executable SHA-256 `8ef22515398d453b2e1e3557726e32a33c94f9df230272bfc439a74b709997c9`.

The first launch was denied by the filesystem sandbox; the authorized local
server run succeeded after escalation. Temporary API-key file was removed and
the owned server stopped by the probe's finally block. Reports/audio/logs remain
ignored. The final owner refinement also counts preparation time against the
budget; the real run preceded this small accounting change.

## Quality and integration limits

Protocol success is not semantic acceptance. `en-03` changes the return condition
to a departure condition; `ja-03` omits the return condition. Thus quality gate
remains false. Other output needs full manual review against the corpus.

Worker scheduling/history/stale-result application, UI captions, secrets in OS
storage, LM Studio/oMLX compatibility, context quality, live audio E2E and macOS
are not validated here. Environmental proxy injection was not separately run;
the client explicitly configures no_proxy. HTTP cancellation does not prove
server-side inference cancellation. M3/M4 gates remain open.

Next T03-01c: connect the owner to final-only worker jobs, preserving source on
translation errors and applying completions through the existing full-key core.
