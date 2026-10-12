# v1.0 필수 요구·수용 기준 대조

2026-10-12. 브랜치 `master`, HEAD `15f1db0`와 현재 미커밋 소스를 읽었다.
기준: [요구사항](02_REQUIREMENTS.md), [검증 기준](07_VALIDATION.md), [로드맵](V1_ROADMAP.md).
지원 범위와 기준 수치는 변경하지 않았다. Windows·Apple Silicon macOS를 함께 판정한다.

## 결론

개발 MVP와 제품 베타의 수용 조건은 아직 충족하지 않았다.
자막 상태·저장 화면 자동 검사는 확대됐지만 실제 자연 음성·양 OS·배포 검증은 남아 있다.
완료율은 계산하지 않는다. 하나의 요구에 코드·실기기·품질 조건이 함께 있기 때문이다.

대조 당시 확인한 구현 누락은 원문+번역 SRT, DPI/모니터 자동 복구, live 기본 장치 추종,
OS 비밀 저장소와 macOS 캡처 경로다. P0B의 앱 관리형 모델·런타임·설치 기능도 남아 있다.
후속으로 [HIS-002 Windows 구현·자동 검사](evidence/bilingual-srt-windows-20261012.md)를 완료했다.
후속으로 [UI-004 자동 위치 복구](evidence/overlay-placement-windows-20261013.md)의 Windows 구현·자동 검사를 완료했다.
다음은 SEC-002 OS 비밀 저장소·런처 인증 전달의 범위 정리다.
실제 모니터/DPI·OS 저장 창·macOS 수용은 남았다.
보류한 캡처 시작 원인 조사와 Laya 실험은 재개하지 않는다.

## 사용한 근거와 날짜

- 2026-10-12 [최신 자막·기록 재생](evidence/caption-native-recheck-windows-20261012.md):
  계약 84개 / 16,086 검사, 기록 12회 / 각 표시기 4,351 검사. 실제 새 추론은 없다.
- 2026-10-12 [혼합 수명 검사](evidence/caption-mixed-lifecycle-windows-20261012.md):
  두 경로 각각 가상 1,800초. 실제 30분 사용과 프로세스 메모리 측정은 아니다.
- 2026-10-05 [키보드·기록](evidence/ui-keyboard-history-windows-20261005.md): 앱 내부 41개.
- 2026-10-04 [설정·연결 복구](evidence/ui-recovery-windows-20261004.md): Windows 모의 연결 60개.
- 2026-09-30 [worker 캡처](evidence/T02-02a-windows-worker-capture.md): 짧은 PCM 수신·Stop 3회.
  최신 장치 전환·분리·10분 E2E 근거로 확대하지 않는다.
- 2026-10-06 [자연 음성 시도](evidence/natural-session-attempt-windows-20261006.md): 수신 음성 0초.
  [준비 절차](NATURAL_SESSION_VALIDATION.md)는 실제 30분 합격 결과가 아니다.

최초 대조 단계는 소스·근거의 정적 대조다. 해당 단계에서는 새 빌드·테스트·캡처·모델 실행을 하지 않았다.
이전 검증 결과를 이번 실행으로 표시하지 않는다. 화면 검사도 새로 실행하지 않았다.
요구 ID의 정적 대조는 35/35다. P0 23개·P0B 4개·비기능 8개를 포함했다.
문서의 상대 경로 링크도 모두 존재한다. 이것은 구현/수용 검사 통과 수가 아니다.

## P0 기능 23개

‘부분’은 구현 또는 제한된 검사 근거가 있다는 뜻이다. 해당 요구 전체의 합격은 아니다.

| ID | 현재 근거·구현 | 남은 수용 조건 또는 구현 |
| --- | --- | --- |
| AUD-001 | Windows WASAPI owner·짧은 PCM 수신 근거 | 양 OS 실제 시스템 음성, HW-W01/HW-M01/HW-E2E |
| AUD-002 | 장치 목록·ID 선택. `capture_runtime.rs`는 `pinned_until_restart` | live 기본 장치 추종 구현, 고정 장치 분리·자동 추종 실제 검사 |
| AUD-003 | macOS 시스템 믹스 경로 없음 | ScreenCaptureKit 위험 검증 후 bridge·worker 연결 |
| AUD-004 | macOS 권한 흐름 근거 없음 | 거부·허용·재시도와 signed app 권한 귀속 |
| AUD-005 | core 시간축·epoch·gap 계약, Windows 짧은 캡처 | 실제 Pause/gap/장치 변경의 시간축과 양 OS 검사 |
| ASR-001 | Whisper adapter·파일 추론·en/ja/ko 선택 경로 | 최신 설정의 en/ja/ko 자연 음원 평가·양 OS 확인 |
| ASR-002 | core revision·partial/final·worker 검사, 기록 재생 | 실제 장시간 발화·무음 경계·최종 누락 판정 |
| ASR-003 | VAD·비음성 fixture·억제 경로 | 지정 디지털 무음 10분, 실제 반복 발화 보존 |
| ASR-004 | window/reconciliation·분할 계약 | 지정 긴 발화 corpus와 실제 overlap 중복·누락 판정 |
| TR-001 | HTTP catalog·configure·실패 복구·전사 전용 경로 | 실제 서버 장애·재연결과 양 OS 수용 |
| TR-002 | 번역 owner·파일/통제 음성 결과, 실패 상태 분리 | en/ja→ko 각각 50개 의미 평가·실제 실패 시 전사 유지 |
| TR-003 | translation key·revision/epoch 검사, 혼합 수명 재생 | 실제 취소·지연 응답·세션/장치 변경 E2E |
| TR-004 | core 직전 원문 최대 2개, 번역 입력 문맥 600자 상한 | 실제 장치 변경의 문맥 초기화와 의미 품질 평가 |
| UI-001 | MainWindow 제어·worker 상태 연동·모의 전환 | 실제 capture/model 준비·Pause/Resume/Stop·100회 제어 |
| UI-002 | 원문 옵션·폭·글꼴·불투명도·이동, 저장 화면 | 실제 사용·옵션 복원·물리 연속 화면·macOS |
| UI-004 | 화면/DPI/크기 변경 자동 복구·기하 189개·창 알림/저장 화면 | 실제 모니터 분리·DPI 전환·macOS·작은 화면 가독성 |
| UI-005 | 준비·세션·캡처·번역 상태와 오류 안내 | 실제 무음·권한·장치·지연 상태의 구분과 복구 |
| HIS-001 | core `MAX_HISTORY=1000`, worker/client 기록 상한 | 실제 긴 세션의 퇴출·실패 상태·재조회 수용 |
| HIS-002 | UTF-8 TXT·원문 SRT·`srt_bilingual`, 파일/IPC 검사·Windows UI 44개 | 실제 OS 저장 창·macOS·실제 플레이어 수용 |
| MOD-001 | 모델 경로 오류 처리·설치 자산 해시 준비 도구 | 앱 입력의 경로/형식/해시 오류와 배포 자산 검증 |
| SEC-001 | loopback endpoint 제약·redirect 금지, 빌드 telemetry opt-out | 기본 실행 전체의 외부 요청/telemetry 관측 |
| SEC-002 | 설정에는 token 없음. worker는 환경 변수 사용 | OS 비밀 저장소·참조 저장. 런처 임시 key 파일의 수명/보호 검증 |
| OPS-001 | WorkerClient 종료·process-tree kill·UI 연결 복구 경로 | 실제 worker 사망·재시작·고아 프로세스·장시간 실패 검사 |

## P0B 기능 4개

| ID | 현재 범위 | 남은 작업 |
| --- | --- | --- |
| TR-005 | `run-live.ps1`이 설치된 외부 런타임을 시작 | 앱 관리형 설치·health·인증·실행·종료와 macOS backend |
| UI-003 | Windows 잠금/해제와 창 속성 구현 | 실제 클릭 전달·해제와 macOS 구현/검증 |
| MOD-002 | 동의받은 연구용 다운로드·manifest | 앱 동의·진행·취소·불완전 파일·해시 오류·삭제 흐름 |
| OPS-002 | 개발 환경 실행 | SDK 없는 양 OS 설치·권한·모델 준비·실행·정리 |

## 비기능 요구 8개

| ID | 부분 근거 | 남은 판정 |
| --- | --- | --- |
| NF-001 | 캡처 callback와 유한 큐 분리 | 실제 과부하·stdout stall 중 callback 지연 |
| NF-002 | core/owner/history/자막 저장 상한·가상 반복 | UI+worker+server 실제 메모리 추이·양 OS 2시간 soak |
| NF-003 | Windows 짧은 Stop 관측, 취소 owner | 최신 실제 준비 취소·Stop≤0.5초·기기별 범위 |
| NF-004 | stale key·epoch·Stop·재개 자동 계약 | 실제 지연 응답과 100회 제어 |
| NF-005 | 부분 스케줄·단일 번역 owner·VAD 억제 | 실제 무음/효과음/음악·모델 호출 0회 조건 |
| NF-006 | 설치 자산·로컬 HTTP 실행 | 관리형 모델 준비 후 네트워크 없는 양 OS 실행 |
| NF-007 | 앱 내부 focus·키보드·접근성 이름 | 물리 키 입력·화면 읽기 프로그램·macOS |
| NF-008 | lock·native/model manifest·근거·환경 기록 | 미커밋 변경 동기화·다른 PC 재현·패키지 자산 일치 |

## 필수 검사 ID의 판정 범위

| 검증 ID | 현재 판정 | 추가 작업 |
| --- | --- | --- |
| UT-001~008 | audio/core/translation의 테스트 소스와 과거 check 근거 | 최신 전체 `scripts/check.ps1`; 이번 자막 검사로 전체 통과를 대신하지 않음 |
| UT-009~010 | IPC Unicode·원문 SRT 검사 경로 | 원문+번역 SRT의 시간·revision·Unicode 검사 추가 |
| UT-011~012 | 기본 로그/endpoint 방어 경로 | 비밀 저장소·임시 key 파일·default 실행의 실제 정보/요청 검증 |
| IT-001~004 | worker/protocol/HTTP 오류·귀속 검사 소스 | 최신 전체 check와 실제 worker/server 장애 주입 |
| IT-005~006 | VAD·파일 억제·pending 종료 검사 경로 | 정해진 무음 길이·실제 packet 중단과 모델 호출 측정 |
| HW-W01~02 | 짧은 Windows PCM 수신 | 10분·장치 추종/분리. 최신 완주 근거 없음 |
| HW-M01~02 | 미착수 | macOS capture·권한·서명·재실행 |
| HW-UI01 | 저장 화면·창 속성 확인 | DPI/모니터 자동 복구와 실제 클릭 통과 |
| HW-E2E | 실제 30분 최신 완주 없음 | 양 OS 시스템 음성→ASR→번역→overlay |
| REL-001~002 | 개발 환경·가상 반복 | SDK 없는 설치, 실제 2시간과 100회 제어 |

## 다음 구현 순서

1. **HIS-002:** 기존 `srt` 호환을 유지하며 별도 원문+번역 형식을 추가한다.
   확정 원문과 같은 revision의 완료 번역만 결합한다. 번역 실패·미완료·stale의 저장 정책을 명시한다.
   worker export·IPC·스키마·C# 버튼을 함께 변경한다. 시각·UTF-8·순번·실패 저장을 자동 검사한다.
2. **UI-004:** 화면 목록 변경·DPI 변경을 감지하고 접근 가능한 작업 영역으로 복구한다.
   기하 계산을 순수 코드로 검사한다. 이후 실제 다중 화면 검사만 묶어 수행한다.
3. **AUD-002 / SEC-002:** 기본 장치 추종 설계와 OS 비밀 저장소를 별도 구현한다.
   캡처 시작 원인 조사는 보류를 유지한다. 장치·권한 검사는 실행 가능한 환경에서 수행한다.
4. **M3 수용:** corpus·정답·검토문과 실제 품질/지연/30분 결과를 확보한다.
   영어/일본어 각 50개 중 의미 합격≥45개, 중대 오류 0개와 기존 지연 기준을 적용한다.
5. **M4~M5:** 관리형 자산·프로세스·패키징·양 OS soak. macOS 플랫폼 probe가 먼저다.

자동 판정이 어려운 의미 품질·최종 사용성만 사용자 확인 후보로 남긴다.
동일 조건의 반복 수동 확인은 요청하지 않는다. 이번 라운드에는 수동 검사를 요청하지 않았다.

## 직접 확인한 소스와 인계

- [export.rs](../crates/worker/src/export.rs), [MainWindow.cs](../apps/EchoSub.Desktop/MainWindow.cs): `txt/srt`·원문 저장.
- [OverlayWindow.cs](../apps/EchoSub.Desktop/OverlayWindow.cs): 수동 `ResetPlacement`.
- [capture_runtime.rs](../crates/worker/src/capture_runtime.rs): `pinned_until_restart`.
- [pipeline-core](../crates/pipeline-core/src/lib.rs), [translation](../crates/translation/src/lib.rs): 기록/문맥 상한.
- [DesktopPreferences.cs](../apps/EchoSub.Desktop/DesktopPreferences.cs), [번역 계약](WORKER_TRANSLATION.md),
  [런처](../scripts/run-live.ps1): token 환경 변수와 임시 파일, OS 저장소 미구현.
- [WorkerClient.cs](../apps/EchoSub.Desktop/WorkerClient.cs): 종료·process-tree 정리.
- `native/`와 macOS capture crate는 현재 파일 목록에서 확인하지 못했다.

이번 문서는 미커밋이며 다른 PC에 전달되지 않았다. 문서 갱신은 커밋·푸시 승인이 아니다.
재개 시 저장소 루트에서 `scripts/check.ps1`과 `scripts/test-captions.ps1`의 범위를 구분한다.
Windows 개발 도구는 Rust/.NET 10·복원한 패키지다. native 실행에는 MSVC/CMake/LLVM/CUDA와 설치 자산이 필요하다.
모델·런타임 manifest는 소스에서 확인한다. 로컬 설정·모델·캐시·음원·로그·엔진 기록·PNG는 별도 준비한다.
