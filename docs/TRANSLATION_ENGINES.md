# Translation engine comparison

## Diagnostic configurations

- llama.cpp `llama-server`, existing Qwen3-4B-Instruct-2507 GGUF Q4_K_M.
- TabbyAPI/ExLlamaV3, same base model in EXL3 4.0bpw_H6; pinned source/model in
  `benchmarks/tabby-model.json`. User approved downloads on 2026-10-01.
- Quantizations, chat templates and sampler implementations can differ. This compares
  deployment configurations; it does not isolate engine performance.

TabbyAPI is AGPL-3.0, ExLlamaV3 MIT, model Apache-2.0. Tabby remains an external
server candidate; changing the app's distributed backend requires a separate
packaging/license decision. Model/runtime files, credentials and full reports
stay in ignored `models/`, `logs/`, `benchmarks/results/` directories.

## Reproduce the comparison

The consented setup is `scripts/setup-tabby.py --download`, using a working Python
3.12 installation. This flag downloads the model and CUDA dependencies; obtain
download consent before running it on another installation. Package versions are
recorded in `models/tabby/requirements-installed.txt`; file hashes are recorded in
`models/tabby/model-downloads.json`. Setup creates an isolated venv; it does not
change global Python packages.

```powershell
./scripts/probe-translation-engines.ps1 -Warmup 1 -Rounds 3
./scripts/probe-translation-engines.ps1 -Warmup 1 -Rounds 3 -Order tabby-first
./scripts/probe-translation-engines.ps1 -Warmup 1 -Rounds 3 -Worker
```

Close other translation servers first. The script refuses an occupied diagnostic
port, starts one owned server at a time and stops it before starting the other.
Use `-ServerPath` to specify an installed llama-server executable. No downloads
occur during the comparison. Both engines use 4096-token cache/context settings,
single request concurrency, no draft model and the production Rust HTTP owner.
The default `-SamplingProfile current` matches installed llama-server/app fallbacks:
temperature 0.2, top-k 40, top-p 0.9, min-p 0.1, repetition penalty 1 and 256 output
tokens. `-SamplingProfile untruncated` disables top-k/top-p/min-p filtering; its
result must not be presented as current app performance. Seeds remain random.
`-Worker` additionally checks CPU Whisper file ASR→translation→history on each
server; it performs no capture or playback.

Forty fixed authored cases include English/Japanese, no context versus the latest
two source strings. One complete corpus round warms each engine; subsequent
rounds are measured. Model ID is normalized when hashing actual JSON requests;
the comparison rejects different corpus/order/request fingerprints. HTTP deadline,
response completeness, text bounds and retry policy match the worker adapter.

Reports contain seconds, mean/P95/max, partial/error evidence and paired outputs
in `semantic-review.csv`. A one-second GPU sampler records **whole-device** memory
and utilization; it is not a process VRAM peak. Semantic review must inspect
numbers, negation, conditions, extra explanations and missing meaning. The tool
does not automatically pass quality or select an engine.

## Windows result (2026-10-01)

With current sampling, two opposite-order runs measured 120 requests per engine
each: llama.cpp mean **0.119–0.131 seconds**, Tabby mean **0.158–0.160 seconds**.
The existing llama launcher remains the default. With untruncated sampling,
Tabby was faster; that result does not justify replacing the current backend.
Both engines mistranslated/omitted the return condition in authored examples.
See [measurements, quality findings and limits](evidence/translation-engines-windows-20261001.md).

## Try Tabby in the app

Double-click **`run-tabby.bat`**. It checks the existing model, starts Tabby on
`http://127.0.0.1:1234/v1/` and opens the live UI. Click **서버 연결 / 모델 조회**,
wait for Ready, then start a session. `run-tabby.bat -NoBuild` uses existing builds.
Closing the app stops the owned server. `run-live.bat` continues to use llama.cpp.
Both launchers require port 1234 to be free; do not run them together.

Keys are randomized, temporarily stored in the ignored run directory and removed
on cleanup. The wrapper redacts Tabby's API/admin keys from its logger. Abrupt
launcher termination can leave a server/key file; no unrelated process is killed.

## Acceptance boundary

Authored-text HTTP measurements exclude VAD, ASR and UI rendering. Actual game
coexistence, frame-time impact, full audio→caption latency and rendered UI require
separate measurement. Whisper remains CPU in both live launchers. CUDA ASR is a
separate performance candidate, with shared-GPU contention to evaluate.

Official sources: [TabbyAPI](https://github.com/theroyallab/tabbyAPI),
[ExLlamaV3](https://github.com/turboderp-org/exllamav3),
[EXL3 model](https://huggingface.co/ArtusDev/Qwen_Qwen3-4B-Instruct-2507-EXL3/tree/4.0bpw_H6).
