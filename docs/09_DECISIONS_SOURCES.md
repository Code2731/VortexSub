# 09. 설계 결정·공식 근거

기준일: 2026-09-30. 아래는 이번 문서를 위한 확인 자료다. 라이브러리/서비스는 변할 수 있으므로 실제 구현에서는 정확한 버전·커밋·모델 파일 해시를 고정한다. 설계에서 정한 기본값과 외부 문서가 보장하는 기능을 구분한다.

## A. 결정 기록

| ID | 결정 | 이유 | 상태 |
|---|---|---|---|
| D01 | 새 독립 프로젝트, 가칭 EchoSub | 기존 영상 편집기와 제품 수명·배포를 분리 | 제안 기준안 |
| D02 | Rust 코어 + Avalonia UI | 공통 엔진과 Windows/macOS UI를 분리 | 제안 기준안 |
| D03 | UI↔worker는 private stdio NDJSON | 모델/native 실패와 UI 격리, 검증 가능한 계약 | 제안 기준안 |
| D04 | Windows WASAPI, Mac ScreenCaptureKit | 운영체제 기본 시스템 오디오 API로 시작 | M0 실기기 gate |
| D05 | Silero ONNX CPU + whisper.cpp | 음성 구간 선별과 동일 ASR 엔진, Python-free 배포 | M0 모델 gate |
| D06 | final-only 번역 | partial 재번역 부하·화면 흔들림 감소 | MVP 기준 |
| D07 | 외부 로컬 번역 → 관리형 llama-server | 먼저 핵심 기능 검증, 이후 독립 제품 UX 확보 | M3/M4 단계 분리 |
| D08 | 4B non-thinking 저비트 모델을 번역 기준 후보 | 작은 모델·짧은 context로 먼저 측정 | 품질/성능 미검증 |
| D09 | 영어/일본어→한국어 우선 | 검증 범위를 좁혀 실제 품질을 확인 | MVP 범위 |
| D10 | Linux/화자 분리/TTS/OCR 후속 | 첫 제품 범위 억제 | 후속 |
| D11 | 모델·원본 음성·클라우드 자동 업로드 없음 | 사용자 데이터 흐름과 자원 사용을 명확히 함 | 기본 정책 |
| D12 | 하드웨어 성능은 목표와 실측 구분 | 다른 모델/장치 벤치마크를 제품 실적으로 오인하지 않음 | 상시 적용 |

## B. 구현 전 확인할 열린 항목

1. macOS 앱 번들/worker에 시스템 오디오 권한이 어떻게 귀속되는지와 서명 방식: T00-03 실기기 확인.
2. Rust↔whisper.cpp binding 및 Silero 모델 정확한 버전: T00-01/T00-04에서 pin.
3. 실제 모델 변환본의 저장소·파일명·SHA-256·배포 라이선스: T00-04에서 확인. 예측한 HF 저장소 이름 금지.
4. Qwen 기준 후보의 한국어 번역 품질·기본 프리셋: T00-04 baseline 후 T05-01 확정.
5. Mac fullscreen/Spaces와 Windows 게임 창의 overlay 범위: T00-04/T04-03 검증.
6. 제품명·상용 배포 방식·가격·라이선스 정책: 기술 MVP 이후 사용자 결정. 이 문서는 상표/도메인 확보를 의미하지 않음.

## C. 앞선 아이디어에서 명세로 구체화한 부분

- 크로스플랫폼에서 달라지는 것은 캡처만이 아니다. 권한·창 동작·가속 라이브러리·서명·비밀 저장소도 플랫폼 어댑터에 둔다.
- Whisper 전사와 임의 언어 번역을 분리한다. `--task translate`로 일본어→한국어가 된다고 구현하지 않는다. [R06]
- NLLB 기본 모델은 배포 후보에서 제외했다. 공개 모델 카드의 비상업 라이선스 때문에 판매 가능성을 열어 둔 기본안과 맞지 않는다. [R14]
- 1–2초를 보장하는 제품으로 먼저 약속하지 않는다. 오디오 입력→발화 종료→ASR→번역→UI의 실제 지연을 측정한다.
- oMLX/LM Studio는 개발 MVP의 선택적 외부 서버다. 최종 제품이 반드시 다른 앱 설치를 요구하는 구조로 고정되지 않게 한다.

## D. 출처

### R01 — Microsoft: WASAPI Loopback Recording

확인 내용: 재생 endpoint의 시스템 오디오 수집, shared-mode loopback, 보호 콘텐츠 등의 캡처 제한.

`https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording`

적용: AUD-001/002, Windows CaptureAdapter. endpoint loopback과 특정 앱만의 loopback을 구분한다.

### R02 — Apple: Meet ScreenCaptureKit, WWDC22

확인 내용: SCStream/SCContentFilter/SCStreamConfiguration, 시스템 오디오 sample 전달, 사용자 동의, 앱 단위 오디오 필터.

`https://developer.apple.com/videos/play/wwdc2022/10156/`

추가 API 문서:
`https://developer.apple.com/documentation/screencapturekit/capturing-screen-content-in-macos`

적용: macOS 캡처 브리지, 권한/필터 설계. 최신 SDK의 상세 시그니처는 구현 시 헤더/공식 문서로 다시 확인한다.

### R03 — Avalonia: Supported platforms / Windows

확인 내용: Windows/macOS 대상, OS별 지원 단계와 투명 창 동작의 차이.

`https://docs.avaloniaui.net/docs/supported-platforms`

`https://docs.avaloniaui.net/docs/platform-specific-guides/windows`

적용: UI 선택과 플랫폼별 창 어댑터. 공통 UI 지원을 모든 fullscreen/click-through 조합 보장으로 해석하지 않는다.

### R04 — ggml-org: whisper.cpp

확인 내용: C/C++ ASR, C API, Windows/macOS 지원, CPU/Metal/NVIDIA GPU 경로와 양자화.

`https://github.com/ggml-org/whisper.cpp`

C API:
`https://raw.githubusercontent.com/ggml-org/whisper.cpp/master/include/whisper.h`

적용: ASR 어댑터. 제품의 발화 분할·취소·역압은 별도 구현 사항이다.

### R05 — Silero VAD

확인 내용: 음성 구간 검출, ONNX 사용 경로, 8/16kHz 지원과 MIT 표기.

`https://github.com/snakers4/silero-vad`

적용: VAD 선택. 문서의 보편적 속도 주장을 본 장치의 실측으로 사용하지 않는다.

### R06 — OpenAI Whisper README

확인 내용: 다국어 모델과 `.en` 모델의 구분, 원문 전사와 영어 대상 translation task의 구분, 코드/가중치 MIT 표기.

`https://github.com/openai/whisper`

직접 읽을 수 있는 README:
`https://raw.githubusercontent.com/openai/whisper/main/README.md`

적용: ASR 언어/번역 단계 분리. README의 다른 실행 환경 VRAM 수치를 whisper.cpp 제품 측정값으로 그대로 사용하지 않는다.

### R07 — Qwen 공식 모델 카드

확인 내용: Qwen3-4B-Instruct-2507의 4B 구조, non-thinking 모드, Apache-2.0 표기.

`https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507`

적용: 작은 번역 기준 모델 후보. 모델 카드의 일반 성능은 일본어→한국어 실시간 자막 품질 보증이 아니다.

### R08 — LM Studio OpenAI Compatibility

확인 내용: `/v1/models`, `/v1/chat/completions`, 사용자 지정 base URL과 실제 model ID.

`https://lmstudio.ai/docs/developer/openai-compat`

적용: 외부 로컬 번역 backend 계약.

### R09 — oMLX 공식 저장소

확인 내용: Apple Silicon 서버, OpenAI-compatible API, 모델 목록, macOS 요구사항, 자체 SSD 캐시 기능.

`https://github.com/jundot/omlx`

적용: Mac 외부 번역 backend. 앱의 무저장 정책과 외부 서버 캐시 정책을 구분한다.

### R10 — llama.cpp llama-server 문서

확인 내용: 로컬 서버, 모델 목록·Chat Completions, 인증/health 등 구성 옵션. 완전한 모든 API 동작의 동일성을 보장하지 않는다는 문서 설명.

`https://raw.githubusercontent.com/ggml-org/llama.cpp/master/tools/server/README.md`

적용: 관리형 번역 backend 후보. 옵션은 고정한 버전에서 다시 검증한다.

### R11 — Microsoft: Application loopback audio capture sample

확인 내용: 특정 프로세스 트리를 포함/제외하는 별도 loopback 경로와 시스템 요구조건.

`https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/`

적용: 후속 앱별 캡처. 기본 endpoint loopback으로 앱별 캡처까지 된다고 가정하지 않는다.

### R12 — OpenAI: AGENTS.md instructions

확인 내용: Codex의 프로젝트 지침 발견과 계층, 프로젝트 지침 크기 한도.

`https://developers.openai.com/codex/guides/agents-md`

확인 시 공식 안내는 다음 주소로 연결됨:
`https://learn.chatgpt.com/docs/agent-configuration/agents-md`

적용: 짧은 루트 AGENTS.md와 별도 명세 문서. 전체 명세가 자동으로 모두 읽힌다고 가정하지 않고 시작 지시문에서 명시적으로 읽게 한다.

### R13 — Silero VAD 공식 ONNX wrapper 구현

확인 내용: 16kHz 경로의 512-sample 입력, state/context 관리와 모델 호출.

`https://raw.githubusercontent.com/snakers4/silero-vad/master/src/silero_vad/utils_vad.py`

적용: Rust ONNX adapter의 계약 테스트. upstream 최신 코드 대신 실제 채택한 commit/model을 고정한다.

### R14 — Meta NLLB-200 distilled 600M 모델 카드

확인 내용: 공개 가중치의 `cc-by-nc-4.0` 표기.

`https://huggingface.co/facebook/nllb-200-distilled-600M`

적용: 기본 배포 모델 제외 결정. 별도 허가가 확보되면 의사결정을 다시 기록할 수 있다.

## E. 출처 유지 규칙

모델/의존성을 바꿀 때 원본 모델과 변환본, 코드와 가중치 라이선스를 각각 확인한다. 확인하지 않은 API/버전을 검색 결과 제목만 보고 확정하지 않는다. 성능 보고에는 자체 측정 결과 파일을 출처로 연결한다.

