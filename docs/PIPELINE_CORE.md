# T01-02: 결과 상태기계·유한 큐·history 버전

## 구현 범위

`crates/pipeline-core/`는 `audio-core`의 immutable PCM lease를 사용하는
단일 처리 스레드용 Rust 코어다. ASR/번역 작업 의도와 결과 적용, 기록의
권위를 담당한다. native decode·HTTP·UI·오디오 callback은 연결되지 않았다.
후속 [T01-02b](WORKER_DELIVERY.md)에서 mock worker IPC 전달을 연결했다. fixture는 생성 PCM과 mock 결과를 사용한다.
M1 전체 수용은 실제 VAD 모델과 전달 경로까지 연결한 뒤 별도로 판정한다.

## 작업과 결과의 키

ASR 키는 `session_id/epoch/segment_id/source_revision`이다. 요청마다 revision을
증가시키고 현재 키와 실제 in-flight 키가 모두 일치해야 결과를 적용한다.
동일 epoch에서 새 segment ID는 증가해야 하며 history eviction 뒤에도
재사용할 수 없다. 새 session ID도 이전보다 커야 한다.

이미 표시할 partial 원문은 새 요청이 대기하는 동안 유지한다.
`applied_source_revision`은 그 원문을 만든 revision이고 `key.source_revision`은
가장 최근 요청 revision이다. 두 값을 혼동해 이전 원문을 새 결과로 표시하지 않는다.
final이 예약되면 partial 적용을 닫고, final 적용 후 원문을 동결한다.
final 범위는 VAD의 실제 post-roll 때문에 이전 partial보다 짧아질 수 있다.

| 원문 상태 | 의미 |
|---|---|
| Partial | 갱신 가능한 발화; 표시 원문이 없을 수도 있음 |
| FinalPending | final PCM 예약 완료; partial 결과는 적용하지 않음 |
| Final | 확정 원문; 번역과 무관하게 보존 |
| Failed | final decode 실패·잘못된 텍스트 |
| Skipped | final 큐/PCM 확보 실패·decode 취소 |
| Discarded | VAD 폐기·Pause/Stop/epoch 변경 |

native adapter가 빈 결과/no-speech/반복/낮은 신뢰도를 판정하는 정책,
overlap 텍스트 정합과 prefix 안정화는 후속 ASR 통합 책임이다.
코어는 공백뿐인 결과·NUL·4,096 UTF-8 bytes 초과 결과를 거부한다.
실제 반복 문자열은 그대로 보존한다. 입력 String의 과도한 capacity는
수용 시 축소하며 원문/번역을 로그나 디스크에 저장하지 않는다.

## 큐·PCM·취소 소유권

| 자원 | 상한 / 정책 |
|---|---|
| ASR 실행 | 1개; 동일 context 병렬 decode 금지 |
| ASR final 대기 | 2개 FIFO; partial보다 먼저 dispatch |
| ASR partial 대기 | 최신 1개; 기존 lease 반납 후 새 PCM 확보 |
| 번역 실행 / 대기 | 실행 1개 / 대기 2개 FIFO |
| history | 최대 1,000개; 미결 상태를 보호하며 가장 오래된 terminal 기록부터 eviction |
| 원문·번역 | 각각 record당 최대 4,096 UTF-8 bytes |
| history page | 최대 100개 record; caller 소유 복사 |

ASR은 [기존 4슬롯 pool](AUDIO_CORE.md)을 재사용한다. 외부 clone이 남거나
ring에서 범위가 사라졌으면 snapshot 확보에 실패할 수 있다. final은
`Skipped`와 range·사유를 기록한다. partial 실패도 사유를 남기고 이전 표시
원문을 보존한다. history가 전부 미결이면 `HistoryFull`을 반환한다.
caller는 이 admission 실패도 gap/오류로 전달하고 무손실로 표시하지 않는다.

final 요청은 실행 중 partial의 협력 취소를 요청한다. Pause/Stop/epoch
변경은 실행 작업의 적용 권한을 없애고 대기 큐를 비운다. **취소 요청만으로
실행 슬롯을 비우지 않는다.** owner는 native full 반환 또는 소유 프로세스의
종료를 확인한 뒤 completion을 전달한다. 그 전에는 새 epoch라도 동일
context의 다음 작업을 dispatch하지 않는다. 실행 PCM lease도 그때까지
adapter가 보유한다. 오래 보관한 clone이나 새 pool의 반복 생성은 caller 책임이다.

history 텍스트 payload는 최대 8,192,000 bytes다. PCM은 기존 합 2,816,000 bytes,
번역 job은 확정 원문과 문맥 최대 2개를 복사한다. 객체·할당기·native 모델·
callback/event 큐·caller가 보유한 page 복사는 별도 예산이며 전체 앱 메모리
상한이나 soak 수용을 통과한 것은 아니다.

## 번역 terminal 계약

확정 원문만 번역한다. source==target은 `Bypassed`이며 요청을 dispatch하지
않는다. 문맥은 같은 session/epoch의 직전 확정 원문 최대 2개다.
번역 결과 적용에는 원문 키와 증가하는 `translation_request_id`를 함께 확인한다.
자동 언어 감지와 HTTP 연결/retry/warm-up은 이 코어에 포함하지 않는다.

deadline은 ASR final **결과 등록** 후 8초다. 단조 관측 ns로 비교하고,
진단 표시에서만 초로 변환한다. 이 owner의 관측 clock은 새 session에서도 되돌리지 않으며 새 오디오 sample 시간축과 구분한다. `poll`은 만료된 대기를 skip하고 실행 중
번역의 취소를 요청한다. caller는 주기적으로 poll해야 한다.
응답이 deadline 이후 도착해도 적용하지 않는다. 실제 반환 전에는 실행
슬롯을 유지한다. 만료 큐를 정리한 후 새 final 번역을 예약한다.

`Pending`은 성공 `Done`, 오류 `Failed`, 취소/초과/과부하 `Skipped`로 끝난다.
Pause/Stop은 `Interrupted`, epoch 변경은 `EpochChanged` 사유로 skip한다.
늦은 응답을 버려도 기존 pending이 남지 않으며 확정 원문은 보존한다.
재시작은 증가한 epoch, 새 Start는 증가한 session을 사용한다. 새 session의
history 유지/내보내기/삭제 선택은 UI에서 받은 뒤 `clear_history`로 전달한다.

## history snapshot 일관성

모든 record 변경과 eviction은 단조 `version`을 증가시킨다. caller는
처음 읽은 버전을 모든 페이지의 `expected_version`으로 전달한다.
중간에 번역·원문·종료·eviction이 갱신되면 `StaleSnapshot`으로 거부한다.
caller는 이전 페이지를 폐기하고 최신 버전의 첫 페이지부터 재시도한다.
반환된 페이지 자체는 복사이므로 이후 변경되지 않는다. 지속 갱신 중
재시도 횟수/화면 표시 정책과 `seq` 누락→snapshot 요청은 IPC/UI 통합에서 구현한다.

## 검증과 남은 연결

2026-09-30 Windows x64 / Rust 1.90.0:

```powershell
cargo test -p echosub-pipeline-core --offline
$env:ECHOSUB_OFFLINE='1'
$env:ECHOSUB_NUGET_SOURCE="$env:USERPROFILE/.nuget/packages"
.\scripts\check.ps1
```

새 fixture 23개 PASS. 기존 39개 포함 Rust 62개, fmt/workspace·C# 빌드와
C#↔Rust Unicode/64요청/worker 수명 smoke PASS. 확인한 범위:

- UT-004/006 subset: 최신 partial, final 동결, 전체 키·request ID 검증,
  Pause/새 session/epoch 뒤 늦은 결과 폐기, native 반환 전 다음 dispatch 금지.
- UT-007/008 subset: 언어 bypass, 모든 job 큐 상한, pool 4슬롯 교체,
  ring wrap/epoch reset 후 PCM 유지, 과부하·실패 기록, 번역 deadline과 terminal 상태.
- history subset: 페이지 사이 번역 갱신 시 버전 재시도, 1,200 final 후
  기록 1,000개 유지, eviction 뒤 ID 재사용 거부, Unicode/newline 보존.
- 생성 PCM→normalizer→VAD→mock ASR→mock 번역→history 연결.

실제 Whisper/번역 응답·Silero state/context·overlap 텍스트 dedup·native callback
queue·캡처 장치·Mac·장기 soak는 NOT_RUN이다. 후속 T01-02b의 유한 worker/C# event
queue·seq 복구·IPC DTO mock 검증은 [전달 결과](WORKER_DELIVERY.md)를 따른다.
실제 inference adapter 통합에서는 D-011의 base/partial-off 후보를 따른다.
