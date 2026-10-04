# 실제 시간 앱 세션·연결 복구 검사 (Windows, 2026-10-03)

## 이번에 연결한 범위

`--caption-session-probe-worker`와 `--caption-session-probe-report`를 함께 지정하면
실제 MainWindow가 **세션 연결 검사 / 모의 입력** 제목으로 열린다. 검사에서 실제
버튼의 Click 처리를 호출하며 생산 operation gate, Rust NDJSON IPC, history refresh,
session/epoch guard, Deck, OverlayWindow와 DispatcherTimer를 사용한다.
가상 시각이나 수동 AdvanceReading 호출은 없다.

Worker는 `--mock-pipeline --mock-session-control`로만 실행한다. 전사·번역은
기존 mock 명령으로 주입하고, 모델 준비 및 캡처 Running 입력만 명시적으로
대체한다. 실제 WASAPI/ASR/HTTP 서버는 사용하지 않는다. 일반 실행에는 이 대체가
적용되지 않는다. 입력 주입 직후 직접 화면 갱신을 호출하지 않고 앱의 기존
event/poll 경로로 결과가 도착하는지 기다린다.

## 시나리오

1. 메인 창 연결과 오버레이 표시 → 세션 시작 → Worker history로 자막 수신.
2. 다음 번역이 메인 창에 도착했으나 읽기 보호로 대기하는 것을 확인한 후 Pause.
3. 같은 UUID의 새 epoch로 Resume → 새 자막 → 실제 타이머로 만료.
4. 대기 입력이 있는 상태에서 Stop → 새 UUID로 Start. 보존된 과거 history 제외.
5. 대기 입력이 있는 상태에서 소유 Worker를 종료 → 끊김 처리 → 버튼으로 재연결.
6. 교체 Worker에서 새 세션/자막 확인 → Worker 종료 버튼 → 읽기 기한 이후 빈 화면 유지.

## 결과

최종 빌드로 위 순서를 3회 반복해 모두 통과했다. 실행마다 이름이 서로 다른
확인 항목 34개이며, 그중 슬롯 위치 확인은 타이머 동작 중 반복한다.
원시 assertion 개수는 스케줄링에 따라 달라지므로 독립 사례 수로 해석하지 않는다.
각 실행의 9 PNG에 대해 49 픽셀/상태 검사를 수행했다(합계 27 PNG, 147 checks,
실패 0). 초기/대기 화면의 글자 픽셀이 같고 pause/만료/Worker 종료 뒤 본문 잉크가
없으며 새 세션·복구 자막과 두 슬롯 위치가 맞는지 확인했다.

`scripts/check.ps1` 통과: Rust formatting/tests/build, C# builds, 표시 검사
(presentation 41, lines 98, pipeline 43) 및 C#↔Rust IPC smoke.
일반 표시 정책 변경은 없고 명시적 진단 진입점과 렌더 검사 도구를 추가했다.

## 재실행과 기록

```powershell
$env:ECHOSUB_OFFLINE='1'
scripts/check.ps1
dotnet run --project apps/EchoSub.Desktop --no-build --no-restore -- --caption-session-probe-worker J:/MyProject/VortexSub/target/debug/echosub-worker.exe --caption-session-probe-report J:/MyProject/VortexSub/benchmarks/results/caption-session-realtime-final-round1-20261003/report.json
models/tabby/venv/Scripts/python.exe -X utf8 scripts/check-caption-session-render.py benchmarks/results/caption-session-realtime-final-round1-20261003/report.json
```

3회 결과: `benchmarks/results/caption-session-realtime-final-round{1,2,3}-20261003/`.
보고서는 OS, UTC 시작 시각, 실제 경과 초, Worker/앱 SHA-256을 포함한다.
전체 check 로그: `benchmarks/results/caption-session-realtime-validation-20261003.log`.
이미지·원시 결과는 Git 제외다.

## 남은 범위

실제 MainWindow/IPC/표시 타이머의 짧은 세션 검사다. 물리 화면의 연속 프레임,
native capture 정리, 실제 ASR/번역 부하, 모델 설정 실패 복원, 자연 음성·게임
30분, macOS는 확인하지 않았다. 전체 P3.2 완료나 깜빡임 전면 해소를 주장하지 않는다.
