# EchoSub

**한국어** | [English](README.en.md)

EchoSub는 Windows/macOS의 시스템 오디오를 전사하고 한국어로 번역하는 데스크톱 앱을 목표로 합니다.

**현재는 M2 전사와 M3 번역 통합을 개발하는 단계입니다.** 진단 worker에서 실제 Windows loopback→Silero VAD→Whisper→원문 history를 연결했습니다. 파일 전사도 지원합니다. 진단 원문 history·오버레이 UI, UUID 세션 시작·일시정지·재개·종료와 세션별 history를 연결했습니다. opt-in worker의 파일 전사→로컬 HTTP 번역→history도 확인했습니다. 로컬 서버 설정·번역 history/오버레이 표시도 연결했습니다. 실제 화면 수용과 전체 제품 wire 전환은 후속이며 macOS는 실기기에서 검증하지 않았습니다.

## Windows에서 빌드하고 실행하기

고정된 **Rust 1.90.0**과 **.NET SDK 10.0.102**가 필요합니다. Avalonia 버전은 데스크톱 프로젝트에 고정되어 있습니다. 저장소 루트에서 실행하세요.

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
.\scripts\check.ps1
.\scripts\run.ps1
```

- `check.ps1`: Rust 포맷 검사·기존 시험, Rust/C# 프로젝트 빌드, C#↔Rust IPC smoke 검증을 실행합니다.
- `run.ps1`: worker와 UI를 빌드하고 MOCK UI를 엽니다.
- 루트의 **`run.cmd`를 더블클릭**해도 실행할 수 있습니다. 단계별 진행과 오류를 표시하고 앱이 닫힐 때까지 기다립니다. 시작 로그는 Git에서 제외된 `logs/`에 저장합니다.
- 이미 빌드했다면 `scripts/run.ps1 -NoBuild`, 캐시된 의존성으로 실행하려면 `-Offline`을 사용하세요.

런처는 PATH에 SDK가 없을 때 `CARGO_HOME/bin`, `%USERPROFILE%/.cargo/bin`, `DOTNET_ROOT`, `%ProgramFiles%/dotnet`도 검색합니다. 선택한 SDK 경로를 표시하고 해당 프로세스의 PATH에 추가합니다. `run.cmd`의 PowerShell 실행 정책 설정도 해당 프로세스에만 적용됩니다.

오프라인 NuGet 환경에서는 `ECHOSUB_NUGET_SOURCE`를 로컬 패키지 소스로 지정하고 Cargo에 `ECHOSUB_OFFLINE=1`을 설정한 뒤 검사 스크립트를 실행하세요. 패키지 캐시는 Git에서 제외된 `.nuget/`에 둡니다. 검사·앱 실행 스크립트는 모델을 다운로드하지 않습니다.

macOS에서는 고정 SDK 설치 후 `bash scripts/check.sh`를 실행합니다. Mac 실기기 결과가 확보되기 전까지 지원 검증은 미완료입니다.

## 실제 원문 진단 UI

**번역 서버까지 함께 실행하려면 루트의 `run-live.bat`을 더블클릭하세요.** 기존 Qwen 모델과 설치된 `llama-server`를 사용하며, 서버 준비 후 실제 UI를 엽니다. UI에서 **서버 연결 / 모델 조회**를 누르고 번역 Ready 뒤 세션을 시작하세요. 앱 종료 시 함께 띄운 서버도 종료합니다. `-NoBuild`와 `-ServerPath` 옵션은 [실행 안내](docs/DESKTOP_TRANSLATION.md)를 참고하세요.

CUDA 전사를 비교하려면 **`run-live-cuda.bat`**을 실행하거나 `run-live.bat -AsrBackend cuda`를 사용하세요. NVIDIA GPU와 설치된 CUDA Toolkit이 필요합니다. 기본 전사는 CPU이며, CUDA의 실제 번역·게임 동시 성능은 아직 미검증입니다. [파일 비교와 실행 안내](docs/LIVE_UI.md).

더 빠른 임시 번역 비교는 **`run-live-cuda-fast.bat`**으로 실행하고 세션 시작 전에 **안정된 부분 먼저 번역 · 임시 결과**를 켜세요. 첫 요청은 0.8초이며, 이후에는 전사 결과에 따라 재확인·추가 음성 대기를 조절합니다. 미완성 정보와 GPU 비용이 늘 수 있으며 기본값은 유지합니다. [실제 ASR→Qwen 측정](docs/evidence/paced-translation-windows-20261001.md) · [적응형 스케줄·확인 범위](docs/ADAPTIVE_PARTIALS.md).

현재 draft 갱신 대기도 줄였습니다. 앱의 **자막 지연 기록** 옵션으로 수신→카드 반영 시점을 텍스트 없는 로그에 기록할 수 있습니다. 실행 중 켜고 끌 수 있고 저장 경로를 표시합니다. 이전 자막 읽기 시간은 유지합니다. [표시 규칙·계측](docs/CAPTION_READING.md).

`./run.cmd -Live -Offline`로 실행하고 모델 Ready 뒤 출력 장치·원문 언어를 선택해 **세션 시작**을 누르세요. 원문 history와 오버레이를 연결했습니다. 부분 전사는 기본 꺼짐이며 세션 시작 전 **부분 전사 켜기**로 선택합니다. 로컬 번역 서버를 별도로 실행한 뒤 **서버 연결 / 모델 조회**에서 모델을 준비하면 한국어 번역도 표시합니다. [서버 설정·표시 계약](docs/DESKTOP_TRANSLATION.md). [부분 전사 계약](docs/WORKER_PARTIAL_ASR.md). 일시정지/종료 후 정리가 끝나면 세션별 **TXT 저장 / 원문 SRT 저장**을 사용할 수 있습니다. [저장·UTC·시간축](docs/HISTORY_EXPORT.md). 선택 세션의 메모리 기록 삭제도 제공합니다. 실제 클릭·원문 화면 수용은 미검증입니다. [실행과 확인 범위](docs/LIVE_UI.md)

TabbyAPI/ExLlama 번역 후보는 `run-tabby.bat`으로 별도 실행합니다. 동의받아 설치한 EXL3 모델·런타임이 필요하며, `run-live.bat`과 동시에 실행하지 마세요. [엔진 비교·설치·측정 조건](docs/TRANSLATION_ENGINES.md).

부분 번역을 실험하려면 세션 시작 전에 **안정된 부분 먼저 번역 · 임시 결과 / 기본 끔**을 선택하세요. 부분 전사도 함께 켜지고 오버레이에 **[임시 번역]**으로 표시합니다. 뒤의 문맥에 따라 뜻이 바뀔 수 있어 기본 꺼짐을 유지합니다. [처리 계약·오프라인 비교](docs/STREAMING_TRANSLATION.md). SenseVoice/Fun-ASR 및 Qwen3 1.7B는 [비교 후보](docs/MODEL_CANDIDATES.md)로 조사했으며 아직 설치·실측하지 않았습니다.

## Windows 시스템 오디오 캡처 probe

```powershell
cargo run -p echosub-capture-windows -- --list
cargo run -p echosub-capture-windows -- --seconds 600
cargo run -p echosub-capture-windows -- --seconds 600 --device-id '{0.0.0.00000000}.{device-guid}'
```

`--list`는 활성 재생 장치의 ID·이름과 기본 장치를 표시합니다. 기본 모드는 console 기본 재생 장치를 따라가며, `--device-id`는 지정 장치만 사용합니다. 고정 장치가 사라져도 다른 장치로 넘어가지 않습니다.

probe는 WASAPI loopback에서 실제 오디오 형식, 패킷·프레임 수, 장치/QPC 위치, 무음·불연속 플래그, timeout, 1초 단위 peak/RMS 레벨을 출력합니다. PCM은 저장하지 않습니다. 장치 상태를 polling하고 기본 장치 변경이나 캡처 실패 시 다시 엽니다. 알려진 시스템 재생 음원으로 레벨 변화를 확인하세요. 실제 측정 결과와 남은 장치 전환·분리 검증은 [구현 상태](docs/STATUS.md)에 기록합니다.

## 전사·번역 모델 probe

[모델 catalogue](benchmarks/model-downloads.json)는 Whisper base/small과 Qwen3-4B-Instruct-2507 Q4_K_M의 revision·크기·SHA-256을 고정합니다. 다운로드 전에 용량과 라이선스에 대한 동의가 필요합니다. 모델 파일과 생성 음원은 Git에 포함하지 않습니다.

빌드 도구, 모델·fixture 준비, 실행 명령과 측정 범위는 [벤치마크 안내](benchmarks/README.md)를 따르세요. native ASR에는 CMake/LLVM/MSVC, CUDA 실행에는 CUDA Toolkit이 추가로 필요합니다. 일반 workspace 빌드는 native 추론을 포함하지 않습니다.

- `scripts/probe-asr.ps1`: CPU 또는 CUDA에서 독립 전사를 측정합니다.
- `scripts/probe-translation.ps1`: 설치된 로컬 `llama-server`로 번역을 측정합니다.
- `scripts/probe-cancellation.ps1`: 실제 native 취소, 같은 context 재시작, 정상 해제, 소유 자식 강제 종료·복구를 측정합니다. [취소·수명 보고서](docs/evidence/T00-04.2-windows-cancellation.md)
- `scripts/probe-contention.ps1`: base/small 단독, 번역 단독, 각 ASR 모델과 번역의 동시 실행을 조건마다 302초 측정합니다. [동시 부하 보고서](docs/evidence/T00-04.4-windows-contention.md)

새 전사·번역·취소·동시 부하 시간 출력은 **초**입니다. 모델 로딩·처리 시간은 실제 자막 표시 지연과 구분합니다. 합성 음원과 작성된 번역 문장은 진단용 자료입니다. 자연 발화 품질, 게임 공존, 장기 안정성, macOS는 별도 검증이 필요하며 모델의 제품 채택은 보류 중입니다.

## 공통 오디오 코어 (M1 진행 중)

`crates/audio-core/`는 44.1/48 kHz mono·stereo를 16 kHz mono로 변환하고,
512-sample frame, 세션 sample 시간축, 12초 rolling buffer와 유한 immutable
PCM snapshot을 제공합니다. OS·모델 없이 `cargo test -p echosub-audio-core`
로 fixture를 실행할 수 있습니다. 발화 구간·8초 분할·packet-stop watchdog과
실제 Silero의 worker 파일·live 캡처 연결도 구현했습니다. 진단 원문 UI를 연결했으며 화면 수용은 미검증입니다.
[오디오 코어](docs/AUDIO_CORE.md) · [VAD 구간 계약과 검증](docs/VAD_CORE.md)

## 파이프라인 상태·작업 큐 (M1 진행 중)

`crates/pipeline-core/`는 최신 partial 교체, final 우선 큐, 늦은 결과 폐기,
번역 deadline·terminal 상태와 최대 1,000개 버전 history를 제공합니다.
`cargo test -p echosub-pipeline-core`로 mock fixture를 실행합니다.
worker event 큐·버전 history snapshot과 C# 클라이언트를 연결했습니다.
진단 UI history를 연결했습니다. 실제 HTTP와 화면 수용은 후속입니다. 일반 실행의 history는 비어 있고,
진단용 생성 이벤트는 `--mock-pipeline`에서만 허용합니다.
[상태·큐 계약](docs/PIPELINE_CORE.md) · [worker 전달·복구](docs/WORKER_DELIVERY.md)

## Worker 실제 파일 전사 (M2 진행 중)

`scripts/probe-worker-asr.ps1 -Backend cpu -Offline`은 기존 Whisper base와
합성 WAV로 실제 native 전사→IPC history, 취소·epoch 재시작과 추론 중 종료를
검증합니다. 전용 추론 스레드가 context를 재사용하며 제어 요청은 반환을 기다리지
않습니다. 입력은 16 kHz mono·최대 8초이며 VAD·번역·실시간 캡처를 사용하지 않습니다.
모든 측정 시간은 초입니다. [실행·소유권 계약](docs/WORKER_ASR.md) ·
[CPU 측정](docs/evidence/T02-01a-windows-worker-asr.md)

`scripts/probe-worker-vad.ps1 -Offline`은 동의받아 확보한 Silero v6.0과
ONNX Runtime 1.22.0 CPU로 발화 범위를 나누고 실제 전사합니다. 무음·생성 톤/잡음
억제, 두 발화 분리·epoch reset·해시 오류를 확인했습니다. 한국어 fixture의
추가 후보 하나는 빈 ASR 출력으로 실패 기록이 남으며 **품질 gate는 미통과**입니다.
[VAD 실행·계약](docs/WORKER_VAD.md) · [측정](docs/evidence/T02-01b-windows-worker-vad.md)

## Worker 실시간 원문 진단 (M2 진행 중)

`scripts/probe-worker-live-asr.ps1 -Offline`은 기존 영어 TTS를 잠깐 재생해
실제 loopback→연속 Silero→Whisper→history를 확인합니다. 추론 중 Stop·재시작,
시간축 gap, 해시 오류와 활성 종료도 검사합니다. 부분 전사 검사는 `-Partials`로 실행합니다. 화면 수용·번역은 후속이며
게임/자연 음성 품질 gate는 미통과입니다. [live 실행·계약](docs/WORKER_LIVE_ASR.md)

## 전사 정합·번역 계약

전사 경계에는 [token 시간 정합](docs/ASR_TOKEN_ALIGNMENT.md)을 추가했습니다.
600초 **파일 PCM** 무음 검증에서 VAD/ASR/history 생성이 없었습니다.
실제 live dedup 품질은 미검증입니다. M3 [로컬 번역 계약](docs/TRANSLATION_CONTRACT.md)은
HTTP owner·모델 조회/선택·취소·응답 검사를 구현했습니다. worker의 확정 원문→번역→history도
연결했습니다. `scripts/probe-translation.ps1 -Worker -Offline`로 실제 파일 ASR→로컬 서버를
검사할 수 있습니다. [worker 번역 계약](docs/WORKER_TRANSLATION.md). 화면 번역 연결과 품질 수용은 후속입니다.

## MOCK 자막 오버레이

`scripts/probe-worker-capture.ps1 -Offline`은 기존 TTS WAV를 잠깐 재생해
worker의 실제 WASAPI 수신·16 kHz 정규화, 반복 Start/Stop·활성 종료를 확인합니다.
캡처 진단의 `live_asr=false`이며 자막/history를 생성하지 않습니다.
[캡처 owner·상한·미검증 범위](docs/WORKER_CAPTURE.md)

`scripts/probe-capture-startup.ps1 -Offline`은 모델·재생 없이 새 worker를
출력 장치별로 시작해 지연·실패·종료를 비교합니다. `-DeviceId default -Rounds 10`으로
기본 선택만 반복할 수 있습니다. [실행과 측정 한계](docs/CAPTURE_STARTUP_PROBE.md)

`-Rounds 20`으로 시작/정지 반복 측정을 늘릴 수 있습니다. 시작 대기는 10초에
실패로 기록되며 API 위치·초 단위 경과 시간을 남깁니다. native 종료 시간은
별도로 확인해야 합니다. [시작 지연 관측](docs/evidence/T02-02c-windows-capture-startup.md)

`scripts/run.ps1` 실행 후 메인 창의 **샘플 오버레이 표시**를 누르세요. 영어/일본어 원문과 한국어 번역의 샘플을 표시하며 이동 핸들·리사이즈 그립, 폭·불투명도 조절, 숨김·위치 초기화를 제공합니다. 메인 창을 닫으면 오버레이도 닫힙니다. Windows 어댑터는 창 비활성화와 tool window 속성을 적용합니다.

`scripts/probe-overlay.ps1`은 짧은 Windows 창 검증을 실행하고 `docs/evidence/`에 JSON·렌더링 PNG를 저장합니다. 종료 코드 0은 실행 가능한 창 속성 검사의 통과를 뜻합니다. foreground HWND를 조회하지 못한 경우 `focus_result`는 `BLOCKED`이므로 별도로 확인하세요. 실제 입력·마우스 조작, 게임 위 표시, Mac Spaces는 미검증입니다. [오버레이 검증 절차](docs/OVERLAY_PROBE.md)

## 문서와 프로토콜

- 제품 명세: [개념](docs/01_CONCEPT.md)부터 [결정·출처](docs/09_DECISIONS_SOURCES.md)까지 `docs/01`~`09` 문서
- 구현 순서: [구현 계획](docs/IMPLEMENTATION_PLAN.md), [M0 실행 계획](docs/M0_EXECUTION_PLAN.md)
- 현재 결과와 미검증 항목: [구현 상태](docs/STATUS.md)
- 기여 안내: [Repository Guidelines](AGENTS.md)
- IPC 계약: [계약 문서](docs/05_CONTRACTS.md), [v1 JSON schema](schemas/worker-protocol-v1.schema.json)

현재 worker는 `hello`, `ping`, `get_state`, `get_history`, `shutdown`에 응답합니다. `-Live` 실행은 UUID `start_session`/`pause_session`/`resume_session`/`stop_session` 제어도 제공합니다. [세션 계약·검증](docs/SESSION_CONTROL.md). mock 또는 native 파일 진단 명령은 해당 모드를 켰을 때만 허용합니다. 일반 MOCK 실행에는 세션 제어가 없습니다. [설정 예시](examples/config.example.toml)는 향후 설정을 설명하며 현재 worker가 읽는 파일은 아닙니다.


긴 발화의 시간/span 기반 경계 정합과 빈 결과 skip도 연결했습니다. `scripts/probe-worker-live-asr.ps1 -Offline -Boundaries`로 실제 8초 분할·겹침을 관측합니다. 현재 실제 dedup 효과와 격리된 무음 수용은 미검증입니다. [정합 계약·한계](docs/ASR_RECONCILIATION.md).
