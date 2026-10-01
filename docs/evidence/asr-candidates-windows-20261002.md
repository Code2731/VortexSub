# SenseVoice / Whisper 파일 비교 (2026-10-02)

## 실행 범위

사용자의 다운로드·설치·실제 비교 동의 후 SenseVoiceSmall INT8와
sherpa-onnx/core 1.13.8을 확보했다. 고정 네 파일의 크기와 SHA256은 모두 일치했다.
Windows x64, Ryzen 7 5800X, RTX 3080, NVIDIA driver 617.14, CUDA 12.6,
CPython 3.12.14, CPU threads 8이다. SenseVoice는 CPU, Whisper base는 CUDA이며
DTW off·greedy best_of 1·no_context true다. SenseVoice 언어 힌트와 ITN은 켜져 있다.

영어/한국어 Windows TTS 각 10개와 무음 1개를 조건별 3회 실행했다.
각 회차 full/prefix 순서는 교대하지만 후보 간 순서는 SenseVoice 전체→Whisper
전체→패딩 Whisper 전체다. prefix는 실제 PCM 0.8초부터 0.256초 간격으로 누적한다.
번역·VAD·capture·worker 큐·실제 재생 대기는 없다. 실제 시간으로 측정한 decode를
순차 처리 시각 계산에 더한 **파일 시뮬레이션**이며 실제 자막 지연이 아니다.

## 결과

언어별 speech 30회에서 중앙값이다. CER은 전체 입력의 문자 단위 micro 오류율이다.
공백/구두점 제외, NFKC·소문자를 동일하게 적용한다.

| 항목 | SenseVoice CPU | Whisper CUDA | Whisper CUDA 짧은 입력 패딩 |
|---|---:|---:|---:|
| 영어 전체 decode (초) | 0.067671 | 0.046737 | 0.063354 |
| 한국어 전체 decode (초) | 0.069456 | 0.052789 | 0.060736 |
| 영어 첫 비어 있지 않은 원문, 시뮬레이션 (초) | 0.829508 | 1.093061 | 0.854387 |
| 한국어 첫 비어 있지 않은 원문, 시뮬레이션 (초) | 0.830271 | 1.102379 | 0.849463 |
| 영어 2회 공유 접두사 4글자, 시뮬레이션 (초) | 1.091309 | 1.349828 | 1.107337 |
| 한국어 2회 공유 접두사 4글자, 시뮬레이션 (초) | 1.093052 | 1.351560 | 1.126349 |
| 영어 부분 결과 수정 횟수/음원 | 4.0 | 2.5 | 3.0 |
| 한국어 부분 결과 수정 횟수/음원 | 7.5 | 3.0 | 3.5 |
| 영어 전체 CER | 4.42% | 3.40% | 3.40% |
| 한국어 전체 CER | 6.50% | 13.01% | 13.01% |

원문 양끝 공백을 제거한 뒤 이전 결과의 글자가 삭제/교체되는 관측을 수정 1회로
센다. 문장부호 수정도 포함한다. 4글자 공유 지표는 제품의 안정 단어/절 판정이
아니며 오역을 반복한 경우도 통과할 수 있다. 전체 입력의 패딩 여부는 같으므로
full decode의 조건 간 시간 변동을 패딩 비용으로 해석하지 않는다.
후보 순서가 고정된 별도 실행이므로 작은 추론 시간 차이는 재검증이 필요하다.

## 발견과 판단

1. 이번 CPU SenseVoice의 추론 자체는 CUDA Whisper보다 빠르지 않았다.
   GPU 번역과의 경합 감소 가능성은 이번 비교에 포함하지 않았다.
2. 첫 원문 차이의 큰 부분은 최소 입력 길이다. Whisper의 0.8초 PCM은 내부에서
   790 ms로 계산되어 전사 없이 반환된다. 정확히 1초 패딩도 990 ms로 거부됐다.
   처음의 1초 미만 PCM만 **1.02초까지 zero-pad**하면 경고 없이 처리됐다.
   실제 audio_end/시뮬레이션 도착 시각은 원래 PCM 길이를 유지했다.
3. 패딩 Whisper의 첫 원문은 영어 약 0.239초, 한국어 약 0.253초 앞당겨졌다.
   전체 입력의 최종 원문은 기존 Whisper와 60/60 동일했다. 부분 결과의 품질은
   별개다. `We cannot win the game`처럼 짧은 입력에서 예측한 단어를 수정했고,
   한국어 첫 부분에도 오류가 남았다. 첫 출력 가속을 정확한 번역 가속으로 보지 않는다.
4. SenseVoice는 영어 `shield`를 `Shi`로, 한국어 `네 도움`을 `내 도움`으로 전사했다.
   한국어 띄어쓰기가 잘게 나뉘었다. Whisper에도 `회복 물약`을 잘못 인식하는 사례가
   있다. 숫자 `15` 대 `fifteen`, `3` 대 `세` 같은 표기 차이는 CER에 포함되므로
   위 숫자만으로 의미 정확도의 우열을 확정하지 않는다.
5. 무음 3회에서 SenseVoice는 `I.`, Whisper는 `[BLANK_AUDIO]`를 출력했다.
   집계는 일반 텍스트와 제어 표식만 있는 출력을 분리한다. 제품 VAD의 무음 제외는
   이 직접 ASR 파일 비교에 없으며 VAD 결합 수용으로 확대하지 않는다.

**기본 Whisper를 유지한다.** 다음 구현은 짧은 입력 패딩을 실험 경로에 연결하고
생산 스케줄러의 안정 구간 판정·Qwen 번역까지 paced 조건을 교대 비교한다.
실제 번역 가속과 수정/오역 증가 여부를 확인한 뒤 기본 적용을 결정한다.
SenseVoice는 한국어·CPU 선택 후보로 보존하며 자연 발화/일본어와 GPU 경합은 후속이다.

## 재현과 검증

```powershell
./scripts/probe-asr-candidates.ps1 -Rounds 3 -Threads 8 -CudaArchitecture 86
# 같은 빌드가 있으면 -NoBuild로 재사용
target/model-probe-cuda/release/echosub-model-probe.exe prefix benchmarks/fixtures/local-tts/manifest.json models/ggml-base.bin 60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe benchmarks/results/padded-whisper.json 8 cuda 3 pad-short
```

이번 원본: Git 제외 `benchmarks/results/asr-candidates-20261002-025902-10144/`의
`sensevoice.json`, `whisper.json`, `whisper-pad-short.json`, `summary-with-padding.json`.
`whisper-pad-1s-rejected.*`는 실패한 1초 패딩 조건을 보존한다.
`runtime.json`은 기본 비교 binary hash와 native profile을 기록한다.

`native` 자동 CUDA 빌드 2회는 CC 5.2 커널 오류로 실패했다. 권한 변경 뒤에도
같아 sandbox만의 문제라고 확정하지 않는다. 확인된 CC 8.6을 명시한 빌드/실제
CUDA decode는 성공했고, 초기 실패 로그는 별도 보존했다. 다른 GPU에는 86을
그대로 적용하지 않는다. 제품 worker를 이 probe로 덮어쓰지 않았다.

Python AST와 PowerShell parser, Rust fmt, native release build를 확인했다.
일반 Rust/C#/IPC 테스트는 이번 라운드에 실행하지 않았다. live 게임·화면·번역
지연·자연/일본어 음성·macOS 수용도 미검증이다.
