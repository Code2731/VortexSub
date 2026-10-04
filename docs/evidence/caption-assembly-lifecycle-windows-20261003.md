# 표시 조립기의 대기 취소·상태 전환 검증 (Windows, 2026-10-03)

## 재현과 수정

표시 단위 A를 읽는 동안 단위 B가 대기하다가 다시 A의 정정 결과가 도착하면,
`CaptionLines.changeUnit`이 B의 대기 상태를 유지했다. 읽기 보호 종료 후 소비한
접두사를 초기화하여 동일한 앞 문장까지 재표시했다.

실제 Avalonia `OverlayWindow` 진단에서 수정 전 실패를 확인했다.
`benchmarks/results/caption-lifecycle-before-20261003/report.json`의
`returned unit clears obsolete unit transition and reuses completed clause`가 실패했다.
매 입력에서 **표시 중인 단위와 최신 입력**을 비교해 전환 상태를 다시 계산한다.
null/빈 입력은 접두사 대기 상태도 초기화한다. 두 줄 읽기 보호·최신 입력 하나·
기존 정정 정책은 유지한다.

## 검사 범위와 결과

- 줄 상태: 80→98 assertions. 단위 복귀, 동일 결과 복귀, 연속 정정,
  정정 철회, null/대기/빈 입력, product/session/epoch 전환, 종료를 추가했다.
- 생산 Deck→Lines: 34→43 assertions. HistoryRecord 형식으로 접미 단위에서
  전체 preview로 복귀하고 이전 context 기록이 지연 도착하는 경우를 검사했다.
- 실제 Avalonia 컨트롤: 581 checks, 44 PNG. 기존 반복 검사에 단위 복귀,
  연속 정정, 취소, 정정 대기 중 종료를 추가했다.
- 픽셀: 117 checks, 실패 0. 대기 전후 두 슬롯의 픽셀 동일성을 비교했다.
  `18-recovery-corrected.png`의 오른쪽 방향, `21-rapid-corrected.png`의
  최종 부정 문장을 직접 확인했다. 동일 앞 문장과 중간 후보는 재표시되지 않았다.
- `scripts/check.ps1` 통과: Rust formatting/tests/build, C# builds,
  표시 상태와 C#↔Rust IPC smoke. presentation 41, lines 98, pipeline 43.

## 재실행

```powershell
$env:ECHOSUB_OFFLINE='1'
scripts/check.ps1
dotnet run --project apps/EchoSub.Desktop --no-build --no-restore -- --caption-line-probe-report J:/MyProject/VortexSub/benchmarks/results/caption-lifecycle-render-20261003/report.json
models/tabby/venv/Scripts/python.exe -X utf8 scripts/check-caption-render.py benchmarks/results/caption-lifecycle-render-20261003/report.json
```

전체 로그: `benchmarks/results/caption-lifecycle-validation-20261003.log`.
생성 이미지와 전체 결과는 Git 제외다.

## 한계

Windows의 생산 표시 클래스·실제 컨트롤 렌더를 합성 텍스트와 가상 시계로 검사했다.
581개는 독립 음성 사례 수가 아니다. 이번에는 모델/음성 추론을 새로 실행하지 않았다.
live MainWindow/IPC의 실제 시간 연결, 물리 연속 프레임, 자연 음성·30분 사용,
모델 설정 실패 복원, macOS는 미검증이다. P3.2 전체 완료로 간주하지 않는다.
