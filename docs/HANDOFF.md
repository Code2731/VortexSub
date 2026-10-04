# 작업 인계

갱신일: 2026-10-04. 이 문서는 현재 상태와 재개 지점을 보관한다.
과거 변경 이력은 [STATUS](STATUS.md), 검증 상세는 `docs/evidence/`를 따른다.

## 현재 목표

실시간 번역 자막의 지연과 읽기 문제를 개선하고 배포용 UI/UX를 정리한다.
전체 고도화 계획은 아직 완료되지 않았다.
[번역 개선 계획](TRANSLATION_IMPROVEMENT_PLAN.md)과
[리뷰 개선 계획](REVIEW_IMPROVEMENT_PLAN.md)을 함께 확인한다.

## 소스 동기화 상태

- 로컬 브랜치: `master`.
- 현재 HEAD: `79c1ead` — `docs: research translation engines and quality estimation`.
- 이후 구현·문서 변경과 신규 파일은 작업 트리에 남아 있다. 이번 라운드에서 커밋·푸시하지 않았다.
- HEAD만 체크아웃하면 아래 최신 구현을 재현할 수 없다.
- PC 이동 전 사용자 승인에 따라 변경을 커밋·푸시하거나 미커밋 파일을 함께 전달한다.
  신규 파일도 포함한다. 다른 PC에서는 실제 Git 상태와 이 기록을 먼저 대조한다.

## 완료한 최신 작업

1. 메인 화면을 자막·설정·기록·진단 탭으로 나눴다.
2. 음성 언어, 출력 장치, 자막 모양, 원문 표시와 부분 자막 옵션의 저장·복원을 추가했다.
3. 초기 사용 안내, 번역 설정 바로가기, 원인별 번역 오류 안내를 추가했다.
4. 설정 복원과 번역 연결 실패 복구의 실행 검증을 추가했다.
   실제 Avalonia 컨트롤, Rust IPC와 로컬 모의 HTTP 서버를 사용했다.
   별도 앱 프로세스로 설정 복원을 확인했다. 60개 검사 항목이 모두 통과했다.
   오류·복구 화면 4개도 확인했다.

관련 구현은 `apps/EchoSub.Desktop/MainWindow.*.cs`, `DesktopPreferences.cs`,
`UiCapture.cs`, `UiRecoveryProbe.cs`, `App.cs`에 있다.
[최신 검증 근거](evidence/ui-recovery-windows-20261004.md)를 읽는다.

## 재개할 작업

현재 진행 중인 실행이나 차단된 작업은 없다. 다음 항목은 미착수다.

1. UI의 키보드 이동 순서, 비활성화 사유 안내, 빈 기록 화면을 점검하고 개선한다.
2. 실제 모델 연결 상태의 메인 화면·오버레이를 확인한다.
3. 최신 자막 정책의 상태·렌더 회귀 검사와 30분 사용 검증을 수행한다.
4. 배포 준비 전에 저장소의 미커밋 변경을 논리 단위로 정리한다.

위 순서는 후속 작업 후보다. 새 사용자 지시가 있으면 우선 적용한다.
기존 음성 캡처 시작/백신 승인 이슈는 사용자가 보류했다.
사용자 요청 없이 그 조사로 돌아가지 않는다.

## 검증 상태와 한계

- Windows x64: 기본 Rust worker offline 빌드 통과.
- Windows 앱: 빌드 경고 0개, 오류 0개.
- 설정·연결 복구: 60개 항목 통과, 실패 0개, 19.0296초.
- 화면: 125% 배율에서 기본·최소 크기 및 오류·복구 화면을 확인했다.
- 이번 복구 검증은 실제 음성·모델 추론을 실행하지 않았다.
- 파일 권한 거절, 키보드 접근성, 30분 사용, 다른 배율과 macOS는 미검증이다.
- Avalonia 렌더를 캡처했다. 운영체제 화면 합성 결과는 아니다.

## 다른 PC의 환경 준비

저장소 루트를 현재 작업 폴더로 사용한다. 기존 PC의 `J:` 경로를 복사하지 않는다.

- 기본 UI/모의 검증: Windows, Rust/Cargo, .NET 10 SDK가 필요하다.
  새 PC는 먼저 의존성을 복원한다. `--offline`과 `--no-restore`는 캐시 준비 후 사용한다.
- 실제 추론: MSVC, CMake, LLVM과 선택한 backend의 CUDA 도구가 필요하다.
  [실행 안내](LIVE_UI.md)와 [모델 선택](TRANSLATION_MODEL_SELECTION.md)을 따른다.
- 모델·런타임: `benchmarks/translation-research-models.json`,
  `translation-candidate-models.json`, `translation-prompt-profiles.json` 및 실행 안내의
  매니페스트를 확인한다. 새 다운로드 전 동의 규칙을 적용한다.
- `models/`, `logs/`, `benchmarks/results/`, 빌드 산출물과 패키지 캐시는 Git 제외다.
  필요한 원본 증거는 별도로 전달하거나 검증을 다시 실행한다.
- 개인 설정은 `%LOCALAPPDATA%/EchoSub/settings.json`이다. PC마다 별도다.
  이전 PC의 장치 ID는 새 PC에서 사용할 수 없을 수 있다. 비밀값은 전달 문서에 넣지 않는다.

## 저장소 위치와 무관한 복구 검증 명령

의존성 캐시가 준비된 Windows에서 실행한다. 개인 설정과 실제 모델은 사용하지 않는다.

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $PWD '.nuget/packages'
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
cargo build -p echosub-worker --locked --offline
dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore
$workerPath = Join-Path $PWD 'target/debug/echosub-worker.exe'
$reportPath = Join-Path $PWD 'benchmarks/results/ui-recovery/report.json'
dotnet run --project apps/EchoSub.Desktop --no-build --no-restore -- --ui-recovery-probe-worker $workerPath --ui-recovery-probe-report $reportPath
```

결과 JSON의 `passed`, 검사 항목, 실제 실행 환경을 확인한다.
재개 후 이 문서와 `STATUS.md`, AGENTS.md의 현재 작업 요약을 갱신한다.
