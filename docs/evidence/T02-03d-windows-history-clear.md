# T02-03d 세션별 메모리 기록 삭제

2026-10-01, Windows build 26200/AMD64 16 logical CPU. HIS-001/002의 기록 관리
일부 구현이다. 사용자의 요청으로 Initialize timeout 원인 조사는 후속으로 보류했다.

## 변경

`history_clear` capability와 UUID `clear_history`를 추가했다. 안정된 Paused/Idle,
capture/VAD join·native/번역 작업 반환·빈 작업 큐를 요구한다. 선택한 세션의
record만 제거하고 버전/이벤트를 갱신한다. 현재 세션 metadata·다음 구간 ID와
다른 세션은 유지하며 삭제한 이전 session metadata는 회수한다.
UI는 저장과 같은 세션 목록에 삭제 버튼과 UTC/UUID/개수 확인 창을 제공한다.
확인 중 worker가 바뀌면 거부한다. 저장한 파일에 삭제 I/O를 수행하지 않는다.
[사용·삭제 계약](../HISTORY_EXPORT.md).

## 검증

- `scripts/check.ps1`, 오프라인 Cargo/로컬 NuGet: Rust **95개**, 포맷·workspace
  빌드, Desktop/ProtocolSmoke/TranslationProbe 빌드 및 C# IPC PASS. 경고/오류 0.
- Rust IPC: 실행 중/잘못된 UUID 거부, Paused 삭제·빈 현재 세션 반복 삭제,
  버전 변경/옛 snapshot 거부, 삭제 후 Resume의 같은 UUID/UTC·증가한 segment,
  이전 세션 삭제 후 다른 세션 보존·metadata 회수·history.changed 확인.
- C# IPC: typed history가 비워짐, 저장한 SRT 내용 유지, 삭제한 이전 UUID의
  export 거부를 확인했다. mock translation은 비동기 owner가 없으므로 Pause
  취소를 즉시 반환 처리해 예약이 남지 않게 했다. 실제 native 예약은 기존대로
  반환까지 유지한다.
- 첫 저장소 검사에서는 C# export smoke의 폴더 정리가 `DirectoryNotEmpty`로
  실패했다. 명령 대기가 끝나도 worker 작업이 남을 수 있어 소유 worker를 종료한
  뒤 파일/폴더를 정리하도록 수정했고, 원래 예외를 정리 오류로 가리지 않게 했다.
  당시 앞선 오류는 확정하지 못했다. 이후 직접 C# 재실행과 최종 전체 검사는 PASS.
- native CPU 빌드 및 `scripts/probe-worker-live-asr.ps1 -NoBuild -Offline -Sessions`
  한 번 실행 PASS. Final 3개·Pause/Resume·Stop·두 세션 export 후 이전 세션 기록
  삭제, 새 세션 기록과 두 저장 파일 보존을 확인했다.

Git 제외 보고서: `benchmarks/results/worker-live-asr-cpu-20261001-043744/report.json`.
Pause 응답 0.000976초, Stop 응답 0.000239초, Stop부터 Idle 0.532673초.

## 미검증

실제 UI 삭제/취소/창 닫기 조작·화면 렌더링·macOS는 미검증이다. 이번 성공으로
시작 안정성·품질 gate를 변경하지 않는다. full 제품 wire, 시작 정책의 retain/
export/clear 선택, apply_config, partial/overlap 정합·번역도 남아 있다.
