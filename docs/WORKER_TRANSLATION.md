# Worker local translation (T03-01c)

## Enable and configure

Add `--diagnostic-translation` to an existing native ASR/live worker or explicit
`--mock-pipeline` diagnostic. `hello.capabilities.translation=true` describes
the opt-in HTTP adapter; it does not indicate a ready server. Default builds
and launch commands perform no translation HTTP until explicitly configured.

After hello, send:

```json
{"endpoint":"http://127.0.0.1:1234/v1/","model_id":"actual/server/model-id"}
```

as `configure_translation` parameters. Only numeric loopback endpoints are
accepted. `model_id` may be omitted only when the server returns one model.
Configuration is asynchronous: accepted→translator.state Preparing→Ready/Failed.
`get_state.translator` exposes model IDs, selection, catalog_pending, in_flight,
completed_jobs and sanitized last_error. Tokens are read from process environment
`ECHOSUB_TRANSLATION_TOKEN`, never configuration IPC; OS secret storage is pending.

Configure/disable only when session Idle, capture stopped and ASR/HTTP/queued
jobs drained. `disable_translation` takes `{}` and disables future admissions;
retained translations remain in history. Invalid parameters preserve the current
configuration. Reconfigure after a failed catalog lookup to retry preparation.

## Source and results

Only final source admits a translation. Native decode language is captured into
the job; Korean→Korean is Bypassed with no HTTP job. Previous two same-epoch
final sources supply context. One active/two waiting jobs, eight-second deadline
from final registration and the [HTTP limits](TRANSLATION_CONTRACT.md) apply.
Partial/no-speech/overlap-only records do not send requests.

Owner completions must match source session/epoch/segment/revision and request ID.
Core completion checks cancellation/deadline again. Pause/Stop/epoch changes
terminalize pending history and cancel HTTP; execution stays reserved until the
owner completion returns. Stop reaches Idle after both ASR and HTTP reservations
drain. History export/clear also waits for translation completion.

`translation.completed` reports full identity, elapsed_s, applied and typed error;
`translation.updated` publishes the applied history record. Failure preserves
final source; no server response body or token is copied into error diagnostics.
Catalog failure does not stop ASR. Later request failures leave the configured
adapter available for subsequent dialogue. `mock_translate` is disabled in this mode.

## Reproduce

`scripts/check.ps1`: core/Rust IPC and C# typed HTTP fixtures.
`scripts/probe-translation.ps1 -Worker -Offline`: actual Whisper CPU file input→
local Qwen→history; uses existing consented models and an installed llama-server.
[Evidence and limits](evidence/T03-01c-windows-worker-translation.md).
Desktop server settings, translated captions/export, live E2E and macOS are next.


Desktop settings and presentation: [T03-02a](DESKTOP_TRANSLATION.md).

## Optional timing metadata (2026-10-01)

`translation.started` reports full source/request identity, `preview`, and
`worker_at_s` at HTTP owner dispatch. `translation.completed` and applied
`translation.updated` also carry `worker_at_s`. Capture admission, native ASR,
and source events carry the same worker-local clock; source events add
`stable_chars`. These additive fields do not change protocol version 1.
Unknown events/fields remain compatible with the bounded desktop event reader.
Owner dispatch is not the socket-send or first-token timestamp. The desktop
caption timing option selects scalar metadata into its bounded background log;
text-bearing event payloads are never serialized into that log.
See [stage definitions and limits](CAPTION_READING.md).
