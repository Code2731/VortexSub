# Whisper CPU/CUDA 부분 전사 비교 (2026-10-01)

## 변경

`probe-partial-scheduling.ps1`에 backend, 반복 횟수, 첫 요청 시점,
현재 scheduler만 실행하는 옵션을 추가했다. 기존 기본값(CPU, 1회, 0.8초,
legacy/current 비교)은 유지한다. 명시적 ignored native 파일 측정만 실행한다.
이번 변경은 production scheduler나 VAD 간격을 바꾸지 않는다.

직접 비교용 `run-live-cuda.bat`과 두 PowerShell 런처의 `-AsrBackend cpu|cuda`
선택을 추가했다. CUDA는 별도 worker 경로와 `--asr-backend cuda`를 사용한다.
기본 전사는 CPU다. 모델/런타임 다운로드와 자동 backend 전환은 없다.

## 조건과 재현

동일 기존 영어 합성 3문장 연결 7.605초, Whisper base, 8 threads.
기존 하드웨어 기록은 Windows build 26200, AMD64 Family 25 Model 33,
논리 CPU 16개다. 이번 `nvidia-smi`는 RTX 3080 10 GiB, driver 617.14를
확인했다. sandbox CIM 조회는 권한 거부로 CPU/OS를 재조회하지 못했다.

```powershell
scripts/probe-partial-scheduling.ps1 -Backend cpu -CurrentOnly -Rounds 3 -FirstPartialSeconds 1.0 -WavPath benchmarks/results/streaming-translation-20261001-093831-71c7ed/en-joined-prefix-10.wav
scripts/probe-partial-scheduling.ps1 -Backend cuda -CurrentOnly -Rounds 3 -FirstPartialSeconds 1.0 -WavPath benchmarks/results/streaming-translation-20261001-093831-71c7ed/en-joined-prefix-10.wav
```

CUDA 빌드는 설치된 Toolkit 12.6/Ninja/MSVC/LLVM과 architecture 86을 사용했다.
컴파일 종료 뒤 CPU 전체→CUDA 전체 순서로 측정했다. 각 backend에서 1초와
0.25초 간격을 3회씩, 총 12회 실행했다. 각각 fresh native owner, warmup 없음.
모델 검증/로딩은 별도 `load_s`이며 replay 시간에서 제외한다.
독립 파일의 0.8초 요청에 있던 최소 입력 경고를 피하려 첫 요청을 1.0초로
고정했다. 제품의 VAD pre-roll·0.8초 첫 요청과 같은 조건은 아니다.

PCM은 실제 속도로 공급하고 알려진 파일 끝에서 즉시 final을 요청했다.
VAD 무음 대기·캡처·번역·IPC client·화면·게임은 없다. 기존 앱의 GPU 사용은
격리하지 않았으며 측정 전 device 사용률 40%도 관측했다.
CPU/CUDA native 빌드, 모델/음원 hash 일치와 CUDA 6개 owner의
`using CUDA0 backend` 로그를 확인했다. GPU 샘플은 장치 전체 값이며
ASR 프로세스의 VRAM peak로 해석하지 않는다.

## 결과

각 3회의 중앙값, 단위 초. 첫 텍스트는 적용된 첫 nonempty partial이며
안정 prefix나 번역 화면의 첫 자막은 아니다.

| backend / 간격 | 첫 텍스트 | 확정 완료 | 음원 끝→확정 | 누적 native decode |
|---|---:|---:|---:|---:|
| CPU / 1초 | 2.781 | 8.554 | 0.949 | 5.410 |
| CUDA / 1초 | 2.182 | 7.810 | 0.205 | 1.115 |
| CPU / 0.25초 | 2.006 | 8.835 | 1.230 | 7.570 |
| CUDA / 0.25초 | 1.363 | 7.754 | 0.149 | 3.002 |

1초 간격의 범위: CPU 첫 텍스트 2.753~2.784 / 확정 8.520~8.630,
CUDA 첫 텍스트 2.082~2.196 / 확정 7.770~7.829.
누적 decode는 취소/NoSpeech도 포함하며 요청 횟수가 달라 순수 동일 요청
처리속도 배율로 해석하지 않는다. 1초 간격에서 첫 텍스트 약 0.599초,
파일 끝 뒤 확정 약 0.744초 감소를 관측했다.

1초 CPU는 적용 완료 7/무시 1, CUDA는 8/0으로 모든 반복이 같았다.
0.25초 CPU는 적용 10/무시 1, 보류 25·대체 16; CUDA는 적용 27/무시 1,
보류 0·대체 0이다. applied에는 NoSpeech도 포함한다.
0.25초 CUDA는 nonempty partial 업데이트 25회로 1초의 6회보다 많으며,
누적 decode도 1.115→3.002초로 늘었다. 자동 간격 단축은 채택하지 않는다.

## 품질과 채택 범위

12회 확정 원문은 모두 같고 이 합성 fixture의 3문장과 일치했다.
부분 전사에는 양쪽 모두 `three n's`, `door and`가 나타났다.
빠른 CUDA 0.25초에서도 `until I reach` 뒤 `until I return`으로 수정됐다.
첫 문장은 1.3초대에 이미 전체로 출력되므로 실제 말의 끝/정확한 시간
정렬을 보장하는 지표로 보지 않는다. 제품 품질 gate는 계속 미통과다.

CUDA는 선택 실행으로 제공한다. Whisper와 Qwen의 동시 GPU 경쟁 및
게임 영향은 미측정이므로 기본 CPU와 제품 요청 간격을 유지한다.
다음은 캡처 없이 실제 paced ASR→Qwen 번역 경로를 연결해 단계별 지연,
수정/조건 오역과 GPU 동시 부하를 비교하는 작업이다.

## 검증·보관

native CPU/CUDA/VAD release 빌드, 각 6회 파일 측정 정상 종료,
Rust fmt·PowerShell 런처/probe 구문·diff 검사 PASS.
이번 추가는 IPC/제품 Rust 계약을 바꾸지 않는다. 전체 회귀 suite,
CUDA 배치의 실제 UI/장치 실행, 자연/일본어 음성·게임·macOS는 미실행이다.

원본: Git 제외 `benchmarks/results/partial-backends-20261001-195607/`
(`cpu.json`, `cuda.json`, `summary.json`, native logs, device GPU samples).
음원 SHA-256: `ec4391bf31bd0e983cbdd265344fa13ed6f1371c16c2a572b08dc54981101615`.
model/worker SHA-256도 결과에 보관했다. 새 자산은 다운로드하지 않았다.
