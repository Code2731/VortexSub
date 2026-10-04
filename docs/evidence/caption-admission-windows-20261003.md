# 입력 접수와 읽기 보호 분리 — Windows, 2026-10-03

## 재현 및 변경

입력 카드가 아직 1.25초 교체 제한 안에 있을 때 다음 segment가 한 번만 전달되는
fixture가 실패했다. Deck가 다음 단위를 대기 상태로 저장하지 않고 기존 카드를
반환했다. 이후 Tick만 실행하면 새 내용은 접수되지 않았다.

실시간 앱은 `CaptionDeck.LineReadingManaged=true`로 입력 카드의 일반 교체 제한을
제거한다. 생성/연결 재초기화 양쪽에 적용했다. 새 단위와 본문 교정은 즉시
CaptionLines로 전달하며 실제 보이는 줄은 최소 읽기 시간을 지킨다. 빠른 입력은
최신 하나만 대기한다. CaptionPresentation의 식별자/원문 guard 검사는 유지한다.
독립 카드 비교 harness는 기본 모드의 기존 보호 정책을 유지한다.

마침표 옵션은 자신의 0.75초 기한 뒤에 일반 draft 제한을 추가하지 않는다.
0.74초에는 이전 입력, 0.75초에는 마침표 입력이 전달되는지 두 모드에서 검사했다.
이 시각은 가상 시계의 입력 채택 시각이며 실제 화면 표시 시각과 같지 않다.

## 명령과 결과

```powershell
dotnet run --project tests/EchoSub.ProtocolSmoke --no-restore -- --lines-only
dotnet run --project apps/EchoSub.Desktop --no-restore -- --caption-line-probe-report J:/MyProject/VortexSub/benchmarks/results/caption-admission-20261003/final-report.json
models/tabby/venv/Scripts/python.exe -X utf8 scripts/check-caption-render.py benchmarks/results/caption-admission-20261003/final-report.json
$env:ECHOSUB_OFFLINE='1'; & scripts/check.ps1
```

* Deck+Lines 연결 검사 26개 통과: 새 단위 즉시 채택/보이는 줄 유지, 이력 추가
  없이 읽기 후 표시, 최신 대기 하나, draft 교정의 중복 대기 제거/읽기 보호,
  마침표 옵션 경계값과 이전 만료·실패·세션 검사를 포함한다.
* 기존 CaptionPresentation 41개, CaptionLines 63개 통과.
* 실제 오버레이 렌더 fixture 571개, PNG 33장. 다음 문장을 65.1초에 한 번 입력하고
  추가 이력 갱신 없이 67.6초 가상 시계에서 표시했다. 기존 문장은 읽는 동안 유지됐다.
  전후 렌더 이미지를 직접 확인했다. 실시간 앱과 같은 줄 보호 모드를 사용했다.
* 저장 픽셀 검사 89개 통과. 전체 check의 Rust formatting/tests/build와 C# 세
  프로젝트 빌드(경고/오류 0), 상태/연결/IPC smoke도 통과했다.

전체 결과는 Git 제외 `benchmarks/results/caption-admission-20261003/`의
`final-report.json`, `pixel-report.json`, PNG, `full-check.log`에 있다. 새 다운로드는 없다.

## 해석 범위

합성 이력과 가상 시계의 검사다. 실제 ASR/HTTP 번역 성능, 게임 동시 실행/30분
읽기, macOS는 미확인이다. 읽기 보호 때문에 첫 입력이 즉시 화면에 나오는 것은
아니며, 최신 대기로 대체된 중간 내용은 생략될 수 있다. RenderTargetBitmap은
Windows 합성기의 연속 프레임이 아니므로 GPU 깜빡임 완전 해결을 주장하지 않는다.
