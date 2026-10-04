# 연속 갱신과 긴 문장 표시 — Windows, 2026-10-02

## 재현 및 수정

생산 OverlayWindow/CaptionLines로 합성 입력을 렌더링했다. 첫 줄 뒤로 문자 하나씩
추가하는 모든 단계, 줄바꿈, 원문 표시 옵션, 420↔760 DIP 폭 변경, 긴 혼합 언어
본문의 읽기 만료/줄 전환을 확인했다. 가상 읽기 시간이며 실제 추론 검사는 아니다.

* 초기 fixture가 LF를 포함한 첫 줄에서 실패했다. FitLine은 줄바꿈을 소비하지만
  표시 조각에는 남겨 둬 TextBlock 안에 추가 줄이 생겼다. LF/CRLF/CR을 줄 이동
  신호로 소비하고 본문 끝에서는 제거했다. 선행/빈 줄도 슬롯을 차지하지 않는다.
* 이모지가 들어간 줄은 최소 높이 36 DIP를 넘어 38.4 DIP로 늘었다. 슬롯 높이를
  42 DIP로 고정하고 line height 36 DIP, clipping을 적용했다. 실제 배율의 픽셀
  반올림은 허용하되 반복 갱신 중 두 슬롯의 위치/크기는 동일해야 한다.
* 긴 문장 이미지에서 새 내용이 윗줄, 이전 내용이 아랫줄에 나타나는 순서 역전을
  확인했다. 아랫줄을 읽는 중에는 빈 윗줄을 채우지 않고, 읽기가 끝나면 위에서부터
  다시 채운다. 아랫줄을 움직이지 않는다. 읽기 보호에 따른 후속 대기는 추가된다.

## 실행 및 결과

```powershell
dotnet run --project tests/EchoSub.ProtocolSmoke --no-restore -- --lines-only
dotnet run --project apps/EchoSub.Desktop --no-restore -- --caption-line-probe-report J:/MyProject/VortexSub/benchmarks/results/caption-line-stream-20261002/final-report.json
models/tabby/venv/Scripts/python.exe -X utf8 scripts/check-caption-render.py benchmarks/results/caption-line-stream-20261002/final-report.json
$env:ECHOSUB_OFFLINE='1'; & scripts/check.ps1
```

* 생산 상태 클래스: 63개 검사 통과. LF/CRLF/CR의 초기/추가 입력, 빈 줄,
  긴 입력의 보류·순서·소진 후 재등장 방지까지 확장했다.
* 실제 Avalonia 렌더 fixture: 564개 검사 통과, PNG 29장. 25초 가상 시계에서
  두 줄의 위치/크기 유지, 순서 보호, 긴 본문 완전 재구성, grapheme 경계를 검사했다.
  한국어/일본어/결합 문자와 가족 이모지 포함 이미지를 직접 확인했다.
* 픽셀 검사: 81개 통과. 글자 추가 각 단계에서 첫 줄 전체 픽셀과 기존 아랫줄의
  gold glyph 영역이 동일했다. 각 프레임의 본문 유무와 실제 glyph 유무도 일치했다.
* 전체 check 통과: Rust formatting/tests/build, C# 세 프로젝트 빌드(경고/오류 0),
  CaptionPresentation 41개와 CaptionLines 63개 및 C#↔Rust IPC smoke.

증거는 Git 제외 `benchmarks/results/caption-line-stream-20261002/`의
`before-report.json`, `final-report.json`, `pixel-report.json`, PNG와 `full-check.log`다.
픽셀 검사는 Pillow를 사용하며 기존 승인된 Python 환경으로 실행했다. 새 다운로드는 없다.

## 한계

문자 추가 단계의 PNG 비교는 Windows 화면 합성기에서 매 프레임을 캡처한 결과가
아니다. GPU 깜빡임 완전 해결이나 물리 화면 지연을 주장하지 않는다. 임의 폰트/배율,
macOS, 실제 음성/게임 동시 실행 및 30분 읽기는 미확인이다. 창을 줄이는 중인 기존
줄은 재분할하지 않아 잘릴 수 있다. 본문 교정은 읽기 보호 시간 뒤 적용한다.
