# EchoSub

**한국어** | [English](README.en.md)

EchoSub는 Windows/macOS의 시스템 오디오를 전사하고 한국어로 번역하는 데스크톱 앱을 목표로 합니다.

**현재는 M0 probe와 M1 공통 코어를 개발하는 단계입니다.** UI와 Rust worker 사이의 IPC, Windows 시스템 오디오 캡처 probe, 독립 전사·번역·취소·동시 부하 probe, MOCK 자막 오버레이를 구현했습니다. 캡처·추론·번역은 아직 worker/UI에 연결되지 않았으며, 앱에서 실제 실시간 자막을 제공하지 않습니다. macOS는 실기기에서 검증하지 않았습니다.

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
로 fixture를 실행할 수 있습니다. 실제 VAD와 worker/UI 연결은 아직
구현하지 않았습니다. [오디오 코어 계약과 검증](docs/AUDIO_CORE.md)

## MOCK 자막 오버레이

`scripts/run.ps1` 실행 후 메인 창의 **샘플 오버레이 표시**를 누르세요. 영어/일본어 원문과 한국어 번역의 샘플을 표시하며 이동 핸들·리사이즈 그립, 폭·불투명도 조절, 숨김·위치 초기화를 제공합니다. 메인 창을 닫으면 오버레이도 닫힙니다. Windows 어댑터는 창 비활성화와 tool window 속성을 적용합니다.

`scripts/probe-overlay.ps1`은 짧은 Windows 창 검증을 실행하고 `docs/evidence/`에 JSON·렌더링 PNG를 저장합니다. 종료 코드 0은 실행 가능한 창 속성 검사의 통과를 뜻합니다. foreground HWND를 조회하지 못한 경우 `focus_result`는 `BLOCKED`이므로 별도로 확인하세요. 실제 입력·마우스 조작, 게임 위 표시, Mac Spaces는 미검증입니다. [오버레이 검증 절차](docs/OVERLAY_PROBE.md)

## 문서와 프로토콜

- 제품 명세: [개념](docs/01_CONCEPT.md)부터 [결정·출처](docs/09_DECISIONS_SOURCES.md)까지 `docs/01`~`09` 문서
- 구현 순서: [구현 계획](docs/IMPLEMENTATION_PLAN.md), [M0 실행 계획](docs/M0_EXECUTION_PLAN.md)
- 현재 결과와 미검증 항목: [구현 상태](docs/STATUS.md)
- 기여 안내: [Repository Guidelines](AGENTS.md)
- IPC 계약: [계약 문서](docs/05_CONTRACTS.md), [v1 JSON schema](schemas/worker-protocol-v1.schema.json)

현재 worker는 `hello`, `ping`, `get_state`, `shutdown`에 응답합니다. 다른 명령은 `UNSUPPORTED_CAPABILITY`를 반환합니다. [설정 예시](examples/config.example.toml)는 향후 설정을 설명하며 현재 worker가 읽는 파일은 아닙니다.

