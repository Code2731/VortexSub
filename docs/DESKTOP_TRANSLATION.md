# Desktop local translation diagnostics (T03-02a)

## Run and configure

Double-click the repository-root **`run-live.bat`** to start the installed
llama-server with the existing manifest model, wait for readiness on port 1234,
and open the live UI using cached dependencies. Then click **서버 연결 / 모델 조회**
in the UI before starting the session. Closing the app stops the server owned by
this launcher. An occupied port is rejected without stopping another process.
Use `run-live.bat -NoBuild` for existing binaries or
`run-live.bat -ServerPath "C:\path\llama-server.exe"` for a custom installation.
Startup logs are under ignored `logs/`; models are never downloaded.
This convenience launcher creates a random server API key in an ignored temporary
file, passes it through the child process environment and removes the file on
normal/error cleanup. Abrupt termination of the launcher can leave that file and
server behind. The launcher has not been interactively exercised in this round.

Start an existing OpenAI-compatible local server separately, then run
`./run.cmd -Live -Offline`. The launcher enables the HTTP adapter but translation
starts disabled. No server/model download or automatic server start occurs.
For the already installed llama-server and consented Qwen file, an example is:

```powershell
llama-server.exe -m ./models/Qwen3-4B-Instruct-2507-Q4_K_M.gguf -c 4096 -ngl 99 --parallel 1 --host 127.0.0.1 --port 1234 --alias qwen3-4b-instruct-2507-q4_k_m
```

1. Before starting a session, enter `http://127.0.0.1:1234/v1/` and click
   **서버 연결 / 모델 조회**. Only numeric loopback HTTP addresses are accepted.
2. One catalog model is selected automatically. For multiple models, select an
   actual ID from the dropdown and click **선택 모델 적용**. Preparing/Ready/Failed
   and sanitized errors are shown. Catalog lookup prevents session start until done.
3. Start a session. Final English/Japanese sources translate to Korean; Korean
   sources bypass translation. Partial translation is off by default. Select
   **안정된 부분 먼저 번역 · 임시 결과 / 기본 끔** before session start to try it;
   this also enables partial ASR. See [provisional translation contracts](STREAMING_TRANSLATION.md).
4. End the session and wait for cleanup before changing configuration or selecting
   **번역 끄기**. Retained history is preserved.

If authentication is required, set `ECHOSUB_TRANSLATION_TOKEN` in the launching
process environment. There is no token input or credential persistence. OS secret
storage remains pending. Endpoint/model settings are in memory only. Credentials
in URLs are rejected; proxy and redirects are disabled by the worker adapter.

## Presentation contract

History shows source, translation state/reason and completed translation. The
overlay hides the source by default; enable **오버레이에 원문 표시** to show it
alongside Korean translation. This setting changes immediately and applies only
to the overlay; history retains the source. The setting lasts for the current app
run. Translation remains visible when source display is disabled; no source
fallback appears during translation wait/failure with this option off.
The overlay shows a Korean line for a current finalized revision, or an explicitly
marked provisional translation with a matching stable source prefix, with a nonzero
translation request ID. Provisional lines carry **[임시 번역]**. It consumes validated snapshot
records rather than merging asynchronous event text.

Current UUID/internal session/epoch and applied source revision filter the latest
eligible source. Pending, failed, skipped and bypassed translations keep the source
visible. Final/source-only revisions retain their five-second source lifetime.
Displayed provisional captions have five seconds from their first presentation;
they remain visible while the next revision waits if its source still matches the
previous stable prefix. Repeated snapshots do not extend that lifetime. A corrected
prefix, new segment, or Pause/Stop clears the provisional card.
The diagnostic overlay still displays one latest card; two-card layout, display
presets, clipping/font/DPI acceptance and localization resources remain pending.

Result events now wake a snapshot refresh after a 0.03-second coalescing delay,
with one bounded wakeup and the existing single-operation gate. The 0.5-second
heartbeat remains for state recovery and expiry. This reduces scheduled display
waiting, not speech segmentation or inference time; actual rendered improvement
has not been measured. [Latency investigation](evidence/desktop-latency-20261001.md).

## Validation boundary

Pure presentation fixtures and C# HTTP/IPC validate identity/state/lifetime logic.
They do not render Avalonia. Manually check catalog selection, reconnect/failure,
source plus translation, long text, expiration, Pause/Resume and game focus on a
normal desktop. Live E2E latency and semantic translation quality remain separate
acceptance gates. [Evidence](evidence/T03-02a-windows-desktop-translation.md).
