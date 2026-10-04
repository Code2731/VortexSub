# 코드 리뷰 종합 및 개선 기록

2026-10-03. 대상은 번역 설정, 자막 전달·읽기, VAD 계측과 진단 기능이다.
현재 소스와 외부 리뷰를 대조했다. 구현 목표는 [리뷰 개선 계획](REVIEW_IMPROVEMENT_PLAN.md)에 기록했다.
검증 명령·결과·제한은 [증거 문서](evidence/review-improvements-windows-20261003.md)에 기록한다.

## 확인된 문제와 조치

### 추가 외부 리뷰 대조 (2026-10-04)

추가 리뷰는 테스트 미실행 보고다. 아래 판정도 소스 대조와 빌드 결과를 구분한다.

| 항목 | 현재 소스 판정 |
| --- | --- |
| C1 정정 뺄셈 | `Unit`은 내부 상태다. 외부 history가 직접 들어오지 않는다. 손상 상태의 뺄셈·UTF-8 범위에 의존하지 않도록 `checked_sub`, `checked_add`, `str::get`으로 보강했다. 오류는 `InvalidRepair`로 보류한다. |
| C2 delivered 무한 누적 | 해당 없음. `CaptionDeck.Tick`은 전달 목록을 복사한 뒤 `delivered.Clear()`를 호출한다. `Update`도 `Tick`을 반환한다. 임의의 거대한 단일 배치에 대한 별도 상한은 다른 문제다. 장시간 세션 누적 주장과 구분한다. |
| C3 겹친 세그먼트 시각 덮어쓰기 | 현재 경로에서 확인되지 않음. VAD는 `active: Option<Active>` 하나를 보관한다. finalize는 active를 take한다. worker는 각 이벤트에 observe/live 처리를 순서대로 수행한다. 다중 VAD stream을 도입하면 구조를 재검토해야 한다. |
| M1 매니페스트 검사 없음 | 일부 사실과 다름. `model_profiles_match_download_manifests_and_document_standard_compatibility` 검사가 이미 있다. PS 런처의 ID 중복과 변경 비용은 남아 있다. |
| M2 지원 외 언어 무한 대기 | 현재 모든 프로필이 en/ja/ko만 허용한다. UI 언어 목록도 동일하다. prepare 오류는 SubmitError::Invalid로 반환되고 worker는 Outcome::Failed와 상태 이벤트를 발행한다. Hy-MT2만의 미처리 오류가 아니다. |
| M3 Run 압축 | 성장 시 Run 추가는 맞다. 실제 비용 측정은 없다. 임계 압축은 기존 glyph 객체 보존과 충돌하므로 깜빡임 회귀 검증 없이 적용하지 않는다. |
| M4 레거시 revision 0 | 비교 불가한 값의 정책을 명시할 필요는 있다. 일괄 무시는 현재 synthetic/legacy 호출 계약을 바꾼다. 생산 이벤트의 순서·중복 필터와 분리하여 후속 검토한다. |
| M5 pending 소켓 누수 | 해당 없음. `close_pending`의 take는 Owner를 Drop한다. Drop은 cancel, 채널 닫기, thread join을 수행한다. 명시적 cancel 추가는 동일 동작을 반복한다. |
| M6 커밋 규모 | 논리별 커밋 분할 권고는 유효하다. 미커밋 규모 자체가 Conventional Commit 제목 규칙 위반은 아니다. 이번 대조에서 커밋·푸시는 수행하지 않았다. |
| logs ignore | 이미 `/logs/`가 있다. 런처 BAT는 기여 대상 소스이므로 생성 로그와 구분한다. |

이번 변경은 Rust 포맷을 적용하고 offline workspace 빌드, CUDA/VAD release worker
빌드, Windows 앱 빌드를 완료했다. 앱 경고/오류는 0개였다.
새 방어 경로의 회귀 테스트와 실제 음성 실행은 수행하지 않았다.
장시간·물리 화면·macOS 잔여 검증은 기존 제한을 유지한다.

| 항목 | 확인 결과 | 조치 |
| --- | --- | --- |
| 중간 자막 누락 | Deck의 최신 세그먼트 선택과 Lines의 최신 대기 입력 교체에서 발생 가능 | 세그먼트별 전달, 기록 이벤트 버퍼, 제한된 읽기 대기열 연결 |
| 설정 커밋 실패 | core 오류가 poll을 통해 worker 종료로 전파됨. 일반 경로 재현은 확인하지 않음 | pending 폐기, 기존 Ready 유지, CommitRejected 상태 발행과 재시도 검증 |
| VAD 관측 시각 | 기존 필드는 문서대로 batch 처리 완료 시각임 | 처리 시작 필드를 추가하고 push/poll 기간을 분리 |
| RepairTooLong | 기존 긴 입력은 IncompleteRepair만 검증함 | 383/384/385바이트·UTF-8 경계와 final 번역 진행 검증 |
| 모델 ID 이중 관리 | 프로필 검사와 다운로드 매니페스트의 ID가 별도임 | Rust 상수화와 매니페스트 일치 검사. 생산 실행은 benchmark 파일을 읽지 않음 |
| UI 반복 작업 | Body 배열 생성과 같은 글자의 재측정 등이 있음 | Lines 본문 캐시, 빈 전달 목록 공유, FitLine 최근 측정 캐시와 인덱스 순회 |
| 저장소 위생 | Python 캐시와 임시 검사 로그가 untracked에 노출됨 | gitignore 보완 |

읽기 대기 상한은 4개·10초다. 같은 단위의 수정본은 합친다.
서로 다른 단위는 순서를 유지한다. 상한 초과·원문 무효화·세션 초기화를 구분한다.
이미 보이는 줄의 2.5초 읽기 보호는 유지한다. 과부하에서 전체 문장 보존을 보장하지 않는다.

## 정정하거나 보류한 지적

* `Hy-MT2만 ko/en/ja 제한`: 공통 요청 검증과 core 언어 검증에 같은 제한이 있다.
* `Inlines null 방어 필수`: 실제 결함을 확인하지 않은 추측이었다. 해당 권고를 제거한다.
  Avalonia 12.0.1의 실제 글자 추가 경로와 기존 Inline 객체 보존을 렌더 검사에서 확인한다.
* `pending_owner.cancel 추가 필수`: idle/configuring 가드와 Owner 소멸 시 취소가 이미 있다.
  별도 취소 누락 재현 없이 호출을 추가하지 않는다.
* `UI 자동 검증 부재`: 실제 Avalonia 렌더와 MainWindow/IPC/타이머 검사가 존재한다.
  실제 추론 부하와 물리 화면 검증은 별도다.
* `Previous 슬롯`: 생산은 항상 null이다. 기존 진단 소비자와의 호환을 위해 필드를 유지한다.
  도달 불가능한 previous 진단 분기는 current로 정리했다.
* `LINQ가 매 tick 델리게이트를 생성`: 비캡처 람다는 캐시될 수 있다.
  할당과 실제 처리 비용을 구분해야 한다.

Qwen은 standard의 JSON/system 계약과 greedy 비교 계약을 사용할 수 있다.
Hy-MT2는 전용 user 번역 계약을 요구한다. 이 차이를 accepts_model 옆에 명시했다.

## 검증 판단

빌드·테스트 통과는 실행한 시나리오의 결과다. 모든 상태나 모든 깜빡임의 해결을 뜻하지 않는다.
가상 시각, 실제 타이머, 저장된 추론 기록, 새 추론 실행을 각각 구분한다.
저장된 PNG는 물리 화면의 연속 프레임이 아니다.

자연 음성·게임 30분 사용, 새 timing의 실제 음성 지연 비교, 실제 UI의 모델 설정 실패 복원,
macOS 검증은 남아 있다. 대규모 미커밋 변경의 분할 커밋도 릴리스 준비에서 다룬다.
