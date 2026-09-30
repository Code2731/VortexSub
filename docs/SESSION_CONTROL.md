# Windows UUID session 제어 어댑터 (T02-03a)

## 실행과 명령

`scripts/run.ps1 -Live -Offline`은 기존 CPU 자산으로 worker를 빌드하고
`--session-control`을 켠다. 일반 실행은 MOCK이다. 최초에는 모델 Ready를
기다려 출력 장치·언어를 선택한다. **세션 시작 → 일시정지 → 재개 → 세션 종료**를
사용한다. 설정은 세션 시작 시 고정하며 변경하려면 종료 후 새 세션을 만든다.
기존 `-NoBuild` worker에는 capability가 없을 수 있으므로 한 번 다시 빌드한다.

```json
{"config":{"source_language":"en","device_id":null},"history_policy":"retain"}
```

위 params로 `start_session`을 요청한다. worker가 UUID와 Preparing을 반환하며
WASAPI Running과 live VAD 준비 완료 후 `session.state`가 Running이 된다.
`pause_session`, `resume_session`, `stop_session`에는 반환된 `session_id`를 보낸다.
다른 UUID는 `STALE_SESSION`, 잘못된 전이는 `INVALID_STATE`다.
이 모드에서는 진단 `start_capture`/`stop_capture`로 우회할 수 없다.

## 상태·소유권

- Pause는 입력을 중단하고 epoch를 올려 미확정 발화·대기 작업·늦은 native
  결과를 무효화한다. 이미 확정된 history는 유지한다.
- Paused 뒤에도 capture/VAD join과 native 반환은 진행될 수 있다.
  Resume은 capture/VAD join 뒤 허용한다. 이전 native full이 남아 있으면
  동일 PCM pool과 단일 inference 예약이 반환 때까지 유지된다.
- Stop은 Stopping을 반환한다. capture/VAD join과 native full 반환이 모두
  확인되어야 Idle이 된다. 취소 응답과 실제 정리 시간은 구분한다.
- 시작/실행 오류는 Error다. Stop으로 정리를 요청하고 Idle을 기다리거나
  Worker 종료·재연결을 사용한다. 시작의 기존 10초 deadline은 native 반환
  보장이 아니며 간헐적 Initialize 문제는 남아 있다.
- 새 세션은 새 UUID·증가하는 내부 숫자 ID·새 ring을 사용한다. segment ID는
  세션 내 Pause/Resume 때 초기화하지 않는다. history는 최대 1,000개 유지하며
  시작 정책은 명시적 `retain`만 지원한다. 원문 export와 안정된 Paused/Idle의
  세션별 `clear_history`를 제공한다. [저장·삭제 계약](HISTORY_EXPORT.md).

## 제품 목표와 현재 wire 형식

UUID는 제어와 `get_state.session`/`session.state`에서 사용한다.
`internal_session_id`는 기존 u64 audio/history ID와의 대응이다. T02-03b부터
source record와 history에 `product_session_id` UUID 및 `session_audio_start_s`/
`session_audio_end_s`를 추가한다. UI는 UUID와 내부 ID/epoch/revision, Running을
모두 확인해 표시하고 history에는 세션 시작 기준 초를 표시한다.
기존 numeric `session_id`, worker 기준 `audio_*_s`, `get_history` 페이지 및
event/payload 형식은 호환을 위해 유지한다. 진단 모드에는 새 필드를 넣지 않는다.
UUID 대응 정보는 history에 남은 세션(최대 1,000개)과 현재 세션만 유지한다.
원문이 없는 세션을 반복 생성해도 보존된 record의 UUID를 잃지 않는다.
`audio_origin_s`는 세션 시작 위치다. 새 상대 시간은 시작 sample index를 빼서
구하며 Pause 구간을 포함한다. T02-03c는 시작 UTC와 남은 history의 TXT/확정 원문 SRT
내보내기를 제공한다. [저장·시간 계약](HISTORY_EXPORT.md).
세션 경과 초는 Pause를 포함하고 Idle에서 고정된다.

제품 목표의 `session_id` UUID/type-data 형식으로의 전면 전환,
apply_config, 시작 시 history 정책 선택, Recovering, 번역·macOS는 미구현이다. 부분 전사는 `config.partial_enabled`로 선택하며 기본값은 false다. [계약](WORKER_PARTIAL_ASR.md).
이 어댑터를 제품 명세 전체의 완료로 간주하지 않는다.

## 확인 범위

`scripts/check.ps1`의 Rust IPC와 C# smoke에 명시적
`--mock-pipeline --mock-session-control` 시나리오를 추가했다. UUID 생성,
잘못된 설정/상태/오래된 UUID 거부, Pause epoch, Resume segment 증가,
Stop Idle, 새 UUID와 history 유지를 확인한다. 이 모드는 실제 audio가 없다.

`scripts/probe-worker-live-asr.ps1 -Sessions -Offline`은 기존 합성 영어 음원을
실제 loopback으로 재생해 Pause/Resume·추론 중 Stop·새 세션을 확인한다.
`-NoBuild`는 준비된 native CPU worker를 사용한다. 생성 WAV·원문 포함 보고서는
Git에서 제외된 results 아래에만 저장한다. 다른 시스템 음원은 격리되지 않는다.
실측과 미실행 항목은 [Windows 근거](evidence/T02-03a-windows-session-control.md)를 따른다.

T02-03b는 native full 관측 중 Pause를 요청하고 재개 후 해당 record가 Discarded로
남는지 확인한다. Resume 요청 직전 decode 예약 존재 여부를 별도로 기록한다.
UUID/상대 시간과 빈 세션 1,001회 뒤 기존 UUID 유지도 IPC 검증에 포함한다.
[추가 실행의 성공·시작 실패](evidence/T02-03b-windows-session-history.md)를 함께 따른다.
