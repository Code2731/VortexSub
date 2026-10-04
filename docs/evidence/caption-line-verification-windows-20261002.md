# 두 줄 표시 검증 — Windows, 2026-10-02

## 실행 범위

Windows 개발 PC에서 생산 표시 상태 클래스와 실제 Avalonia `OverlayWindow`를
사용했다. 입력은 합성 텍스트, 읽기 시간은 주입한 가상 시계다. 음성 캡처나 모델
추론 성능 검사가 아니며 GPU 성능 수치도 산출하지 않았다.

## 발견하고 수정한 문제

* source guard 진단 → 전체 Clear, 번역 대기 → Clear 연결을 제거했다.
* 처음 렌더 이미지를 검토하자 두 번째 줄이 `신` 한 글자에서 멈춰 있었다.
  완성된 줄은 고정하고 작성 중인 마지막 줄은 prefix 보존 시 suffix를 추가하도록 수정했다.
* TextBlock 전체 문자열 교체 대신 기존 glyph Run 뒤에 새 Run을 추가한다.
  숨겨진 원문 대입과 입력 가설 변경에 따른 스크롤 초기화도 피한다.
* 교정/새 epoch의 초기 스냅샷은 UI 배치 전에 저장돼 빈 이미지였다.
  fixture가 배치를 기다린 뒤 저장하도록 고치고 이미지와 픽셀을 다시 확인했다.

## 명령과 결과

```powershell
dotnet run --project tests/EchoSub.ProtocolSmoke --no-restore -- --lines-only
dotnet run --project apps/EchoSub.Desktop --no-restore -- --caption-line-probe-report J:/MyProject/VortexSub/benchmarks/results/caption-line-render-20261002/final-report.json
$env:ECHOSUB_OFFLINE='1'; & scripts/check.ps1
```

* CaptionLines: 52개 검사 통과. 40회 글자 추가, 대기/만료, 교정, 최신 대기 입력,
  epoch/일시정지/중지 후 재등장 방지를 포함한다.
* CaptionPresentation: 41개 검사 통과. 단일 입력 카드 정책과 교체 제한을 반영했다.
* 실제 오버레이 fixture: 41개 검사 통과, PNG 6장 저장. 상/하 위치 유지와
  prefix 추가, 대기, 읽기 후 교정, 새 epoch/중지 처리를 확인했다.
* 저장된 PNG 픽셀 검사: 5개 통과. 추가 전후 첫 줄 영역 동일, 교정 대기 중 본문
  동일, 교정/새 epoch 글자 실제 렌더링, 교정 전후 픽셀 차이를 확인했다.
* 전체 check 통과: Rust formatting/tests/build, Desktop/ProtocolSmoke/TranslationProbe
  빌드(각 경고/오류 0), C#↔Rust IPC smoke. 원래 모델 카탈로그 실패 테스트는
  미연결 상태에서 번역 job 실패를 기다렸다. 현재의 검증 후 연결 정책에 맞춰
  원문 Final 유지·번역 None·HTTP 미발행·연결 실패 상태·재연결 후 번역 Done을 검사했다.

전체 로그와 JSON/PNG는 Git 제외 `benchmarks/results/caption-line-render-20261002/`에 있다.
`final-report.json`, `pixel-report.json`, `full-check.log`가 최종 증거다.

## 판단의 한계

PNG는 Avalonia RenderTargetBitmap 결과다. Windows 화면 합성기의 연속 프레임
캡처는 아니다. 컴퓨터 사용 도구의 창 목록에 투명 ToolWindow 오버레이가 노출되지 않아
직접 화면 캡처 증거는 확보하지 못했다. 모든 GPU 깜빡임 해결을 주장하지 않는다.
실제 음성/게임 동시 실행의 30분 읽기, 번역 의미 품질, macOS는 미실행이다.
수정된 prefix는 읽기 보호 시간 동안 남을 수 있고 최신 입력 하나만 대기하므로
빠른 발화의 중간 단위 생략 가능성은 유지된다.
