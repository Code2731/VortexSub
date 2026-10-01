# Windows 부분 전사·임시 번역 실험 (2026-10-01)

## 범위와 실행

Windows build 26200, AMD64 Family 25 Model 33/16 logical CPUs,
RTX 3080 10,240 MiB/driver 617.14. 기존 manifest의 Whisper base CPU와
Qwen3-4B-Instruct-2507 Q4_K_M/CUDA llama build 6047을 사용했다.
sampler는 현재 앱 조건(temp 0.2, 출력 256, top-k 40/top-p 0.9/min-p 0.1).
원본 모델·음원 SHA는 기존 manifest, worker/server/trace SHA는 ignored runtime.json이다.

`scripts/probe-streaming-translation.ps1 -NoBuild` 최종 실행 PASS.
결과: `benchmarks/results/streaming-translation-20261001-093831-71c7ed/`.
CPU native로 0.75초 간격의 독립 WAV prefix를 전사했다. availability를
음원 끝 + 파일 전사 시간으로 추정한 후 production pipeline의 MOCK ASR
admission과 실제 HTTP에 재현했다. 3개 영어 case × 2조건 + 작성한 일본어
2개 case × 2조건 = 10회. case별 조건 순서를 바꿨고 HTTP warmup은 제외했다.
한 번씩의 작은 표본이며 live 지연이나 자연 발화 품질 판정이 아니다.

## 첫 번역과 최종 번역 (초)

| case | 확정 전용 첫/최종 | 임시 켬 첫 | 임시 켬 최종 | HTTP 완료 수 전용→임시 |
|---|---:|---:|---:|---:|
| en-01 (2.270초 합성 음원) | 3.182 | 3.216 | 3.216 | 1→2 |
| en-03 (2.875초 합성 음원) | 3.887 | 3.327 | 3.748 | 1→2 |
| en-joined (7.605초 합성 3문장 연결) | 9.006 | 3.253 | 8.945 | 1→9 |
| authored-ja-return (작성한 전사) | 3.320 | 1.593 | 3.221 | 1→3 |
| authored-ja-correction (작성한 전사) | 3.272 | 1.625 | 3.191 | 1→2 |

짧은 en-01은 임시 요청이 확정에 취소돼 표시 이득이 없었다.
en-joined의 첫 자막은 5.753초 빨랐으나 최종 번역 단축은 0.061초였다.
HTTP 완료 수에는 취소 반환도 포함한다. 실제 화면 표시/읽기 가능 시간은 측정하지 않았다.

## 의미 검토와 판정

- en-03의 귀환 조건은 임시/최종 모두 부정확하게 번역됐다.
- en-joined에는 불완전한 `three`까지 안정 prefix에 들어가 적 위치 설명이
  나중에 추가됐다. 임시 조건이 뒤의 귀환 조건을 미리 알 수는 없다.
- 일본어 return의 확정 전용 출력은 귀환 조건을 누락했다. 임시 조건의
  최종 출력은 조건을 포함했지만 sampling 차이이므로 개선이라고 일반화하지 않는다.
- 일본어 correction은 임시의 건너기 지시가 최종의 건너지 말라는 지시로
  뒤집혔다. 원문이 연속 두 번 같아도 의미 확정이 보장되지 않는다.

**기능 진단 PASS, 품질 gate FAIL/기본 활성화 보류.** 실제 UI 클릭·렌더링,
live ASR scheduler/VAD·전체 E2E·게임 동시 실행·일본어 실제 음성·Mac 미검증.

## 발견한 HTTP 문제와 검증

초기 두 실행은 en-joined의 첫 HTTP 번역에서 `Transport`로 실패했다.
failure checkpoint에는 확정 원문과 Failed 기록을 보존했다. owner의
current-thread runtime이 작업 사이 정지하는 동안 idle 연결이 남는 경로를
피하도록 HTTP pool의 idle 재사용을 껐다. 이후 동일 긴 case 포함 10회 PASS.
idle socket 재사용이 원인이라는 가설을 지지하지만 저수준 패킷 원인은 확정하지 않았다.
새 연결 사용 회귀 fixture를 추가했다. 이는 오디오 캡처 진단을 수행한 결과가 아니다.

전체 `scripts/check.ps1`: Rust 146개, C# typed HTTP/IPC 및 표시 21개 assertion PASS.
native CPU/VAD worker build PASS. C# NativeAsrSmoke build 경고/오류 0.
모델 다운로드·오디오 캡처·재생·UI 실행은 이번 실험에 없었다.
