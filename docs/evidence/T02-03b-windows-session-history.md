# T02-03b UUID history·native Pause 확인

2026-10-01, 기존 Windows build 26200/AMD64 16 logical CPU 환경.
기존 Whisper base CPU·Silero v6.0·ORT CPU 자산 사용, 다운로드 없음.

## 구현과 저장소 검사

UUID 제어 모드의 source/history에 `product_session_id`와 세션 시작 기준
`session_audio_start_s`/`session_audio_end_s`를 추가했다. 기존 숫자 session ID와
worker 시간 필드는 그대로다. C#은 UUID·유한 시간·양수 범위·세 필드의 동시
존재를 검증하고 UI는 현재 UUID까지 확인해 source를 표시한다.
`session_history_uuid` capability가 없는 이전 worker에는 재빌드를 안내한다.

session metadata는 유지된 history session과 현재 session으로 제한한다.
Rust IPC에서 빈 세션 1,001회 뒤 기존 record UUID가 유지됨을 확인했다.
source.final 이벤트와 다중 세션 history UUID, C# typed 상대 시간도 확인했다.

오프라인 Cargo/로컬 NuGet로 `scripts/check.ps1` 실행: Rust **92개**, 포맷/
workspace 빌드, Desktop·ProtocolSmoke·TranslationProbe 빌드와 확장 C# IPC PASS.
native CPU release 빌드 PASS. 이는 UI 화면/마우스 조작 시험이 아니다.

## 실제 실행: 실패를 포함해 보존

`scripts/probe-worker-live-asr.ps1 -NoBuild -Offline -Sessions`를 두 번 실행했다.
첫 실행은 시작의 InitializeAudioClient에서 **10.006663초** timeout이었다.
accepted PCM은 0초, capture owner는 아직 join 전이며 session은 Error였다.
Pause 단계에 도달하지 못했다. 기존 시작 안정성 gate는 유지한다.

Git 제외 보고서:

- 실패: `benchmarks/results/worker-live-asr-cpu-20261001-033150/report.json`
- 재실행 성공: `benchmarks/results/worker-live-asr-cpu-20261001-033246/report.json`

재실행은 실제 loopback 합성 영어 fixture의 final 3개를 확인했다.
native_running=true를 관측하고 FinalPending record를 식별한 뒤 Pause를 보냈다.
capture/VAD join 후 Resume 요청 직전 decoding=true였으며, 재개 후 해당 옛 record는
Discarded였다. 새 final의 epoch/segment가 증가하고 같은 UUID와 상대 시간이
유지됐다. 새 세션의 final에는 새 UUID가 붙고 이전 history UUID는 유지됐다.

| 항목 | 관측 시간(초) |
|---|---:|
| native 실행 관측 후 Pause 응답 | 0.001093 |
| native 실행 중 Stop 응답 | 0.000256 |
| Stop부터 native 정리 후 Idle 확인 | 0.479699 |

Resume 직전 decoding은 예약의 존재이며 native callback 실행 상태와 동일하지
않다. Resume의 worker 수락 순간에 native가 계속 실행 중이었는지를 단정하지
않는다. 기존 단일 flight·PCM pool의 소유권과 stale completion 거부를 유지한다.

## 미검증

시작 timeout 원인은 미해결이다. 두 번째 성공으로 첫 실패를 지우지 않는다.
다른 시스템 음원은 격리되지 않았고 자연/일본어 품질·화면 표시·게임 포커스·
30분 soak·장치 전환·macOS·내보내기는 미검증이다. 전체 제품 UUID wire 전환과
시작 UTC도 남아 있다. 원문 포함 전체 보고서와 생성 WAV는 Git에서 제외한다.
