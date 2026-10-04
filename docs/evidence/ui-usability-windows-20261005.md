# UI 조작 안내와 빈 기록 화면 (2026-10-05)

## 변경

- 자막 시작·번역 설정·기록 저장 제어의 사용 가능 조건을 화면에 표시한다.
  연결, 처리 중, 모델 미준비, 세션 상태, 번역 설정 변경 대기를 구분한다.
- 자막 기록이 없으면 시작 방법을 안내한다. 빈 목록 상자는 숨긴다.
  기록이 생기면 기존 목록을 표시한다.
- 각 탭의 입력과 버튼에 키보드 이동 순서를 지정했다.
  초기 안내, 탭 선택, 장치·언어, 주요 동작 순서로 접근하도록 구성했다.
- 장치, 언어, 자막 폭·불투명도, 번역 주소·모델·입력 방식과 저장 세션에
  접근성 이름을 추가했다. 시작·번역 적용·기록 저장 버튼에는 같은 안내를 툴팁으로 제공한다.

## 확인

Windows x64 앱 빌드는 경고 0개, 오류 0개로 통과했다.

```powershell
dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore
$workerPath = Join-Path $PWD 'target/debug/echosub-worker.exe'
$capturePath = Join-Path $PWD 'benchmarks/results/ui-usability'
dotnet run --project apps/EchoSub.Desktop --no-build --no-restore -- --ui-capture-worker $workerPath --ui-capture-dir $capturePath
```

모의 연결로 9개 화면 렌더를 저장했다. 메인, 빈 기록, 최소 크기 메인,
번역 연결 영역을 직접 확인했다. 최소 크기에서 주요 제어가 보인다.
번역 설정의 비활성화 사유는 입력 위에 표시된다.
이번 결과는 Git 제외 `benchmarks/results/ui-usability-final-20261005/`에 있다.

## 확인 범위

이 라운드에서는 자동 테스트를 추가하거나 실행하지 않았다.
실제 Tab/Shift+Tab 이동, 화면 읽기 프로그램, 비어 있지 않은 기록으로의 전환,
실제 음성·번역 모델은 실행 검증하지 않았다.
키보드 순서는 코드에 지정했으며 실제 동작 검증은 남아 있다.
화면은 Avalonia 렌더이며 운영체제 합성 화면은 아니다.
