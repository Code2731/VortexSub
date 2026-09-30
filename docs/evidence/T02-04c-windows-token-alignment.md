# T02-04c Windows token alignment

2026-10-01. Windows build 26200 / AMD64 16 logical processors / CPU Whisper base;
models and runtimes reuse the consented pinned manifests. No downloads.

## Implemented

Worker token timestamp extraction validates raw bytes against each native span.
Continuation reconciliation uses exact text, overlap time and safe word boundaries;
uncertain offsets, times and unspaced CJK cuts preserve source. Token metadata is
bounded to 4,096 entries and 4,096 text bytes. Suffix timing is validated once,
avoiding repeated range scans for every match candidate. PCM sizes and valid timed
token counts are reported without source/token bytes in diagnostic events.
[Contract](../ASR_TOKEN_ALIGNMENT.md).

## Executed evidence

- `scripts/check.ps1`: Rust 110 tests and C# IPC/builds PASS before adding the
  independent T03 translation contract tests. Final combined checks are in T03-01a.
- `scripts/build-model-probe.ps1 -Backend cpu -Package echosub-worker -Vad -Offline`:
  native CPU build PASS.
- `scripts/probe-worker-vad.ps1 -NoBuild -Offline`: corrected run
  `worker-vad-cpu-20261001-062407/report.json` PASS. Exact-zero 8-second WAV ×75
  gives **600 seconds of file PCM**; silent cases produced zero VAD calls,
  ASR jobs and history records. Corpus: 32 finals, one NoSpeech skip, zero failed;
  33 completions with 231 positive-duration timed tokens. Mean decode 0.665658 s,
  max 0.729049 s. Epoch reset, pair segmentation and hash rejection PASS.
- Initial VAD run stopped at a helper that incorrectly rejected NoSpeech skip;
  corrected helper checks terminal reason and preserves failed checkpoints.
- `scripts/probe-worker-asr.ps1 -Backend cpu -NoBuild -Offline`:
  `worker-asr-cpu-20261001-062646/report.json` PASS. Twenty finals, ten native-running
  cancellation/restarts, stale application absent, active shutdown PASS.
  Mean decode 0.676680 s, max 0.737072 s.

Native file runs preceded the final punctuation-point and suffix-scan refinements;
they do not exercise live continuation. Fixture tests cover those refinements.
Full reports and audio remain ignored under `benchmarks/results/`.

## Acceptance limits

Two `-Boundaries` attempts did not reach capture; no new live token alignment
result was obtained. Existing failure reports are retained. Actual duplicate
removal, natural speech accuracy, live silence, UI rendering, CUDA and macOS
remain unverified. File PCM validation does not establish live silence acceptance.
M2/M3 product gates remain PARTIAL.
