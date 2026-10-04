# 리뷰 개선 구현·검증 — Windows, 2026-10-03

## 구현 범위

[승인된 계획](../REVIEW_IMPROVEMENT_PLAN.md)의 R1~R7 구현과 자동 검증을 수행했다.
설정 실패 복구, 정정 길이 경계, VAD 계측, 자막 전달과 읽기 대기 정책을 변경했다.
현재 결과는 전체 번역 고도화 또는 v1.0 완료 판정이 아니다.

* 설정 적용 실패는 `CommitRejected`를 발행한다. pending을 폐기한다.
  기존 연결·모델·프로필·카탈로그를 유지한다. 처음 설정이면 Failed가 된다.
  실제 core reservation을 생성한 실패 주입에서 poll 생존과 재시도를 확인했다.
* 완결된 정정의 383/384/385바이트와 UTF-8 경계를 검사했다.
  `RepairTooLong` 뒤 final 원문·번역의 완료를 두 preview 모드에서 확인했다.
* VAD 처리 시작·완료를 구분했다. 기존 observed 필드는 완료 시각을 유지한다.
  push/poll 기간과 stale identity, 누락·역전 시각을 검사했다.
* 실시간 Deck는 최근 64개 세그먼트의 결과를 전달한다.
  UI의 기록 이벤트 버퍼 128개는 스냅샷 수락 후에도 유지된다.
  같은 단위는 합치고 서로 다른 단위는 4개·10초 읽기 대기열에 넣는다.
  오래된 revision, 원문 반박, 중지, 새 context를 처리한다.
* `caption_reading`과 기록 버퍼 초과 진단을 추가했다. 본문은 기록하지 않는다.
  모델 ID 상수와 매니페스트 일치 검사를 추가했다. Python 캐시를 Git에서 제외했다.

## 환경과 명령

Windows x64, OS `Microsoft Windows 10.0.26200`, .NET 10, Rust MSVC.
CUDA Toolkit 12.6, native target SM86. GPU에 새 추론을 실행하지 않았다.
기존 모델·런타임을 사용했다. 추가 다운로드는 없다.

```powershell
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
$env:ECHOSUB_OFFLINE='1'
$env:ECHOSUB_NUGET_SOURCE=Join-Path $PWD '.nuget/packages'
./scripts/check.ps1
./scripts/build-model-probe.ps1 -Backend cuda -Package echosub-worker -Vad -Offline
models/tabby/venv/Scripts/python.exe -X utf8 -m unittest discover -s tests -p test_caption_timing_summary.py
```

실제 창 검사는 `dotnet run --project apps/EchoSub.Desktop --no-build --no-restore --`
뒤에 아래 인자를 지정했다. 출력은 모두 Git 제외 `benchmarks/results/`에 저장했다.

* 줄 렌더: `--caption-line-probe-report <line-verified/report.json>`.
* 세션: `--caption-session-probe-worker <absolute target/debug/echosub-worker.exe>`
  `--caption-session-probe-report <session-verified/report.json>`.
* 지연 기록 연결: 세션 실행 전에 `ECHOSUB_CAPTION_TIMING_LOG`를
  `<session-verified/timing.jsonl>`로 설정했다. 기록 경로도 함께 검사했다.
* 기록 재생: `--caption-replay-input <paced-repair-v2-{base|small}-direction-20261003/report.json>`
  `--caption-replay-report <replay-{base|small}-verified/report.json>`.

저장 픽셀 검사는 `scripts/check-caption-render.py`,
`scripts/check-caption-session-render.py`, `scripts/check-caption-replay-render.py`로 수행했다.
각 도구에 해당 report의 경로를 전달했다.

## 결과

최종 결과 디렉터리: `benchmarks/results/review-improvements-20261003/`.

| 검사 | 결과 |
| --- | --- |
| 전체 check | PASS: Rust fmt/tests/build, C# builds, IPC. 최종 빌드 경고/오류 0 |
| Rust worker / protocol | 38 / 23 tests PASS |
| Pipeline unit / fixture | 3 / 41 tests PASS |
| Translation | 22 tests PASS |
| C# presentation / lines / pipeline / delivery | 41 / 99 / 44 / 21 assertions PASS |
| Timing summary | 9 Python tests PASS |
| CUDA/VAD release worker | BUILD PASS |
| 실제 줄 렌더 | 605 checks, 44 PNG, 픽셀 117 checks PASS |
| 실제 시간 세션 | 40종 확인 항목, 13 PNG, 픽셀 69 checks PASS |
| 지연 기록을 켠 세션 | 40종 확인 항목, 13 PNG, 픽셀 69 checks PASS |
| base/small 추론 기록 재생 | 12 runs, 상태 4351 checks, 176 PNG, 픽셀 953 checks PASS |

최종 세션의 원시 assertion 수는 117이다. 초기 실행은 117/119였다.
타이머의 반복 위치 검사 때문에 변한다.
서로 다른 확인 항목은 실행당 40종이다. 첫 문장·중간 문장·마지막 문장이 실제
타이머로 순서대로 표시됐다. `burst-middle.png`도 직접 확인했다.
글자 추가에서 기존 Inline 객체의 참조가 유지됐다.

지연 기록 세션은 FirstLine 8건, Queued 7건이다. 읽기 대기 중앙값은 0초다.
최종 실행의 최댓값은 5.0338968초다. 이 값은 연속 세 문장의 읽기 보호 대기다.
추론 지연이나 물리 화면 지연이 아니다. 정상 검사에서 과부하 누락은 0건이다.
기록 버퍼 초과, 로그 drop, invalid, bounded map eviction도 0건이다.
용량·시간 초과는 별도 상태 fixture에서 검사했다.
모의 세션에는 실제 모델·프로필 정보가 없다. 따라서 요약의
`session_epochs.quality_gate_passed`는 false다. 실제 번역 품질 통과로 해석하지 않는다.

최종 검사 앱 SHA-256: `4974e00dce1e5d9d4babec3fb18a3614a5f0aea47eee51e4ac6dac240aae4218`.
검사 mock worker SHA-256: `9f6a4008a30dae2668c4fb9e773b0e45a119f43f94a0d4688cd02eebf9094a19`.
CUDA/VAD worker SHA-256: `426614e9169cde33c487b8a8f2fa8f83f2a7915a323747808f7fb769f33fc7cd`.

첫 전체 검사에서 빈 카드 비교 회귀가 발생했다. 빈 전달 배열을 공유하여 수정했다.
초기 실행의 NuGet audit 네트워크 경고는 최종 local cache 검사와 구분한다.
초기 통과 로그는 `check-final.log`, native 빌드는 `native-build.log`에 있다.
후속 검토에서 읽기 대기 상한이 활성 정정을 건너뛰는 경로를 발견했다.
활성 정정을 먼저 표시하도록 수정했다. 정정 후 대기 문장 표시까지 검사했다.
해당 수정 뒤 전체 check는 `check-verified.log`로 다시 확인했다.

## 제한과 후속 검증

세션 검사는 실제 MainWindow, IPC, dispatcher, overlay를 사용했다.
ASR·번역·캡처 준비 입력은 모의 입력이다. 기록 재생은 기존 실제 추론 결과를
사용했으며 새 추론 실행은 아니다. PNG는 연속적인 물리 화면 검증이 아니다.

읽기 보호 때문에 대기시간이 늘 수 있다. 지속적인 과부하에서는 모든 내용을
표시하지 않는다. 늦게 도착한 과거 결과, 대기 상한, 원문 정정은 각각 다른 사유다.
worker 이벤트 유실과 history에 이미 덮인 부분 단위는 UI 버퍼만으로 복구하지 못한다.
이번 값으로 실제 음성부터 자막까지의 지연 개선을 주장하지 않는다.

자연 음성·게임 30분 사용, 물리 화면 연속 프레임, 새 VAD 시각의 실제 음성 검증,
실제 UI의 모델 설정 실패 복원, macOS는 남아 있다.
