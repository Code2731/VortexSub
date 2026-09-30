# T02-03c UTC·원문 history export 확인

2026-10-01, Windows build 26200/AMD64 16 logical CPU. 기존 Whisper base CPU,
Silero v6.0·ORT CPU 자산 사용. 추가 다운로드 없음.

## 구현·검사

UUID session metadata에 시작 UTC를 추가하고 TXT/확정 원문 SRT export 및
UI 세션 선택·저장 버튼을 연결했다. 보존 범위는 전체 history 1,000개다.
정리 완료 후 안정된 snapshot을 저장하며 기존 숫자 wire는 유지한다.
[사용·시간·파일 계약](../HISTORY_EXPORT.md).

- `scripts/check.ps1` (`ECHOSUB_OFFLINE=1`, 로컬 NuGet source): Rust **94개**,
  포맷·workspace 빌드, Desktop/ProtocolSmoke/TranslationProbe 빌드와 C# IPC PASS.
  C# 빌드 경고/오류 0.
- IPC 검사는 UTC 유지, Unicode·CRLF, 실행 중 저장 거부, 잘못된 형식/경로,
  기존 파일 보존·명시적 교체·임시 파일 정리, 빈 세션, 이전 UUID 저장을 확인했다.
- Rust 단위 검사는 UTC 윤년/상한과 FinalPending 제외·1ms 미만 SRT 구간의
  양수 표시 시간을 확인했다.
- `scripts/build-model-probe.ps1 -Backend cpu -Package echosub-worker -Vad -Offline`:
  native CPU release 빌드 PASS.

## 실제 loopback

`scripts/probe-worker-live-asr.ps1 -NoBuild -Offline -Sessions` 한 번 실행: PASS.
합성 영어 fixture의 Final 3개, native 실행 관측 후 Pause/Resume, 오래된 결과
Discarded, Stop 정리, 새 UUID 및 이전 history 보존을 확인했다.
종료 후 새 세션 SRT와 이전 세션 TXT의 실제 파일 생성도 확인했다.

| 항목 | 시간(초) |
|---|---:|
| Pause 응답 | 0.000975 |
| Stop 응답 | 0.000163 |
| Stop부터 Idle 확인 | 0.500325 |

Git 제외 근거: `benchmarks/results/worker-live-asr-cpu-20261001-040928/`의
`report.json`, `current-session.srt`, `first-session.txt`.
새 세션 첫 cue는 `00:00:00,473 --> 00:00:02,266`이며 sample 상대 시간
0.473375..2.265375초에서 변환됐다. 이전 세션 TXT는 UTC와 Final/Discarded
4개 기록을 담았다. UTC 값의 날짜는 Z(UTC) 기준이므로 한국 날짜와 다를 수 있다.

## 한계

이번 실행에서 Initialize timeout은 없었지만 이전 실패는 해결되지 않았다.
[타임아웃 진단](T02-03c-windows-startup-diagnostics.md)을 따로 기록한다.
다른 시스템 음원은 격리되지 않았으며 실제 저장 창 클릭·원문 UI 렌더링,
자연 음성 품질·번역·partial·overlap 병합·장치 전환·soak·macOS는 미검증이다.
실제 파일 생성 성공을 UI 조작 수용으로 확대하지 않는다.
