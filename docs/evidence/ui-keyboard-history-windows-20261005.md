# 앱 내부 키보드 경로와 기록 전환 검증 (2026-10-05)

## 결과

Windows x64에서 키보드·기록 검사 41개가 통과했다. 실패는 0개이며 2.9749초였다.
기존 설정·연결 복구 검사도 60개 모두 통과했다. 실행 시간은 19.4732초였다.
최종 앱 빌드는 경고 0개, 오류 0개였다.

| 영역 | 확인한 동작 |
| --- | --- |
| 메인 키보드 이동 | Tab 순서, 비활성 버튼 건너뛰기, Shift+Tab 역방향 이동 |
| 설정 입력 | 방향키로 자막 폭 변경, 폭·불투명도 사이 Tab 이동 |
| 접근성 이름 | 컨트롤의 AutomationPeer 이름 9개 확인 |
| 키보드 시작 | Enter로 세션 시작, 실제 Rust IPC를 거쳐 Running 도달 |
| 기록 생성 | 모의 원문·번역 도착 뒤 빈 안내를 숨기고 목록 표시 |
| 삭제 확인 | 실제 확인 창, 취소 초기 초점, Tab·Shift+Tab, Enter·Esc 취소 |
| 기록 삭제 | 취소는 기록 유지, 확인은 worker 기록 삭제 후 빈 안내 복귀 |

검증 전용 진입점에서 실제 창과 worker를 사용한다.
초점을 받은 컨트롤에 Avalonia `KeyDown`/`KeyUp` 라우팅 이벤트를 전달한다.
단순히 버튼의 클릭 처리 함수를 호출하는 키보드 검사가 아니다.
물리 키보드나 운영체제 입력 주입 경로를 검증한 것은 아니다.

## 함께 보강한 UI

삭제 확인 창은 취소를 기본 동작으로 지정하고 초기 초점을 취소에 둔다.
Esc도 취소한다. 기록 화면은 내부 UUID와 상태 코드 대신 시간·원문·번역을 표시한다.
저장 세션은 세션 번호와 PC 현지 시각으로 표시한다. 내부 선택 식별자는 유지한다.
기록의 빈 상태, 생성 상태, 삭제 뒤 상태를 렌더로 저장하고 직접 확인했다.

## 재실행

의존성 캐시와 기본 worker가 준비된 저장소 루트에서 실행한다.

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $PWD '.nuget/packages'
dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore
$workerPath = Join-Path $PWD 'target/debug/echosub-worker.exe'
$keyboardReport = Join-Path $PWD 'benchmarks/results/ui-keyboard/report.json'
dotnet run --project apps/EchoSub.Desktop --no-build --no-restore -- --ui-usability-probe-worker $workerPath --ui-usability-probe-report $keyboardReport
$recoveryReport = Join-Path $PWD 'benchmarks/results/ui-keyboard-recovery/report.json'
dotnet run --project apps/EchoSub.Desktop --no-build --no-restore -- --ui-recovery-probe-worker $workerPath --ui-recovery-probe-report $recoveryReport
```

이번 결과는 Git 제외 `benchmarks/results/ui-keyboard-complete-20261005/`와
`benchmarks/results/ui-keyboard-recovery-20261005/`에 보관한다.
검사별 결과·시간과 기록 화면 3개를 저장했다.

## 확인 범위

개인 설정과 실제 음성·모델은 사용하지 않았다.
화면 읽기 프로그램 자체, 운영체제의 키 입력 전달, 모든 탭·팝업의 전체 이동 순서,
다른 운영체제·배율과 30분 사용은 미검증이다.
화면은 운영체제 합성 캡처가 아닌 Avalonia 렌더다.
