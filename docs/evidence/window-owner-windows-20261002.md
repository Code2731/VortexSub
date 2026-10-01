# 실제 부분 전사 owner의 window/fallback (2026-10-02)

## 구현

적용된 부분 결과의 전체 native metadata만 processing thread의 유한 mapping에
보관한다. 같은 session/epoch/segment, 인접 source revision, 동일 range start와
증가하는 end를 검사하고 core의 stable prefix를 이전/현재 관측 양쪽에 정렬한다.
다음 revision만 Plan을 사용한다. ignored completion은 mapping을 갱신하지 않는다.
최종/중단/폐기/epoch 전환에서 비우며 축소 시도 후에는 전체 관측 두 개로 갱신한다.

전체 PCM snapshot을 예약 반환까지 유지한다. owner가 slice를 선택하고 exact
DTW overlap 재결합 실패/추론 오류 시 동일 job의 전체 입력으로 재전사한다.
각 full 호출에는 새 attempt를 사용하되 요청 취소, running, abort/check 상태는
공유한다. 취소된 작업은 재시도하지 않으며 final은 항상 전체 입력이다.
`--experimental-decode-window`는 fast live CUDA ASR에만 허용한다.
기본은 off다. `run-live-cuda-fast.bat -DecodeWindow`로 명시 선택할 수 있다.

IPC의 상태/완료 진단에 on/off·attempt/fallback boolean/counter만 추가했다.
C# timing logger와 Python 요약도 이를 보존하며 텍스트를 추가하지 않는다.
카운터는 native completion 기준이고 ignored 작업도 포함한다.

## 실제 비교

Windows·RTX 3080·Whisper base CUDA DTW·Qwen3 4B Q4_K_M llama.cpp GPU HTTP.
7.605초 합성 영어, 조건별 3회, 순서 교대. 양쪽 적응형 스케줄/첫 요청 0.8초,
후보 간격 0.256초다. baseline은 DTW/window off, 실험은 둘 다 on이다.
모델 load/HTTP warmup은 제외, ASR warmup은 없고 GPU 경쟁을 격리하지 않았다.

| 중앙값(초) | 기준 | window 실험 |
|---|---:|---:|
| 첫 원문 | 1.388479 | 1.410707 |
| 첫 안정 원문 | 1.651728 | 1.661439 |
| 첫 번역 | 1.829519 | 1.840509 |
| 확정 번역 | 8.032895 | 8.063378 |
| 누적 native decode | 1.296135 | 1.476965 |

실험 각 실행은 축소 시도 2회, 전체 fallback 1회였다. 최종 ASR 원문은
6회 모두 fixture의 전체 문장과 같았다. 기본 처리 대비 속도 개선은 없었고
DTW 및 fallback 비용이 포함된 decode 합계가 늘었다. 번역의 표현/오역/수정 빈도와
실제 게임 수용은 검증하지 않았으며 quality_gate_passed=false다.

재현: `scripts/probe-paced-translation.ps1 -Backend cuda -Rounds 3 -DecodeWindow`.
Git 제외 원본: `benchmarks/results/paced-translation-20261002-020345-ba1c49/`
(report/runtime/native-http/server/GPU 로그), `window-owner-paced-final-20261002.log`.
앞선 캐시 정책 버전은 `020101-0d71da`로 보관하며 최종 비교 근거로 쓰지 않는다.
WAV SHA-256: `ec4391bf31bd0e983cbdd265344fa13ed6f1371c16c2a572b08dc54981101615`.

## fallback과 회귀 확인

실제 native owner에 일부러 matching anchor 뒤의 cut을 전달했다. merge 실패 후
전체 PCM 재전사로 fixture의 완전한 원문이 반환됐고 두 flags가 true임을 확인했다.
재현: `scripts/probe-partial-scheduling.ps1 -Trim -WindowFallback -Backend cuda -WavPath <WAV>`.
원본은 `window-forced-fallback-20261002.json/.log`다. 이는 의도적 파일 검사이며
실제 WASAPI 또는 백신 시작 문제를 검사하지 않았다.

- `scripts/check.ps1`: Rust 163개, C# 표시 39 assertion·HTTP/IPC·두 빌드 PASS.
- Python timing summary 6개 PASS, Rust fmt/diff와 launcher 구문 PASS.
- native CUDA/VAD release 빌드, 실제 paced 6회, 강제 fallback 1회 완료.
- fixture: revision/epoch/segment mismatch·캐시 초기화, fallback attempt의 공유
  취소/예약과 개별 claim 확인. 기존 stale/취소/확정 우선 계약도 통과했다.

회귀 원본은 `benchmarks/results/window-owner-check-20261002.log`다.
새 다운로드는 없었다. 캡처/VAD/IPC client의 실제 실행·화면·자연/일본어 음성·macOS는
미검증이다. 다음은 합의한 ASR 후보의 동일 입력 비교이며 현 경로는 실험 옵션으로
유지한다. 새로운 모델/런타임이 필요하면 기존 다운로드 동의 규칙을 따른다.
