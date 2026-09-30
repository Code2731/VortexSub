# Desktop latency investigation (2026-10-01)

User reported approximately two seconds from hearing speech to seeing translated
captions. This is an observation of the full pipeline, not an HTTP timing sample.

## Existing run evidence

Read `logs/translation-20261001-080221-34040.stderr.log` without rerunning capture
or inference. Forty llama-server total inference timing entries:

| Metric | Seconds |
| --- | ---: |
| Mean | 0.42343525 |
| P95, nearest rank | 0.77968 |
| Maximum | 0.97312 |

The server log reports RTX 3080 CUDA0 and 37/37 model layers offloaded.
These are server inference times; they exclude VAD finalization, ASR, admission
queues, HTTP overhead and UI rendering. They cannot establish the reported
two-second end-to-end breakdown for individual utterances.

Current launcher: ONNX Runtime/Silero VAD CPU, whisper.cpp/Whisper base CPU,
llama.cpp llama-server/Qwen3-4B-Instruct-2507 Q4_K_M GPU. Native CUDA ASR diagnostics
exist separately; live UI launcher currently builds the CPU worker.

## Change and validation

Previously the desktop waited for its next 0.5-second poll to reflect results.
New source/history/translation events signal a capacity-one asynchronous wakeup;
the UI coalesces for 0.03 seconds then refreshes validated snapshots. Reader
continuations cannot run UI work synchronously. Existing operation gate,
cancellation, TTL and 0.5-second recovery heartbeat remain. Disconnect cancels and
joins the event refresh task before disposing the client.

`dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore` and
`dotnet build tests/EchoSub.ProtocolSmoke/EchoSub.ProtocolSmoke.csproj --no-restore`:
PASS, zero warnings/errors. No new tests or inference/capture runs in this change.

Actual display improvement is unmeasured. VAD ending silence remains 0.480 seconds;
translation dispatch still requires a final source, and responses remain complete
non-streaming text. No segmentation/model/context budget change was made.
Next performance work should measure utterance-end→ASR→HTTP→render stages and
evaluate native CUDA ASR in the launcher before changing quality-sensitive policy.
