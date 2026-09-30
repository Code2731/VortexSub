# 구현 상태

최종 갱신: 2026-09-30. 결과 범위: Windows T00-01, 독립형 T00-02 WASAPI probe, T00-04.1 모델 harness, T00-04.2 실제 취소·수명 probe, T00-04.4 동시 부하, T00-04.3 MOCK 오버레이, T01-01a/b 독립 오디오·발화 코어, T01-02a 독립 상태·작업 큐. 실제 캡처·추론·번역을 worker/UI에 연결하는 기능, 실제 자막과 macOS 번들은 미구현/미검증이다.

| 작업 | 상태 | 근거 및 다음 단계 |
|---|---|---|
| BASE-00 | PARTIAL; T00-01 준비 완료 | 분리 명세·SDK·대응표, 고정 모델 3개·합성 음원 마련. 자연/일본어 음원과 Mac 정보 미확보. |
| T00-01 | PASS (Windows); BLOCKED (macOS) | 양 언어 빌드, worker 프로토콜, C# 클라이언트, 숨김 GUI 실행 중 자식 worker 생성과 부모 강제 종료 후 worker 정리 확인. 실제 창 클릭/시각 QA와 Mac 실행은 미실행. |
| T00-02 | PARTIAL (Windows) | WASAPI render loopback probe가 실제 PCM·mix format·레벨·device/QPC 위치·플래그를 출력한다. 600.2초 실행에서 587.32초 분량의 PCM 프레임 수신. 통제된 fixture, 실제 기본 장치 전환·고정 장치 분리 시험은 미실행. |
| T00-03 | BLOCKED (macOS) | Mac 실기기와 번들·권한 환경 없음. |
| T00-04.1 | PARTIAL (Windows); BLOCKED (macOS) | 동의받은 모델 3개 다운로드·SHA 검증, 재사용 ASR context/fixture harness와 로컬 번역 harness 구현. 합성 en/ko 각 10개+무음, 번역 en/ja 각 10개 실행. 자연 발화·일본어 음원·VRAM peak·Mac 미검증. 품질 채택 보류. [번역 검토](evidence/T00-04.1-translation-review.md) |
| T00-04.2 | PASS (Windows probe scope); BLOCKED (macOS) | CPU/CUDA base/small 각각 두 시점별 취소·같은 context 재시작 10회, 정상 종료 10회, 강제 종료 후 새 프로세스 복구 10회. CPU small encoder 취소 최대 2.254569초. 협력 취소+native 반환 대기 정책. 캡처 Stop/epoch/UI 통합·Mac 미검증. [측정·수명 정책](evidence/T00-04.2-windows-cancellation.md) |
| T00-04.4 | PASS (Windows probe scope); BLOCKED (macOS) | 5조건 각 302초, 실제 동시 구간 300초 이상. base 동시 전사/번역 각 1208회 완료·건너뜀 0; small 동시 전사 1189회 완료·19개 건너뜀. 오류/OOM 0. base 성능 후보 유지, small 4 Hz 기본값 보류. [측정과 후속 설정](evidence/T00-04.4-windows-contention.md) |
| T00-04.3 | PARTIAL (Windows); BLOCKED (macOS) | MOCK 오버레이, 표시/숨김, 메인 창 폭·불투명도 조절, 드래그 핸들, 화면 작업 영역 기준 초기 위치 구현. 125% 배율의 HWND 속성·투명도·크기/위치 변경 확인. 다른 앱 포커스·게임 합성·실제 마우스 조작은 미검증. |
| T01-01 | PARTIAL: T01-01a/b PASS (Windows fixture scope) | 공통 float32 mono/stereo 정규화, 16 kHz/512-frame, sample 시간축·gap, 12초 rolling/유한 immutable snapshot 구현. 정규화 15개+VAD 15개 fixture. 확률 기반 발화·8초 분할·watchdog 구현. 실제 Silero·native clock·worker/Mac 미검증. [오디오](AUDIO_CORE.md) · [VAD](VAD_CORE.md) |
| T01-02 | PARTIAL: T01-02a PASS (Windows fixture scope) | 단일 실행·final 2/partial 1·번역 2 대기, 전체 키 검증, final 동결, 취소 반환 대기, 번역 terminal과 버전 history 구현. fixture 23개. 실제 worker/IPC/event 큐·native 미연결. [계약·검증](PIPELINE_CORE.md) |
| M1 | PARTIAL | 정규화·발화·상태/작업 큐의 결정론적 코어 구현. 실제 Silero 어댑터와 T01-02b worker 전달·유한 event 큐·IPC snapshot은 미구현. |
| M2~M5 | NOT_STARTED | 해당 제품 통합/실기기 수용 결과 없음. |

## 확인된 Windows 개발 환경

| 항목 | 관측값 |
|---|---|
| OS | Windows NT 10.0 build 26200, win-x64 |
| CPU | AMD64 Family 25 Model 33, 논리 프로세서 16개 (`PROCESSOR_IDENTIFIER`) |
| GPU | NVIDIA GeForce RTX 3080, 10240 MiB, 드라이버 617.14 (`nvidia-smi`) |
| RAM | 미확인. WMI/CIM 조회가 접근 거부됨 |
| Rust | rustc/cargo 1.90.0, stable Windows MSVC |
| .NET | SDK 10.0.102, 런타임 10.0.2 |
| Git | 2.55.0.windows.3; 이 저장소는 이번 작업에서 초기화됨 |
| Native 도구 | VS 2022 Community MSVC 14.44.35207, CMake 3.31.6, Ninja, `C:/Program Files/LLVM/bin/libclang.dll`, CUDA Toolkit 12.6 확보. ASR 스크립트가 설치 경로를 찾아 프로세스 PATH에 추가 |
| Mac | 실기기, OS, SDK, 서명/권한 정보 미확인 |

## 실행 결과

| 범위 | 명령 또는 방법 | 결과 |
|---|---|---|
| Rust 컴파일 | `cargo check --workspace --offline` | PASS: Windows capture crate 포함 |
| Rust 시험 | `cargo test --workspace --locked --offline` | PASS: worker 프로토콜 5개, PCM 레벨 2개 |
| UI 빌드 | `dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore` | PASS: 경고 0, 오류 0. `AVALONIA_TELEMETRY_OPTOUT=1` 설정 |
| C#↔Rust 통합 | `dotnet run --project tests/EchoSub.ProtocolSmoke/EchoSub.ProtocolSmoke.csproj --no-build -- <worker.exe>` | PASS: Unicode, 64개 동시 요청, 정상/강제 종료 |
| 저장소 검증 스크립트 | `ECHOSUB_OFFLINE=1`, 로컬 NuGet source로 `scripts/check.ps1` | PASS: Rust 포맷/7개 시험, 양 프로젝트 빌드, C# smoke |
| Windows 앱 프로세스 | 숨김 `EchoSub.Desktop.exe` 실행 후 프로세스 관찰 | PASS: UI 프로세스 생존, 자식 worker 생성, 부모 종료 후 새 worker 0개 |
| Windows UI 시각·클릭 확인 | 창에서 연결·Ping·재연결 | SKIPPED: 숨김 프로세스 시험만 수행 |
| Windows 장치 열거 | `echosub-capture-windows --list` | PASS: 활성 render endpoint 7개, 기본 장치 `스피커(GSX 1000 Main Audio)` 식별 |
| Windows 지속 loopback | `echosub-capture-windows --seconds 600` | PARTIAL HW-W01: 48 kHz, stereo, float32, mask `0x3`; 실행 600.2초, 58,732 packets/28,191,360 frames(587.32초), silent 41, 첫 packet discontinuity 1, timestamp error 0, position gap 0, 마지막 무음 중 250ms timeout 29. [원본 메트릭](evidence/T00-02-windows-10min.log) |
| Windows 장치 고정 | `--device-id`로 기본 ID 및 없는 ID 지정 | PARTIAL HW-W02: 실제 ID로 capture 시작, 없는 ID는 전환 없이 `device_unavailable` 및 exit 1. 실제 장치 전환·분리 미실행 |
| Windows MOCK 오버레이 | `scripts/probe-overlay.ps1` | PASS (관측 범위): 125% 배율, 실제 Transparent, HWND TOPMOST/NOACTIVATE/TOOLWINDOW, 표시→크기/위치 변경→숨김→재표시. [보고서](evidence/T00-04.3-windows-overlay.json), [Avalonia 렌더 프레임](evidence/T00-04.3-windows-overlay.png). 다른 앱과 합성한 데스크톱 캡처는 아님 |
| Windows 오버레이 포커스 | probe 전/후 `GetForegroundWindow` | BLOCKED: 세 번 모두 HWND 0. 유지 여부는 null이며 PASS로 간주하지 않는다. 물리 드래그/리사이즈·다른 앱 입력·게임 위 표시도 미실행 |
| macOS 빌드/실행 | Mac에서 `bash scripts/check.sh` | BLOCKED: Mac 환경 없음 |
| Windows 모델 probe 및 Mac 캡처 | P0-MODEL, HW-M01/02 | PARTIAL (Windows 합성 ASR/작성 번역); BLOCKED (Mac) |

T00-04.1 추가 검증: 기본 workspace Rust 시험 9개(프로토콜 5, PCM 2, scoring 2), UI·C# smoke 빌드/실행, 번역 .NET 빌드가 통과했다. native CPU/CUDA release와 실제 모델별 21회 전사도 성공했다. 초기 CPU 빌드의 `/O2` 누락과 CUDA architecture 오류를 수정했다. 최적화 CPU 재측정은 base 평균 약 0.621~0.695초, small 약 2.317~2.379초다. 초기 비최적화/컴파일 부하 측정은 채택 비교에서 제외한다. 합성 음성의 WER/CER는 실제 발화 수용 근거가 아니다. 번역은 20회 성공했으나 귀환 조건을 출발 조건으로 잘못 옮긴 사례가 있어 품질 gate는 false다. [실행·오류·한계](evidence/T00-04.1-windows-models.md)

T00-04.2 추가 검증: 최종 두 시점 실행에서 취소/재시작 총 80회, 정상 종료 40회, 소유 자식 강제 종료 및 복구 40회 통과. 취소 callback acknowledgement, 출력 segment 미반환, 다음 decode의 비겹침·문자열 일치, context 해제 후 join을 확인했다. pre-cancel과 token 재사용 거부도 통과했다. native 취소는 연산 경계에서 지연될 수 있으므로 캡처/UI 제어 스레드에서 기다리지 않는다. 모든 새 ASR/취소 시간 출력은 초다.

Windows probe는 render endpoint의 loopback만 열며 microphone endpoint를 열지 않았다. 실제 음원 fixture를 시간에 맞춰 재생한 통제 검증과 전환·분리 시험이 없으므로 HW-W01/W02 gate는 PASS로 올리지 않았다. `--seconds 600`은 실행 벽시계 시간이며, 초기 장치 open과 마지막 재생 중단 때문에 누적 PCM 프레임은 600초에 못 미쳤다. 마지막 silent 패킷 뒤에는 packet이 멈춰 timeout으로 기록됐다.

## 미해결 항목

2026-09-30 Cargo 경로 수정: 사용자 런처의 `cargo` 인식 실패를 확인했다. 설치는 `%USERPROFILE%/.cargo/bin/cargo.exe`에 존재하지만 탐색기에서 시작한 프로세스의 PATH에서 누락될 수 있다. 런처가 PATH 및 표준 설치 폴더를 검색하고 절대 실행 경로를 사용하도록 수정했다. .NET도 같은 방식으로 찾는다. PATH를 System32만 남긴 Windows PowerShell 5.1 실행에서 두 SDK 검색→경고/오류 없는 빌드→메인 창·worker 연결→정상 종료를 확인했다. 시스템/사용자 전역 PATH는 변경하지 않았다.

2026-09-30 실행 진단 보완: `run.ps1`의 우클릭 실행에서 창이 보이지 않는 신고가 있었다. 재현 실행에서는 NuGet 온라인 조회 경고 후 실제 메인 HWND와 worker가 생성됐다. 사용자 실행의 최초 종료 원인은 미확정이다. 런처 단계 출력·오류 시 대기·`logs/` 실행/시작 로그, 패키지 복원 재사용, 직접 EXE 실행과 중앙 배치를 추가했다. Windows PowerShell 5.1에서 빌드→실제 창 visible→worker 연결→WM_CLOSE 정상 종료/worker 정리를 확인했다. 루트 `run.cmd`는 더블클릭 진입점이다.

- 로컬 NuGet 패키지는 존재했지만 기본 sandbox global-packages 경로와 달라 처음 복원에 실패했다. `ECHOSUB_NUGET_SOURCE`와 저장소 내부 `.nuget/packages` 경로로 복원했다.
- Avalonia build telemetry가 허용되지 않은 AppData 로그 경로에 쓰려 하여 sandbox 빌드가 실패했다. 공식 환경변수 `AVALONIA_TELEMETRY_OPTOUT=1`을 검증/실행 스크립트에서 설정한다.
- 모델 3개와 native Windows 도구, 로컬 합성 fixture는 확보했다. 자연 발화·일본어 음원·배경음·긴 발화와 Mac 실기기는 미확보다. 모델 revision/hash/license와 재현 명령은 `benchmarks/model-downloads.json`과 `benchmarks/README.md`를 따른다.
- T00-02 probe는 독립 실행 파일이며 250ms 장치 상태 polling을 쓴다. worker의 capture capability는 아직 false다. T02-02에서 장치 알림, PCM 버퍼 소유권, 수명 계약을 설계하고 통합한다.

T00-04.3의 Windows 창 속성과 Avalonia 프레임은 확인했으나 다른 앱의 입력 포커스를 조회할 수 없었다. `ShowActivated=false`와 `WS_EX_NOACTIVATE`의 적용 사실을 입력 유지의 실측으로 확대하지 않는다. [실행 및 수동 검증 절차](OVERLAY_PROBE.md)를 일반 사용자 데스크톱에서 수행한다.

T00-04.4 Windows 동시 부하 probe를 완료했다. T01-01a 독립 오디오 코어도 구현했다. T01-01b 확률 기반 발화 구간·packet-stop watchdog도 구현했다. T01-02a 상태기계·유한 작업 큐도 구현했다. 다음 단위는 T01-02b worker 전달·유한 event 큐·버전 snapshot 연결이다. T00-04.1은 자연 음성/일본어/Mac, T00-04.2는 Mac·실제 worker 통합 보완이 필요하며 M0 전체는 미통과다. T00-02의 통제 음원 10분·장치 전환/분리와 T00-04.3의 다른 앱 입력·게임 위 표시·수동 조절은 실제 조작으로 완료한다.


T00-04.4 추가 검증: 총 8437회 추론 완료, 실패 0, small 전사 요청 건너뜀 19. GPU 사용과 번역 full offload를 로그로 확인했고 소유 프로세스/API key 잔여는 0이다. 장치 전체 GPU 메모리 관측 최대는 small 동시 6721 MiB이며 프로세스 VRAM peak가 아니다. 모든 새 번역·동시 부하 시간도 초다. 실제 자막 latency·게임 공존·품질 gate·Mac은 미검증이다.

T01-01a 추가 검증: Windows의 pure PCM fixture 15개와 기존 9개 시험, Rust fmt/workspace·C# 빌드와 IPC smoke 통과. 필터 center 기준의 sample 범위, packet 분할 불변성, 고주파 alias 억제, backwards anchor/stale epoch 거부, snapshot 수명·고갈을 확인했다. 실제 VAD 무음 억제·ASR 호출 0회·native callback 비차단 수용·Mac은 미검증이다.


T01-01b 추가 검증: mock 확률 VAD fixture 15개와 기존 24개, Rust fmt/workspace·C# 빌드·IPC smoke 통과. 600초 exact-zero PCM에서 mock 모델/ASR 요청 0, 8초/overlap 분할, healthy packet-stop의 실제 PCM만 final, 오류/Pause/Stop 폐기와 gap reset을 확인했다. 실제 Silero state/context·자연 음성·native watchdog·Mac 수용은 미검증이다. [범위와 후속 계약](VAD_CORE.md)

T01-02a 추가 검증: 생성 PCM/mock 결과 fixture 23개와 기존 39개, Rust fmt/workspace·C# 빌드·IPC smoke 통과. 최신 partial·final 우선/동결·큐 초과 기록, epoch/새 session 뒤 stale 결과 거부, native 반환 전 예약 유지, 번역 deadline/terminal, history 버전·1,000개 상한을 확인했다. 실제 native/HTTP·worker 전달/event 큐·IPC snapshot·overlap 텍스트 정합·Mac은 미검증이다. [계약과 다음 단위](PIPELINE_CORE.md)
