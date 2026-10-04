# 메인 화면 정리 확인 — Windows (2026-10-04)

## 변경

- 자막 탭: 출력 장치, 음성 언어, 시작·중지, 오버레이 제어.
- 설정 탭: 자막 창 모양, 번역 연결, 고급 설정, 실험 기능.
- 기록 탭: 최근 자막, 기록 저장과 삭제.
- 진단 탭: 연결 제어, 상세 상태, 자막 지연 기록.
- 기존 컨트롤과 이벤트 처리를 유지했다. 언어 표시는 한국어로 바꿨다.
  전달하는 언어 코드는 기존 `en`, `ja`, `ko`를 유지한다.
- 시작 가능 상태에서만 준비 완료를 표시한다. 모의 연결은 별도로 표시한다.

## 확인 결과

Windows x64에서 앱 빌드는 경고 0개, 오류 0개였다.

```powershell
dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore
dotnet run --project apps/EchoSub.Desktop --no-build --no-restore -- --ui-capture-worker J:/MyProject/VortexSub/target/debug/echosub-worker.exe --ui-capture-dir J:/MyProject/VortexSub/benchmarks/results/ui-layout-final-20261004
```

실행한 Avalonia 창의 렌더를 PNG로 저장했다. 네 탭, 설정 하단,
최소 크기 자막·설정 화면을 포함한 7개 이미지를 직접 확인했다.
기본 크기는 820×760, 최소 크기는 720×700이며 화면 배율은 125%다.
주요 자막 제어는 두 크기에서 모두 보인다. 설정은 세로 스크롤로 접근한다.

결과는 Git 제외 `benchmarks/results/ui-layout-final-20261004/`에 있다.
`capture.json`에 각 화면의 크기, 배율, 탭, 스크롤 위치를 기록했다.

## 확인 범위

명시적 모의 worker를 사용했다. 캡처는 실제 앱 컨트롤의 Avalonia 렌더이며
운영체제 화면 합성 결과는 아니다. 실제 음성·번역 연결, 오버레이 동작,
키보드 접근성, 다른 화면 배율과 운영체제는 이번에 검증하지 않았다.
자동 회귀 테스트는 실행하지 않았다.

## 다음 작업

설정 저장과 복원, 초기 준비 안내, 번역 연결 오류 안내를 정리한다.
실제 연결 상태의 화면과 오버레이도 별도로 확인한다.
