# T01-02b: 유한 worker 전달·history snapshot

## 구현 범위

worker는 `pipeline-core`를 소유하며 NDJSON 응답·event와 버전 history를
전달한다. C# `WorkerClient`는 두 메시지를 분리하고 typed history를 읽는다.
실제 캡처·Silero·Whisper·HTTP·UI history 렌더링은 연결하지 않았다.
기본 hello의 `system_audio/asr/translation`은 계속 false다.
`events/history_snapshot`은 true이며 기본 기록은 비어 있다.

실행 인수 `--mock-pipeline`에서만 진단용 `mock_segment/mock_translate/mock_burst`를
허용한다. 실제 시스템 음성이 아닌 생성 PCM·입력 텍스트를 쓰며
`implementation=mock`, `mock_pipeline=true`로 표시한다.
일반 앱 실행은 이 인수를 넘기지 않는다.

## worker 소유권과 상한

stdin reader, 단일 상태 owner, stdout writer를 분리했다. callback에 이 큐를
사용하지 않는다. owner는 pipe 쓰기를 기다리지 않으며 0.020초 간격으로
번역 deadline과 출력 상태를 확인한다.

| 자원 | 상한 / 정책 |
|---|---|
| 수신 command 대기 | 최대 32개, 초과 시 stdin reader에 backpressure |
| 제어/오류 response 예약 | 최대 32개; event보다 먼저 쓰기 |
| worker event | 최대 256개; 별도 큐 |
| NDJSON 한 줄 | 양방향 최대 256 KiB UTF-8 payload, newline 제외 |
| C# 요청 | 기존 최대 32개 동시 요청 |
| C# event 수신 | 최대 256개; stdout drain은 UI 소비를 기다리지 않음 |
| IPC history page | 최대 4개 record; 전체 history는 기존 1,000개 |

event는 publish마다 증가하는 `seq`를 가진다. metrics·partial처럼 명시적으로
같은 coalescing key를 준 event만 교체할 수 있다. 최신 event를 뒤에 넣어
전달 순서를 유지하고 생략된 seq는 복구 신호로 남긴다. 현재 mock 경로는
`source.final`, `translation.updated`, `history.changed`를 전달한다.

event 큐가 가득 차면 전달 불가능한 backlog를 비우고 새 seq의
`snapshot.required`를 예약한다. 개별 확정 event 전달이 보장되는 구조가
아니다. 확정 원문·실패·skip의 권위는 worker store에 있으며 클라이언트는
snapshot으로 복구한다. response 예약이 가득 차면 owner는 새 command
처리를 기다리면서 deadline/출력 감시를 계속한다.

한 줄 byte 상한이 최악의 큐 payload 상한도 제한한다. worker event 약 64 MiB,
response/command 각각 약 8 MiB까지 가능하며 객체·OS pipe·현재 처리 중인
메시지는 별도다. 현재 mock event는 이 최대 크기보다 작다. native/model과
전체 앱 메모리 soak를 검증한 것은 아니다.

stdout writer는 성공한 write마다 진행 시각을 갱신한다. 진행 없는 쓰기가
5초 이상이면 owner가 코어를 interruption 상태로 만들고 worker가 오류 종료한다.
일시적인 pipe 지연은 읽기가 재개되면 복구한다. **실제 캡처 안전 종료는
아직 미검증**이며 native 어댑터를 연결할 때 이 경로에 캡처 중단·수명 정리를
연결해야 한다. 미래 native 작업의 종료를 현재 mock 프로세스 시험으로 대체하지 않는다.

## IPC 계약

기존 v1 command/response에 event envelope를 추가했다. schema는
`schemas/worker-protocol-v1.schema.json`이다.

```json
{"v":1,"kind":"event","seq":1,"event":"source.final","payload":{"history_version":2,"record":{}}}
```

위 record는 envelope 설명용 축약이다. 실제 record는 전체 결과 키,
`applied_source_revision`, PCM sample 범위와 초 단위 범위, 원문/번역 상태와
사유·텍스트·translation request ID를 포함한다. PCM 자체는 전송하지 않는다.

`get_history` params: `offset`(기본 0), `limit`(1~4, 기본 4),
`expected_version`(첫 페이지에서는 생략 가능). 응답은 `history_version`,
`records`, `next_offset`(마지막은 null), `last_seq`, `implementation`이다.
페이지 사이 변경은 `STALE_SNAPSHOT`이며 처음부터 다시 읽어야 한다.
원문과 번역 각각 4,096 UTF-8 bytes가 control 문자로 최대 6배 escape되어도
4개 record는 256 KiB 내에 들어간다. 더 큰 limit은 거부한다.

진단용 번역 입력도 `session_id/epoch/segment_id/source_revision`과
`translation_request_id`를 함께 검증한다. 틀린 키는 `STALE_RESULT`이며
현재 작업을 완료시키거나 원문/번역을 변경하지 않는다.

## C# 소비·복구 계약

`WorkerClient.Events.TryRead`로 UI가 event를 소비한다. reader는 UI handler를
직접 호출하지 않는다. seq 누락, `snapshot.required/history.changed`, 또는
로컬 256개 큐 고갈은 `Events.SnapshotRequired`를 켠다.
reader는 크기가 제한된 byte line을 읽어 response를 계속 완료시킨다.

`ReadHistoryAsync`는 같은 버전으로 모든 페이지를 읽는다. stale이면 부분
결과를 버리고 최대 3회 시도하며 계속 갱신되면 `SNAPSHOT_BUSY`를 반환한다.
반환된 `HistorySnapshot`은 caller 소유 typed record 복사다. UI는 이 결과로
기록을 교체하고 이후 event의 전체 키/버전을 확인해야 한다.

읽기가 끝나면 `last_seq`까지의 오래된 수신 event를 폐기한다. 응답 예약이
event보다 먼저 전송되어 snapshot 뒤에 도착한 오래된 event도 무시한다.
snapshot보다 새 event는 유지하며 더 최근에 발생한 누락을 이전 snapshot으로
해제하지 않는다. 실제 UI Dispatcher에서의 렌더링·재시도/표시 정책은 후속이다.

## 검증 결과

2026-09-30 Windows x64, Rust 1.90.0/.NET 10.0.102.
`ECHOSUB_OFFLINE=1`과 로컬 NuGet source로 `scripts/check.ps1`을 실행했다.
Rust 총 71개, fmt/workspace·C# 빌드와 확장 smoke PASS.

- 새 transport unit 5개: 256개 상한/복구 marker, coalescing seq 순서,
  response 32개 예약·우선순위, oversize 원자적 거부, stall/write 오류.
- 새 protocol 4개: 기본 empty history/opt-in 제한, Unicode source/번역·전체
  키·stale 페이지, 최악 escape 크기, stdout unread 상태의 약 5초 오류 종료.
- C# smoke: seq 누락·로컬 고갈·watermark 앞뒤 event 처리, typed Unicode/
  newline·초 단위 범위, 301개 다중 페이지 기록 복구와 event 중 ping 응답.
  기존 64요청·shutdown·worker 강제 종료 검증도 유지했다.

실제 장치/모델·UI history·Mac·게임 공존·장기 soak는 NOT_RUN이다.
다음은 M2의 실제 ASR/VAD 어댑터와 worker 처리 owner 연결이다. M0의 미완료
실기기 gate와 M1의 실제 VAD 모델 검증을 계속 별도로 추적한다.
