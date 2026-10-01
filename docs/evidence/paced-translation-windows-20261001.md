# 실제 부분 ASR→Qwen 번역 지연 (2026-10-01)

## 구현과 조건

명시적 파일 probe를 실제 native owner·HTTP owner·production scheduler에
연결했다. 이전 독립 ASR prefix/MOCK replay와 다르게 Whisper와 Qwen이
같은 GPU에서 실행된다. 전사·안정 prefix·HTTP 요청 원문/문맥·완료 이벤트를
5ms poll 시점의 초 단위로 저장한다. IPC client나 화면 렌더링 시간은 아니다.
진단용 event drain은 test/native feature에만 포함한다.

Windows, RTX 3080 10 GiB, 설치된 CUDA 12.6, Whisper base CUDA 8 threads,
Qwen3-4B-Instruct-2507 Q4_K_M/llama.cpp GPU offload 99를 사용했다.
기존 en-01/02/03 TTS를 이어 붙인 7.605초 파일이며 원본/모델 hash를 확인했다.
새 모델/런타임 다운로드는 없다. 자연 발화나 일본어 ASR 자료는 아니다.

1초·0.5초 요청 간격을 각각 3회, 순서 1→0.5 / 0.5→1 / 1→0.5로 실행했다.
전체 순서 균형 실험은 아니며 background GPU 사용을 격리하지 않았다.
각 ASR owner는 새로 만들고 warmup하지 않았다. 번역 서버만 먼저 워밍업했다.
모델 준비 시간은 제외한다. 첫 요청은 파일 진단에서 1.0초로 고정했다.
VAD 대신 알려진 파일 끝에서 확정을 요청하며 캡처·게임·화면은 실행하지 않았다.
LocalAgreement-2, 번역 0.5초 간격·유한 요청·확정 우선·identity 검증은 유지했다.

```powershell
scripts/probe-paced-translation.ps1 -Backend cuda -Rounds 3
```

기존 Python/llama-server가 필요하며 wrapper가 HTTP warmup probe를 offline 빌드한다.
wrapper는 private port 18087을 점유한 서버를 채택하지 않으며 자신의 서버만
종료하고 임시 API key를 삭제한다. key 값은 보고서에 기록하지 않는다.
이번 native rebuild는 architecture 86으로 실행했다. CUDA native 사용과
Qwen GPU offload는 로그에서 확인한다.

## 결과

각 조건 3회의 중앙값, replay 시작 기준 초다. 단계 간 차이는 반복별 차이의
중앙값이므로 열 중앙값끼리 뺀 값과 약간 다를 수 있다.

| 항목 | 1초 | 0.5초 |
|---|---:|---:|
| 첫 원문 텍스트 | 2.088 | 1.583 |
| 첫 안정 prefix | 3.107 | 2.087 |
| 첫 적용 번역 | 3.361 | 2.330 |
| 원문→안정 prefix 대기 | 1.023 | 0.503 |
| 안정 prefix→적용 번역 | 0.243 | 0.235 |
| 확정 ASR 완료 | 7.769 | 7.764 |
| 확정 번역 완료 | 8.109 | 8.062 |
| 누적 native decode | 0.741 | 1.283 |
| HTTP 완료 횟수 | 4 | 5 |

첫 번역 범위는 1초 3.185~3.389, 0.5초 2.322~2.352초다.
이 파일에서는 첫 번역을 약 1.032초 앞당겼다. 주요 변화는 번역 자체의
가속보다 첫 전사와 두 번째 일치 전사를 얻는 시점이다. 확정 번역 차이는
약 0.047초로 작으며 유의한 확정 가속을 주장하지 않는다.

0.5초는 native 완료 적용 14/무시 1, partial 보류 2회였다.
1초는 적용 8/무시 0, 보류 0회였다. applied에는 NoSpeech도 포함한다.
0.5초의 비용 증가는 더 많은 추론과 임시 번역을 포함한다. GPU sample은
device 전체 값으로 ASR/번역별 VRAM peak나 게임 영향 지표가 아니다.

## 의미 검토: 품질 gate false

확정 ASR 원문은 6회 모두 동일했다. 번역의 정확성은 별도다.

- 0.5초는 3.645~3.661초에 `There are three`를 “세 개가 있습니다.”로 먼저
  번역했다. 1초에는 이 요청이 없었다. 이후 적과 위치 정보로 수정됐다.
- `gate`는 “문턱”, 1초의 한 실행은 “문단”으로 오역했다.
- `Do not open the door`가 귀환 조건 없이 먼저 표시됐다. 뒤의 확정 번역은
  귀환 조건을 포함했으나 일부 결과의 “문을 열기 전에 제가 돌아오기까지는”
  표현은 부자연스럽고 문장 순서도 바뀌었다.
- 빨리 나온 안정 prefix도 두 가설의 일치일 뿐 의미 정확도나 실제 말 끝을
  보장하지 않는다. 이번 음원만으로 품질/제품 gate를 통과시키지 않는다.

## 선택 실행

`run-live-cuda-fast.bat` 또는 `run-live-cuda.bat -FastPartials`로 실행하고,
세션 시작 전에 **안정된 부분 먼저 번역 · 임시 결과**를 켠다.
flag는 live CUDA에서만 허용하며 0.5초 부분 요청 간격을 선택한다.
기본 실행/기본 간격과 첫 0.8초 발화 조건, 번역/읽기 정책은 유지한다.
실제 VAD는 512-sample 프레임 반올림으로 간격 0.992→0.512초를 사용한다.
이 파일 측정은 정확한 1.0/0.5초와 알려진 종료를 사용하므로 실제 live와
조건이 다르다. 빠른 모드의 화면/장치 실행은 이번에 확인하지 않았다.

## 검증과 다음 작업

Windows check PASS: Rust 153개, C# IPC/HTTP 및 표시 29개 assertion,
기본 빌드. native CUDA/VAD release 빌드와 실제 HTTP 파일 6회 완료.
초기 새 fixture의 정확한 1초 가정은 기존 frame 반올림(0.992초)과 달라 실패했다.
effective samples를 기준으로 수정해 첫 요청/최종 PCM 불변과 빈도 증가를 확인했다.
전체 시간 설정을 바꾼 것이 아니다. 스크립트 구문·fmt/diff도 확인했다.

원본은 Git 제외 `benchmarks/results/paced-translation-20261001-212638-cce49b/`
의 `report.json`, `summary.json`, `runtime.json`, native/server logs와 GPU samples다.
완료한 조건은 다음 조건 실행 전에 저장한다.

다음은 UI 수신→실제 카드 교체 지연 계측과 읽기 카드/current draft 교체 제한
검토다. 자연/일본어 음성·게임 경쟁·음성→화면 수용·macOS는 남아 있다.
