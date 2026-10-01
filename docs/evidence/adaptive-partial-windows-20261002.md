# 적응형 전사 스케줄 확인 (2026-10-02)

## 변경 전 사용자 실행

Git 제외 `logs/caption-timing-20261002-004639-12cce77819814da68dd2800bd5958b09.jsonl`에서
첫 요청 음성 길이 중앙값 0.512초, 첫 요청→첫 원문 0.576626초,
첫 요청→첫 번역 1.379688초를 관측했다. 번역이 나온 56개 중 44개는 첫 ASR이
적용돼도 같은 revision의 원문 이벤트가 없었다. 이전 0.8초 실행은 39개 중
1개였다. 통제된 동일 입력 비교는 아니며 원인 분류가 없었던 당시 로그로
NoSpeech/Failed 등을 확정하지 않는다. 첫 요청을 0.8초로 복구했다.

## 정책과 계약

VAD 후보/실제 ASR 접수를 분리하고, bounded policy는 마지막 원문/안정 prefix,
접수 PCM 끝과 실제 decode 비용으로 다음 새 오디오 조건을 정한다.
최신 후보 하나·단일 native 예약·임시 HTTP 반환 보호·확정 우선은 유지한다.
빈 결과/불안정 재확인/안정 증가/반복의 조건은 [정책 문서](../ADAPTIVE_PARTIALS.md)에 있다.
캡처 callback에 작업을 추가하지 않았으며 audio trim/ASR 엔진 교체는 이번 범위가 아니다.
실제 ASR outcome과 adaptive policy/growth를 텍스트 없는 로그에 추가했다.

## 실제 native/HTTP 파일 비교

Windows, RTX 3080, Whisper base CUDA 8 threads, 기존 Qwen3 4B Q4_K_M/
llama.cpp GPU HTTP. 동일 7.605초 합성 영어(en-01~03 연결)로 조건별 3회다.
첫 요청은 양쪽 0.8초, 고정 조건의 요청 간격은 0.512초, 적응형 후보는
0.256초다. 순서는 round별 교대했고 owner 준비/모델 load/HTTP warmup은
측정에서 제외했다. ASR warmup은 없고 전체 GPU 경쟁을 격리하지 않았다.

```powershell
scripts/probe-paced-translation.ps1 -Backend cuda -Rounds 3 -Adaptive
```

| 중앙값 | 고정 | 적응형 |
|---|---:|---:|
| 첫 원문(초) | 1.387946 | 1.368604 |
| 첫 안정 원문(초) | 1.885746 | 1.625226 |
| 첫 번역(초) | 2.016450 | 1.737594 |
| 확정 번역(초) | 8.150245 | 8.004911 |
| 누적 native decode(초) | 1.3981 | 1.0492 |
| 적용 ASR 횟수 | 15 | 13 |
| HTTP 완료 횟수 | 4 | 4 |

첫 번역 차이는 0.278856초다. 첫 원문은 거의 같고 안정 prefix 재확인 시점이
앞당겨졌다. 적용 ASR 횟수는 고정 15/14/15, 적응형 13/13/14다.
양쪽 전체 확정 ASR 원문은 6회 동일했다. 번역에는 gate→문 앞/문 근처,
조건절 표현/문장 순서 차이가 남았다. 첫 출력 이후의 오역·수정 빈도 수용과
자연 음성 품질 검증은 아니며 quality_gate_passed=false를 유지한다.
VAD/capture/IPC client/UI/game는 이 파일 비교에 포함하지 않았다.

원본: `benchmarks/results/paced-translation-20261002-011456-a03c0c/`
(report/runtime/native-http/server/GPU log). WAV SHA-256:
`ec4391bf31bd0e983cbdd265344fa13ed6f1371c16c2a572b08dc54981101615`.

### Harness 보정 기록

첫 sandbox 실행은 WinGet 서버 링크 접근 WinError 5로 중단됐다. 기존 승인된
로컬 모델/서버의 실행 권한으로 재실행했고, 소유 서버 종료/key 제거를 확인했다.
초기 결과 `010848-fe11ec`는 요청 초 간격이 실제 프레임과 달랐다.
후속 `011240-61996c`는 간격만 보정했고 PCM 끝이 임의 샘플이었다.
두 버전에서 초기 끝과 후속 끝의 작은 jitter로 growth 조건이 프레임보다
몇 sample 부족해 후보 하나를 더 기다렸다. 따라서 생산 VAD 비교 근거로 쓰지 않는다.
최종 harness는 요청 간격과 중간 PCM 전달을 모두 512-sample 단위로 맞추고,
알려진 파일 EOF에서만 실제 마지막 샘플까지 전달한다. 앞 두 결과도 Git 제외로 보관한다.

## 회귀 확인

- `scripts/check.ps1`: Rust 157개, C# 표시 39개 assertion·HTTP/IPC·빌드 PASS.
- `tests/test_caption_timing_summary.py`: Python 5개 PASS.
- native CUDA/VAD release 빌드와 위 실제 파일 비교 6회 완료.
- `cargo fmt --all -- --check`, Python/PowerShell 구문, `git diff --check` PASS.

피드백의 빠른 확인/빈 결과/반복/비용 상한과 identity 초기화,
새 오디오 부족 시 pending 유지 및 확정 우회, 기존 HTTP 예약/취소 계약을 확인했다.
C# logger와 Python 요약은 고정 policy/outcome 값만 기록하고 임의 문자열을 제외한다.
원본 회귀 출력: `benchmarks/results/adaptive-partial-check-20261002.log`.

실제 사용자 변경 후 로그·게임·자연/일본어 음성·macOS는 미실행이다.
다음은 실제 로그 확인과 합의한 두 번째 단계인 decode window/product range 분리,
검증된 시간 정렬과 overlap 재결합이다. 모델 교체 비교는 그 이후에 진행한다.
