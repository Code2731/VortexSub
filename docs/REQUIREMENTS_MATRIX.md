# 요구사항·작업·검증 대응표

기준은 [제품 요구사항](02_REQUIREMENTS.md)과 [검증 목록](07_VALIDATION.md)이다. `PARTIAL`은 일부 코드/시험만 확인됐다는 뜻이다. 다음 시험 ID가 없는 셀은 작업 착수 전에 수용 시험을 작성한다. M0의 실제 순서는 [M0 실행 계획](M0_EXECUTION_PLAN.md)을 따른다.

| 요구 ID | 구현 작업 | 검증 ID·증거 | 현재 |
|---|---|---|---|
| AUD-001 | T00-02/03, T02-02/03 | HW-W01, HW-M01, HW-E2E; `evidence/T00-02-windows-10min.log` | PARTIAL: Windows 독립 loopback probe; worker·Mac 미연결 |
| AUD-002 | T00-02, T02-02 | HW-W02; 고정 ID/없는 ID probe | PARTIAL: 기본 추종 코드·고정 ID 확인, 실제 전환/분리 미실행 |
| AUD-003 | T00-03, T02-03 | HW-M01/02 | PLANNED |
| AUD-004 | T00-03, T02-03 | HW-M01/02 | PLANNED |
| AUD-005 | T01-01, T02-02/03 | UT-002, HW-W02, HW-M01 | PARTIAL: T01-01a sample 시간축·epoch reanchor/gap fixture 통과; 실제 native clock·장치/worker 미연결 |
| AUD-006 (P1) | 후속 별도 작업 | 후속 수용 시험 필요 | DEFERRED |
| ASR-001 | T00-04.1, T02-01/02/03 | P0-MODEL, HW-E2E | PARTIAL: Windows 합성 en/ko 실제 ASR probe; 시스템 전사·ja·Mac 미검증 |
| ASR-002 | T01-02, T02-01 | UT-004, IT-006 | PARTIAL: 최신 partial·final 우선/동결·취소 반환 대기 fixture 및 worker IPC 통과. Windows opt-in 연결; 실제 결과/한계는 T02-04a 근거 참조 |
| ASR-003 | T01-01, T02-01/04c | UT-003/005, IT-005/006 | PARTIAL: exact-zero PCM/VAD·NoSpeech skip·반복 보존 fixture 통과. T02-04c 600초 파일 무음 VAD/ASR/history 0 PASS; 실제 loopback/효과음/음악 수용은 후속 |
| ASR-004 | T01-02, T02-01/04c | UT-005, 긴 발화 fixture | PARTIAL: span/token 시간·UTF-8 정합 fixture PASS. 실제 8초 분할/0.608초 continuation 확인; 실제 token dedup/무누락 미검증 |
| ASR-005 (P1) | 후속 별도 작업 | 후속 수용 시험 필요 | DEFERRED |
| TR-001 | T00-04.1, T03-01a/b/c | P0-MODEL, IT-003 subset | PARTIAL: HTTP/worker fixture PASS; 실제 파일 Whisper 영어 3개→Qwen→history 및 한국어 bypass 확인. UI/제품 수용 후속 |
| TR-002 | T03-01/03 | UT-007, IT-003, HW-E2E, 품질 평가 | OPEN: 귀환 조건 오역·용어/시간 표현 문제 발견; T00-04.1 검토와 M3 재검증 연결 |
| TR-003 | T01-02, T03-01c | UT-006, IT-004 subset | PARTIAL: 원문 전체 키+request ID·HTTP worker 결과 적용, Pause/Resume epoch 뒤 applied=false·pending terminal/원문 보존 fixture PASS. UI 적용·live E2E 후속 |
| TR-004 | T03-01a/c | 문맥 경계·epoch 초기화 fixture | PARTIAL: 600 scalar 문맥/2,000 scalar 예산·원문 보존 및 실제 HTTP fixture에서 직전 확정 2개/새 epoch 문맥 초기화 PASS. 실제 모델 문맥 품질 미검증 |
| TR-005 (P0B) | T04-01/02 | REL-001, 관리형 번역 회귀 시험 필요 | PLANNED |
| TR-006 (P1) | 후속 별도 작업 | 후속 수용 시험 필요 | DEFERRED |
| UI-001 | T03-02 | REL-002, HW-E2E | PLANNED |
| UI-002 | T00-04.3, T03-02 | HW-UI01, HW-E2E; `evidence/T00-04.3-windows-overlay.json` | PARTIAL: Windows MOCK 창 속성·투명도 확인; 실제 게임/포커스·Mac 미검증 |
| UI-003 (P0B) | T04-03 | HW-UI01 | PLANNED |
| UI-004 | T00-04.3, T03-02 | HW-UI01; `OVERLAY_PROBE.md` | PARTIAL: MOCK 표시/숨김·조절·초기 위치; 물리 조작·다중 화면 검증 전 |
| UI-005 | T03-02 | 장애 상태 UI 검증 필요 | PLANNED |
| HIS-001 | T01-02, T03-02 | UT-008, 기록/페이지 버전 fixture | PARTIAL: 1,200 final→1,000 record 상한·skip/실패·페이지 사이 변경 재시도 검증; IPC mock snapshot·C# 301개 기록 복구 통과; UI history 렌더링 미연결 |
| HIS-002 | T02-03c/d, T03-02 | UT-009/010 subset; `evidence/T02-03c-windows-history-export.md`, `evidence/T02-03d-windows-history-clear.md` | PARTIAL: 원문 TXT/SRT·UTC/sample 시간축·명시적 덮어쓰기·세션별 삭제 IPC 및 실제 Windows 파일 보존 PASS. 번역 SRT·UI 조작·Mac 미검증 |
| MOD-001 | T00-04.1, T02-01 | P0-MODEL, 모델 경로/해시 오류 시험 필요 | PARTIAL: 고정 모델 3개 해시 확인과 실제 probe 로딩; 제품 설치·오류 수용 미검증 |
| MOD-002 (P0B) | T04-01 | REL-001, 손상/취소 시험 필요 | PLANNED |
| SEC-001 | T03-01a/b | UT-012 endpoint/redirect subset | PARTIAL: numeric loopback 주소·경로 제한과 실제 redirect 미추적 fixture PASS. no_proxy 명시; 환경 proxy 주입 검증·OS 비밀 저장소 후속 |
| SEC-002 | T03-01/02 | UT-011 | PLANNED |
| OPS-001 | T00-01, T00-04.2, T02-02/03 | IT-001/002, P0-CANCEL, HW-M02 | PARTIAL: Windows IPC·독립 native 정상/강제 종료 후 복구; worker 출력 stall 약 5초 오류 종료·bounded delivery 검증; 캡처/모델 통합·Mac 미검증 |
| OPS-002 (P0B) | T05-03 | REL-001, HW-M02 | PLANNED |
| NF-001 | T00-02/03, T01-01 | UT-008, 콜백 리뷰; Windows event-pull probe | PARTIAL: 즉시 패킷 해제·원본 미저장, 제품 큐/부하 검증 전 |
| NF-002 | T00-04.4, T01-01/02, T05-02 | P0-CONTENTION, UT-008, REL-002 | PARTIAL: T01-01a 12초 rolling·4슬롯 immutable PCM/고갈 fixture 통과; T01-02a 작업 큐·1,000 기록/텍스트 bytes 상한 fixture 통과; T01-02b worker event 256/응답 32·C# event 256 상한과 seq/snapshot 복구 통과; native callback 큐·전체 앱 soak 미검증 |
| NF-003 | T00-04.2, T02-01, T03-02 | P0-CANCEL, Stop 시간 측정 | PARTIAL: CPU/CUDA 실제 모델 취소·재시작 시간 확인; 캡처 Stop 0.5초·UI 반응성 미검증 |
| NF-004 | T00-04.2, T01-02, T02-02/03 | P0-CANCEL, UT-006, REL-002 | PARTIAL: 모델 수명·취소 출력과 PCM snapshot 키/session/epoch 분리 확인; T01-02a segment/revision/epoch/request 적용·취소 반환 대기 mock 검증; UI·장시간 통합 미검증 |
| NF-005 | T00-04.4, T02-01, T03-01 | P0-CONTENTION, IT-005, M5 부하 측정 | PARTIAL: RTX 3080에서 5조건 각 302초. base 동시 건너뜀 0, small 전사 19개. 제품 통합·게임·Mac·장기 부하 미검증 |
| NF-006 | T04-01/02, T05-03 | REL-001, 외부 요청 관찰 | PLANNED |
| NF-007 | T03-02 | 키보드/포커스/상태 접근성 검증 필요 | PLANNED |
| NF-008 | BASE-00, 모든 작업 | SDK/패키지/모델 해시·명령·결과 기록 | PARTIAL: Windows SDK·패키지·모델 revision/hash·실행 기록; Mac 미검증 |

P1 항목은 별도 요구와 시험을 정의하기 전 자동 착수하지 않는다. 모든 P0/P0B 행의 최종 PASS는 해당 OS 실기기 결과와 단계 gate에 따라 갱신한다. 문서상의 계획을 시험 PASS로 바꾸지 않는다.

