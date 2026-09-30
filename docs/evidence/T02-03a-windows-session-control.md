# T02-03a Windows session 제어 확인

날짜: 2026-10-01. Windows NT 10.0 build 26200, 기존 AMD64 16 logical CPU 환경.
Whisper base CPU·Silero v6.0·ORT CPU의 기존 동의 자산을 사용했다. 다운로드 없음.

## 실행 결과

- `scripts/check.ps1`: 오프라인 Cargo/로컬 NuGet. Rust **92개**, fmt/workspace build,
  Desktop·ProtocolSmoke·TranslationProbe 빌드 및 확장 C#↔Rust smoke PASS.
  C# 경고/오류 0. 신규 session 시나리오는 명시적 MOCK이며 실제 음원이 없다.
- `scripts/build-model-probe.ps1 -Backend cpu -Package echosub-worker -Vad -Offline`:
  native CPU release 빌드 PASS.
- `scripts/probe-worker-live-asr.ps1 -NoBuild -Offline -Sessions`: 실제 WASAPI
  loopback→Silero→Whisper PASS. 원본 보고서:
  `benchmarks/results/worker-live-asr-cpu-20261001-031600/report.json` (Git 제외).

첫 UUID 세션에서 final 1개, 같은 세션 Pause/Resume 후 final 1개, 새 UUID
세션에서 final 1개를 확인했다. Resume epoch/segment 증가와 이전 확정 history
유지를 확인했다. 실행 중인 native full을 관측한 뒤 Stop을 요청했다.

| 항목 | 관측 시간(초) |
|---|---:|
| Pause 응답 | 0.001010 |
| native 실행 중 Stop 응답 | 0.000244 |
| Stop 요청부터 native 반환·정리 후 Idle 확인 | 0.514054 |

이 값은 단일 실행의 제어/정리 관측이며 P95나 UI 표시 latency가 아니다.
Start/Resume Running은 capture와 VAD 준비 조건으로 확인했다. 새 세션은
새 내부 숫자 ID를 사용했으며 이전 UUID로 명령하면 STALE_SESSION이었다.
probe 종료 시 소유 WAV 재생을 중단하고 WorkerClient를 dispose했다.

## 수정 중 발견한 문제

첫 mock 검증은 실패했다. 기존 RollingAudio.reset은 같은 session의 epoch 변경만
허용했기 때문이다. 새 세션은 새 ring을 만들고 immutable PCM pool은 유지하도록
수정했다. 이후 최종 mock/실제 경로가 통과했다. 기존 결과를 PASS로 소급하지 않는다.

## 한계·후속

UI 버튼/원문 렌더링·게임 포커스·자연/일본어 음성·macOS·장치 전환·soak는
미검증이다. Pause는 final 확인 후 수행했으므로 native full 도중 Pause/즉시 Resume
실측은 후속이다. 현재 자료는 품질 gate를 통과시키지 않는다. 간헐적 Initialize
timeout 해결도 주장하지 않는다. UUID 제어 어댑터와 전체 제품 wire 계약은
[구분된 계약](../SESSION_CONTROL.md)을 따른다.
