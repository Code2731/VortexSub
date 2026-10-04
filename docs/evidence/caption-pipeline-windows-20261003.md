# 입력 카드와 오버레이 연결 검증 — Windows, 2026-10-03

## 발견과 수정

CaptionDeck/CaptionLines를 연결한 생산 클래스 검사에서 긴 번역의 뒷부분 누락을
재현했다. Deck는 입력 카드를 최대 10초 후 만료시키지만, 두 줄씩 읽는 데는 더
오래 걸릴 수 있다. null 카드가 미표시 suffix까지 삭제하는 것은 서로 다른 수명을
혼동한 동작이었다. 일반 만료는 받아 둔 최신 입력을 소진하도록 변경했다.
`CaptionCards.InputExpired`로 일반 만료를 명시한다. null 자체를 만료로 해석하지 않는다.
Stop/Pause/session/epoch 변경은 Clear로 남은 입력을 즉시 폐기한다.
번역 없는 카드가 전달되면 이전 추가 표시를 중지한다. 새 유효 단위는 이전 미표시
부분을 대체한다. 전체 전사 backlog를 만들지 않으므로 빠른 발화의 생략 가능성은 남는다.

또한 동일 단위에서 target prefix를 그대로 보존하는 추가도 Deck가 최대 1.25초
기다리게 했다. 뒤에만 붙이는 입력에는 이 교체 제한을 적용하지 않는다. source guard,
유효 식별자와 본문 교정의 기존 처리는 유지한다. 마침표 cosmetic 묶기 옵션도 유지한다.
추론 속도 개선이 아니라 UI 입력 단계의 불필요한 대기를 제거한 것이다.

## 실행과 결과

```powershell
dotnet run --project tests/EchoSub.ProtocolSmoke --no-restore -- --lines-only
dotnet run --project apps/EchoSub.Desktop --no-restore -- --caption-line-probe-report J:/MyProject/VortexSub/benchmarks/results/caption-pipeline-20261003/final-report.json
models/tabby/venv/Scripts/python.exe -X utf8 scripts/check-caption-render.py benchmarks/results/caption-pipeline-20261003/final-report.json
$env:ECHOSUB_OFFLINE='1'; & scripts/check.ps1
```

* 연결 검사 15개: 긴 입력의 만료 후 완전 소진/재등장 방지, 명시적인 일반 만료,
  인식 실패의 만료 오분류/미표시 suffix 출력 방지, 즉시 append, draft
  rewrite 제한, 최종 교정의 읽기 보호, stop, 새 segment의 보존/이전 tail 대체, epoch.
* 기존 상태 검사: CaptionPresentation 41개, CaptionLines 63개 통과.
* 실제 Avalonia 렌더 fixture 568개, PNG 31장. 긴 입력에도 Deck를 연결해 10초
  입력 만료 후 나머지를 누락·중복 없이 읽었다. 부분 추가→최종 교정→stop도 연결했다.
  추가/교정 이미지를 직접 확인했다.
* 저장 렌더 픽셀 검사 85개 통과. 모든 문자 추가 단계의 기존 glyph 유지와 각
  프레임의 텍스트/glyph 유무 일치를 확인했다.
* 전체 check 통과: Rust formatting/tests/build, C# 세 프로젝트 빌드(경고/오류 0),
  위 상태/연결 검사 및 C#↔Rust IPC smoke.

전체 결과는 Git 제외 `benchmarks/results/caption-pipeline-20261003/`의
`final-report.json`, `pixel-report.json`, PNG, `full-check.log`에 있다. 새 다운로드는 없다.

## 한계

합성 HistoryRecord와 가상 읽기 시계를 사용했다. 실제 ASR/HTTP 번역이나 사용자
영상의 품질/지연, 30분 실제 음성·게임 사용, macOS를 검사한 것은 아니다.
RenderTargetBitmap 결과는 Windows 화면 합성기의 연속 프레임이 아니므로 GPU
깜빡임 완전 해결을 주장하지 않는다. 읽기 보호와 최신 입력 대체의 지연/생략은 유지된다.
