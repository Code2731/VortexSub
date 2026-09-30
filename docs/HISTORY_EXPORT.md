# 세션 시작 UTC·원문 TXT/SRT 저장 — T02-03c

## 사용

`./run.cmd -Live -Offline`로 최신 worker를 빌드한다. 원문을 만든 뒤 **일시정지**
또는 **세션 종료**하고 capture/VAD join과 native 반환을 기다린다.
history 아래 목록에서 UTC·UUID로 세션을 선택하고 **TXT 저장** 또는
**원문 SRT 저장**을 누른다. 이전 세션도 history가 남아 있으면 선택할 수 있다.
저장 창을 취소하면 파일을 쓰지 않는다. 기존 파일 교체는 저장 창의 덮어쓰기
선택을 따른다. 창이 열린 동안 재개했다면 worker가 저장을 거부한다.

저장은 명시적인 요청에만 수행한다. worker 재연결/종료 전에 필요한 원문을
저장한다. 메모리 history는 전체 세션 합계 최대 1,000개이며 오래된 구간은
퇴출된다. export는 남은 기록만 저장하고 퇴출된 기록을 복원하지 않는다.

## 시간·형식

- `get_state.session.started_at_utc`와 record의 `session_started_at_utc`는
  시작 시 시스템 시계를 읽은 `yyyy-MM-ddTHH:mm:ss.fffZ` 값이다. Pause/Resume
  뒤에도 유지한다. 시스템 시계 변경은 이후 세션 UTC에 영향을 줄 수 있다.
- 구간 시간은 UTC/추론 완료 시각과 독립된 16 kHz sample 시간축이다.
  시작 sample을 빼며 초기 장치 준비 지연과 Pause 공백도 포함한다.
- TXT는 UUID·UTC·snapshot 버전·보존 개수와 원문 상태/사유·초 단위 구간을
  담는다. Discarded 같은 미확정 기록도 상태와 함께 남긴다.
- SRT는 적용 revision이 일치하는 **Final 원문**만 저장한다. 순번과
  `HH:MM:SS,mmm`을 쓰며 시작은 ms 내림, 끝은 ms 올림한다. 빈 줄을 제거해
  cue 구분을 유지한다. overlap 병합·번역 자막·누락 구간의 가짜 cue는 없다.
- 두 형식은 UTF-8(BOM 없음)이다. 동일 디렉터리의 새 임시 파일에 먼저 쓰고
  Windows에서는 `MoveFileExW`로 게시한다. `overwrite=false`는 기존 파일을
  교체하지 않는다. 실패 시 이전 파일을 유지하고 소유 임시 파일을 정리한다.

## IPC

`hello.capabilities.history_export=true`인 UUID 제어 모드에서 제공한다.

```json
{"session_id":"세션 UUID","format":"srt","path":"J:\\Exports\\source.srt","overwrite":false}
```

위 params를 `export_history`로 보낸다. 경로는 절대 경로이고 부모 디렉터리는
존재해야 한다. 응답에는 `cue_count`, `record_count`, `history_version`,
`time_basis="session_audio"`, `source_only=true`가 있다. 실행/정리 중은
`INVALID_STATE`, 퇴출된 세션은 `STALE_SESSION`, 기록/확정 원문이 없으면
`EMPTY_HISTORY`, 기존 파일 충돌은 `EXPORT_EXISTS`다.

파일 I/O는 안정된 Paused/Idle 제어 경로에서 수행한다. 네트워크 파일시스템의
지연·원자성, macOS 저장, 실제 저장 창 조작은 미검증이다.
[Windows 확인 근거](evidence/T02-03c-windows-history-export.md).

## 세션 기록 삭제 — T02-03d

일시정지/종료 후 정리가 끝나면 목록에서 세션을 선택하고 **선택 세션 기록 삭제**를
누른다. 확인 창에 UTC·UUID와 기록 개수를 표시한다. 취소하면 그대로 유지한다.
확인하면 선택한 세션의 메모리 기록만 삭제한다. 복구할 수 없으므로 필요한 기록은
먼저 저장한다. 이미 저장한 TXT/SRT 파일과 다른 세션의 기록은 유지한다.

`hello.capabilities.history_clear=true`일 때 `clear_history`에
`{"session_id":"세션 UUID"}`를 보낸다. 실행/추론/정리 중에는 `INVALID_STATE`다.
응답은 `removed_count`, `history_version`, `session_id`이며 실제 삭제 시
`history.changed`를 발행한다. 이전 snapshot은 `STALE_SNAPSHOT`으로 거부한다.
현재 세션은 UUID·UTC·오디오 원점·다음 segment ID를 유지해 재개할 수 있다.
빈 현재 세션을 다시 삭제하면 0개/동일 버전이다. 삭제한 이전 세션의 metadata는
회수하므로 이후 해당 UUID의 저장/삭제는 `STALE_SESSION`이다.
[확인 범위](evidence/T02-03d-windows-history-clear.md).
