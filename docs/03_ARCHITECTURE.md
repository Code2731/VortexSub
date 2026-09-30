# 03. 기술 아키텍처

## 1. 구조

```text
C# Avalonia Desktop
  ├─ Main / Settings / History / Overlay
  ├─ OS window adapter, permissions help, secret-store adapter
  └─ WorkerClient
         │ private stdin/stdout NDJSON (commands, events, snapshots)
         ▼
Rust echosub-worker
  ├─ CaptureAdapter
  │    ├─ Windows WASAPI loopback
  │    ├─ macOS ScreenCaptureKit Swift/Objective-C bridge (C ABI)
  │    └─ FixtureCapture (tests; never presented as real capture)
  ├─ Resampler → 16 kHz mono PCM → Silero VAD
  ├─ Segmenter / ASR scheduler → whisper.cpp
  ├─ Subtitle store / revision / translation scheduler
  ├─ TranslationAdapter → OpenAI-compatible local HTTP
  │    ├─ External: oMLX / LM Studio / compatible endpoint
  │    └─ Managed (product beta): bundled llama-server
  └─ Metrics / bounded history / TXT & SRT export
```

이 그림은 구현 경계 정의다. 별도 네트워크 마이크로서비스를 여러 개 만드는 설계가 아니다. 앱 UI, 공통 worker, 제품 베타의 선택적 번역 프로세스만 둔다.

## 2. UI와 Rust는 sidecar로 분리

**결정:** UI가 worker를 자식 프로세스로 실행하고 익명 파이프로 연결한다. 저빈도 제어/자막에 NDJSON을 사용한다. 원본 PCM은 이 파이프를 통과하지 않는다.

이유: native ASR 오류와 UI를 분리하고, C#/Rust 간 모델 객체 수명 관리 없이 프로토콜 테스트를 할 수 있다. 지연은 추정으로 단정하지 않고 IPC 측정에서 확인한다.

- UI가 worker 한 개를 소유한다. 시스템 서비스 설치는 하지 않는다.
- 로그/진행률 출력으로 stdout 프로토콜을 오염시키지 않는다.
- UI는 stdout·stderr를 별도로 비동기 drain한다.
- 정상 종료: shutdown → 최대 5초 대기 → 소유한 프로세스 트리만 종료.
- Windows는 Job Object 등 소유 프로세스 종료 수단, macOS는 프로세스 그룹/부모 파이프 EOF 감지를 구현한다.
- 패키지의 절대 경로와 인수 배열로 실행한다. shell 문자열을 조립하지 않는다.
- 자동 재시작해 사용자의 허가 없이 다시 캡처하지 않는다. 오류 후 재시작 버튼을 제공한다.

## 3. 플랫폼별 캡처

### Windows

`windows` Rust 바인딩을 통해 WASAPI를 사용한다. `eRender` endpoint에서 `AUDCLNT_STREAMFLAGS_LOOPBACK`과 shared mode를 사용한다. 이벤트 기반 캡처와 실제 mix format 처리를 구현한다. loopback에는 exclusive mode를 쓰지 않는다. [R01]

장치 ID, 실제 sample rate/channel layout, silent/discontinuity 플래그, 타임스탬프를 worker로 전달한다. 장치 변경 알림은 캡처 수명과 별도로 관리한다. 앱별 캡처는 Microsoft의 process-loopback 별도 경로를 후속 어댑터로 검토한다. [R11]

### macOS

**MVP 경로는 ScreenCaptureKit 하나로 고정한다.** Swift/Objective-C 브리지가 SCStream 수명·권한 관련 호출·CMSampleBuffer 변환을 맡고 Rust에 C ABI로 오디오 프레임을 넘긴다. 시스템 오디오 캡처와 사용자 동의 흐름은 Apple 공식 API를 따른다. [R02]

- 메인 run loop/콜백 큐를 구성하고 audio sample format을 실제 버퍼에서 확인한다.
- 화면 영상을 저장하거나 Rust/UI로 보내지 않는다. audio output을 사용하는 구성을 먼저 검증한다.
- API가 요구하는 content filter/stream 설정은 최소한으로 구성한다. 화면 크기를 임의로 0으로 두는 등의 미검증 트릭은 사용하지 않는다.
- C ABI 버퍼는 콜백 동안만 유효하다. Rust가 사전 할당된 큐로 복사한 뒤 반환한다.
- stop 완료 후 콜백이 더 오지 않음을 보장한 다음 handle을 해제한다.
- `.app`/worker/브리지의 권한 귀속과 서명을 M0에서 실제 시험한다. GUI가 떠 있는 것만으로 권한 처리가 끝났다고 간주하지 않는다.

Core Audio Process Tap은 검증 실패 시 검토할 대안이지, MVP에서 함께 구현할 두 번째 경로가 아니다.

## 4. 오디오·모델 엔진

### VAD

Silero VAD ONNX 모델 + CPU ONNX Runtime을 기준으로 한다. 별도 Python 실행 환경을 제품에 요구하지 않는다. 16 kHz 경로와 모델별 상태 텐서/입력 프레임 규약을 고정된 모델 버전에서 검증한다. 공식 구현의 16 kHz 프레임은 512 samples이며 recurrent state와 context 처리를 함께 따라야 한다. [R05][R13]

### ASR

whisper.cpp C API를 Rust RAII 어댑터로 감싼다. upstream 헤더와 native 라이브러리의 커밋을 맞춘다. 모델 context를 매 프레임 만들거나 CLI를 매번 다시 실행하지 않는다. 한 ASR context에는 동시에 한 decode만 실행한다. [R04]

기준 후보: 다국어 `small`; 저부하 후보: 다국어 `base`. `.en`은 영어 전용이므로 일본어/한국어 기본 모델로 쓰지 않는다. 전사는 원어 유지이며 한국어 번역은 다음 단계에서 수행한다. [R06]

- Windows: CUDA 가속을 우선 검증, GPU 사용 불가 시 명확히 표시한 CPU 경로.
- macOS: Metal을 우선 검증. Core ML encoder/NPU 최적화는 후속 측정 과제.
- CPU 실행 가능과 목표 지연 충족은 별도 판정.
- AMD/Intel NPU, xLLM, faster-whisper는 새 어댑터/실험 후보로 남긴다. 지원을 검증하지 않은 엔진을 기본 의존성으로 넣지 않는다.

## 5. 번역 엔진과 모델은 분리

**개발 MVP:** 사용자 지정 base URL과 실제 `/v1/models` 결과의 model ID로 연결한다. 기본 인터페이스는 `/v1/chat/completions`의 비스트리밍 텍스트 응답이다. oMLX와 LM Studio가 제공하는 교집합만 우선 사용한다. [R08][R09]

**제품 베타:** 앱이 관리하는 llama-server를 번역 어댑터 뒤에 추가한다. 버전/모델 경로/해시 검증, loopback bind, 랜덤 인증 토큰, 준비 검사, 종료 처리를 앱이 맡는다. 일반 API 호환성과 모든 공급자 옵션 지원은 같은 의미가 아니므로 실제 계약 테스트가 필요하다. [R10]

**모델 기준 후보:** `Qwen/Qwen3-4B-Instruct-2507`의 검증된 저비트 변환본. 공식 모델은 4B, non-thinking, Apache-2.0로 안내된다. 이는 “현재 최고 번역 모델”이라는 주장이 아니라 작고 검증 가능한 기준선이다. [R07]

관리형 번역의 초기 context 설정은 4,096 tokens를 후보로 한다. 이는 모델의 전체 지원 context가 아니라 이 앱의 자원 예산이다. 외부 서버에서는 context 길이를 강제로 바꾸지 않고 현재 설정·요청 오류를 확인한다.

GGUF 변환본은 llama.cpp/LM Studio, MLX 변환본은 oMLX용이다. 같은 파일을 양쪽 엔진이 그대로 읽는다고 가정하지 않는다. 변환 저장소 이름·파일명·해시는 확인 후 기록한다. 양자화 품질과 한국어 번역 품질은 별도로 평가한다.

NLLB-200 distilled 600M은 공식 모델 카드가 CC-BY-NC-4.0으로 표시하므로, 판매 가능성을 열어 둔 제품의 기본 배포 모델로 채택하지 않는다. 별도 사용 권한이 확인된 경우에만 재검토한다. [R14]

## 6. 모델 선택·메모리 정책

| 프로필 | ASR 기준 | 번역 정책 | 적용 방식 |
|---|---|---|---|
| Economy | base, CPU/GPU 중 실측 선택 | 확정만, 동시 1개 | 부분 전사 기본 off, 게임 동시 실행 우선 |
| Balanced | small, CUDA/Metal | 4B 저비트 후보, 짧은 문맥 | 부분 전사 1초 간격, 기본 후보 |
| Quality | medium 등 측정 후보 | 더 큰 모델은 사용자 선택 | P1, 자동 다운로드/자동 전환하지 않음 |

Balanced 기준 설계 예산: M3 Pro에서는 UI+worker+관리형 번역 프로세스 합산 peak 10 GiB 이내, Windows에서는 ASR+관리형 번역의 peak dedicated VRAM 5 GiB 이내를 목표로 검증한다. 실제 측정값은 아직 없다. GPU를 사용하는 게임의 메모리는 별도이며, 게임과 함께 돌릴 때 프레임 영향도 측정한다.

외부 서버의 GPU 메모리·동시 처리 설정은 이 앱이 임의 변경하지 않는다. 외부 backend에서는 관리할 수 없는 자원을 “제한 완료”라고 표시하지 말고 사용자에게 설정값을 안내한다.

## 7. 저장소 구조 — 구현 목표

```text
apps/EchoSub.Desktop/          # Avalonia views/viewmodels/platform window adapter
apps/EchoSub.Desktop.Tests/
crates/core/                  # pure data types, state machines, timelines
crates/audio/                 # normalization/resampling/VAD scheduling
crates/capture-windows/
crates/capture-macos/
crates/asr-whisper/
crates/translation/            # provider-independent contracts + HTTP adapter
crates/worker/                 # process entry, orchestration, IPC
crates/test-support/           # deterministic mocks / fixture capture
native/macos-capture/          # ScreenCaptureKit bridge
schemas/                      # IPC/config schemas generated/validated in M0/M1
scripts/                      # check, build-native, package, smoke
vendor-lock.json              # native sources + exact commits; created in M0
examples/
benchmarks/
docs/
```

불필요한 crate 분할은 줄일 수 있지만 pure core가 Avalonia/Windows/Swift에 의존하는 역방향 결합은 금지한다. CPU 테스트 타깃은 GPU SDK 없이 빌드되도록 feature를 나눈다.

## 8. 버전 고정과 배포

Rust stable의 실제 버전을 `rust-toolchain.toml`, .NET 10 SDK의 실제 버전을 `global.json`에 고정하는 기준안이다. Avalonia/ONNX Runtime/Rust crate/native 라이브러리 버전은 M0에서 호환성을 확인한 후 lock한다. 이 문서는 특정 최신 버전 번호를 추정해서 넣지 않는다.

Windows 패키지는 .NET self-contained UI, worker, native 의존성을 포함한다. macOS는 `.app`에 UI·worker·브리지를 함께 배치하고 배포 서명/공증을 수행한다. 모델은 사용자 동의 후 별도 설치한다. 패키지의 외부 서버 모드와 관리형 모드를 각각 시험한다.

