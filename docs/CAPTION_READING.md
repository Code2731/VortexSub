# 줄 단위 자막 표시

현재 화면은 사용자가 선택한 **두 줄에 이어 쓰고 읽은 줄부터 제거** 방식이다.
[줄 표시 규칙과 확인 범위](CAPTION_LINES.md)를 참고한다. 아래 한 위치 정책은
줄 표시기로 공급되는 입력 카드의 처리 규칙이다. 실시간 앱은 서로 다른 단위를
제한된 대기열로 전달한다. 독립 카드 비교 harness의 정책은 별도로 유지한다.

## 사용과 표시

기존 배치파일로 다시 실행하면 한 위치에 최신 자막을 표시한다. 원문 표시는
기존처럼 기본 꺼짐이다. 이전 자막 영역은 제거했고 기존 문장을 다른 위치로 옮기지 않는다.
오버레이의 이전/현재/갱신 중 라벨과
`[임시 번역]` 본문 접두사는 표시하지 않는다. header는 내용이 갱신될 수 있음을
항상 같은 문구로 알린다. history와 내부 draft 상태는 계속 유지한다.
원문을 켜면 번역 아래에 표시한다. 긴 내용은 스크롤하며 새 문장 교체 시 초기화한다.
새 segment에 번역이 아직 없으면 보이던 자막을 기존 만료까지 유지한다.
새 번역은 같은 위치에서 교체하며 새 번역 표시 후 이전 번역으로 되돌아가지 않는다.
실시간 앱은 새 segment/번역 단위를 즉시 줄 표시기에 전달한다. 기존 줄을 지울지는
줄 표시기의 읽기 기한으로 결정한다. 입력 단계의 1.25초 대기는 중복 적용하지 않는다.
원문 prefix 반박과 같은 단위의 최종 교정은 즉시 경로를 유지한다.
같은 단위의 수정본은 최신 결과로 합친다. 서로 다른 단위는 최대 4개·10초 대기한다.
상한 초과와 표시 대기는 [리뷰 개선 계획](REVIEW_IMPROVEMENT_PLAN.md)을 따른다.

## 시간과 갱신 규칙

번역 변경 진단은 [P2.1 기록](TARGET_CHANGE_DIAGNOSTICS.md)을 참고한다.
같은 단위 교정, 다음 단위 전환, 최종 전체 복원을 구분하며 표시 규칙은 유지한다.
선택형 [마침표 갱신 묶기](COSMETIC_REVISION_POLICY.md)는 세션 시작 전에 켠다.
같은 본문의 끝 마침표에만 추가 대기를 적용하며 기본은 꺼짐이다.

- 입력 카드 수명은 `2 + 문자 수 / 8`초다.
  공백과 상태 접두어를 제외한 Unicode 문자 수이며 최소 4초, 최대 10초다.
  번역이 없으면 원문 길이를 사용한다. 화면 줄의 읽기 시간은 별도 최소 2.5초이며
  입력 카드 만료 후에도 받은 최신 번역의 남은 줄을 읽는다.
- 동일 번역의 반복 조회와 원문 revision 변경은 읽기 만료를 연장하지 않는다.
  수정된 번역을 실제 표시하면 그 내용의 읽기 시간이 시작된다.
- 실시간 앱의 일반 수정은 즉시 줄 표시기에 전달한다. 화면의 prefix 수정은 읽기
  후 적용하고 단순 추가는 열린 줄 뒤에 붙인다. 같은 단위의 교정은 최신 하나다.
  서로 다른 단위는 제한된 대기열에 보관한다.
  독립 카드 비교 harness의 기본 1.25초 정책과 실시간 앱의 줄 보호를 구분한다.
- 만료된 같은 내용은 부활하지 않는다. 다른 유효 번역은 새 내용이다.
- Pause/Stop/통신 오류/UUID·epoch 변경은 자막과 대기를 함께 지운다.
  history는 변경하지 않는다. identity와 revision 검증은 기존 presenter를 사용한다.

0.1초 표시 전용 timer에서도 갱신/만료를 처리한다. 0.5초 IPC 조회와 분리했다.
같은 카드 상태는 오버레이에 다시 대입하지 않는다. IPC가 늦어도 timer는 독립적으로
동작하며 UI thread가 정상일 때 표시 시점은 한 tick 정도 늦어질 수 있다.
기본 오버레이 높이는 220이고 긴 내용은 스크롤할 수 있다.

2026-10-03 새 단위의 1회 전달→읽기 후 표시를 연결 검증했다.
[입력 대기 제거와 확인 범위](evidence/caption-admission-windows-20261003.md).

## 이번 확인 범위 (2026-10-01)

Windows에서 Desktop과 ProtocolSmoke 프로젝트 빌드(경고/오류 0)를 확인했다.
이번 라운드의 테스트 실행, 화면 렌더링/마우스 조작, 실제 발화의 읽기 시간
평가는 미실행이다. 4~10초와 1.25초는 초기 정책이며 읽기 보장을 실측한 수치가 아니다.
후속 단위 번역 라운드에서 표시 fixture 29개 assertion을 실행했다. 실제
렌더링/읽기 평가는 여전히 미실행이다.

후속 draft 갱신 라운드에서 39개 assertion과 전체 check를 통과했다.
0.1초에 도착한 수정은 fixture의 0.3초 tick에서 적용됐고, 반복 조회는
대기 시점을 초기화하지 않았다. 이전 카드 보호·만료·즉시 확정도 확인했다.
이는 pure 표시 상태 측정이며 실제 화면 지연 감소의 실측이 아니다.

## 선택 진단

2026-10-02 [첫 ASR 이전 계측](PRE_ASR_TIMING.md)을 추가했다.
기존 옵션으로 VAD 음성 관측·첫 요청 후보·첫 큐 등록과 보류 이유도 기록한다.
실제 음성 시작이나 물리적 화면 출력 시각을 측정한 값은 아니다.

앱의 **자막 지연 기록** 옵션을 켜면
`logs/caption-timing-*.jsonl`에 번역 이벤트 수신, Deck 반영과 오버레이 속성
대입 시점을 기록한다. 기본 꺼짐이며 세션 실행 중에도 켜고 끌 수 있다.
켤 때마다 새 파일을 만들고 옵션 아래에 절대 경로를 표시한다. 끄면 기존
파일을 유지한다. 저장 실패 시 옵션을 끄고 오류를 표시한다.
`-CaptionTiming` 실행 인자는 시작 시 옵션을 켜는 호환 기능으로 유지한다.
직접 실행 시에는 현재 작업 폴더의 `logs/`를 사용한다. 원문/번역은 기록하지 않으며 worker PID,
session/epoch/segment/revision/request ID, 초 단위 시점과 대기만 남긴다.
로그는 Git 제외, background writer/128개 대기로 처리하며 고갈 시 drop을 기록한다.
정상 창 종료에서 최대 0.5초 동안 writer를 drain한다. 강제 종료/파일 오류나
고갈로 빠진 관측은 수용 근거로 쓰지 않는다.

```powershell
models/tabby/venv/Scripts/python.exe scripts/summarize-caption-timing.py logs/caption-timing-<run>.jsonl
```

동일 ID의 첫 visible Deck 반영을 수신 이벤트와 연결해 지연·Deck 대기를 요약한다.
512개 ID 상한 밖/누락/숨겨진 오버레이는 완전한 관측으로 보지 않는다.
`overlay_assigned`는 Avalonia 속성 대입이며 GPU 합성·물리 화면 표시 완료가 아니다.

## 단계별 지연 로그 (2026-10-01)

**자막 지연 기록**을 세션 시작 전에 켜면 같은 파일에 `pipeline_event_received`도
기록한다. 원문/번역을 포함하지 않고 worker PID와 전체 identity, 초 단위 시간,
안정 원문 문자 수와 적용/대기 여부만 선택한다. 기존 요약 명령은 UI 통계와
`pipeline.durations_s`를 함께 출력한다. 항목별 `n`은 실제 연결된 관측 수다.

| 항목 | 의미 |
|---|---|
| `sampled_voice_start_to_request_audio_end_s` | VAD 음성 시작부터 요청 PCM 끝까지의 샘플 길이 |
| `latest_partial_deferred_wait_s` | 최신으로 남긴 부분 요청의 scheduler 보류 시간 |
| `asr_admission_to_owner_dispatch_s` | ASR 접수→native owner 전달 |
| `asr_owner_dispatch_to_completion_s` / `native_decode_s` | owner 전달→완료 처리 / native decode 처리 |
| `first_source_to_first_nonempty_stable_s` | 첫 원문→첫 비어 있지 않은 안정 prefix |
| `source_revision_ready_to_translation_dispatch_s` | 해당 원문 revision 가용→번역 owner 전달 |
| `translation_owner_dispatch_to_completion_s` | 번역 owner 전달→완료 처리 |
| `first_nonempty_stable_to_first_translation_update_s` | 첫 안정 prefix→첫 적용 번역 발행 |
| `first_observed_admission_to_first_translation_update_s` | 처음 관측한 ASR 접수→첫 적용 번역 발행 |

worker의 `worker_at_s`는 프로세스 시작 기준이며 UI의 `at_s`와 직접 빼지 않는다.
owner 전달→완료에는 내부 대기/완료 polling이 포함되고, native decode 시간은
별도다. 샘플 길이는 실제 벽시계 지연이 아니다. 캡처/VAD owner의 이전 대기,
물리 음성 재생/화면 렌더링과 worker→UI 전송 시간은 이번 계측으로 측정하지 않는다.
부분 접수 이벤트는 coalesce될 수 있어 처음 관측한 접수가 최초 접수와 다를 수 있다.
안정 prefix는 번역 가능한 완성 단위와 같지 않다. 여러 단위의 후속 요청에는
기존 0.5초 간격도 원문 가용→번역 전달에 포함될 수 있다.

맵은 각각 최대 512개이며 ID/시점 누락·역순은 `missing_or_invalid_pairs`로
보고하고 해당 쌍을 통계에서 제외한다. ignored ASR/unapplied 번역은 별도 집계한다.
실패했지만 pipeline이 수용한 번역 완료도 완료 시간에 포함될 수 있다.
구형 로그의 `pipeline.events=0`은 미측정이다. 이번 로그 형식은 기존 UI 집계와
호환되며, 새 실행 결과로 병목을 판단해야 한다.

### 임시 번역 보류 이유 (2026-10-02)

`source.partial`의 `preview_hold_reason`을 같은 옵션으로 기록한다.
요약의 `preview_decision_observations`는 전체 부분 원문 갱신의 판정 횟수,
`before_first_translation_decisions`는 segment의 첫 적용 번역 전 판정 횟수다.
시간 합계나 보류 해제 이벤트가 아니며, 새 ASR 갱신 때의 상태만 관측한다.

- `NoStablePrefix`: 두 전사의 공통 원문이 최소 기준에 도달하지 않음.
- `TooShort`: 남은 번역 단위가 최소 길이에 미달.
- `IncompleteNumber` / `IncompleteCondition` / `ConditionContinuation` /
  `DanglingWord`: 숫자·조건·관사/연결어 조각의 기존 보류 규칙.
- `Cadence`: 기존 0.5초 임시 요청 간격. `FinalAsrQueued`,
  `FinalTranslationQueued`, `FinalTranslationInFlight`: 확정 작업 우선.
- `EmptyTail` / `AlreadyTranslated`: 새로 번역할 단위 없음.
- `Disabled` / `AsrUnavailable`: 임시 번역 꺼짐 또는 ASR 원문 불가.
- `Eligible`: 선택 시점의 사전 보류 없음. HTTP 성공/의미 완성 보장은 아님.
- `Unreported`: 구형 로그나 알려진 값이 없는 경우. 원인을 추정하지 않는다.

이유는 고정 목록만 저장하며 임의 문자열을 로그로 복사하지 않는다.
빠른 CUDA 모드는 첫 부분 요청 조건을 0.8→0.5초(프레임 반올림 0.512초)로
앞당겼다. 기본 모드는 0.8초다. 두 전사 일치와 보류/읽기 규칙은 유지한다.
짧은 입력에서 전사 변경/무음 판정과 재추론 비용이 늘 수 있으며, 전체 첫 자막
가속은 새 실행 로그로 확인해야 한다. 기존 실행의 결과를 개선 후 측정으로 쓰지 않는다.

### 적응형 스케줄 계측 (2026-10-02 후속)

빠른 모드의 첫 요청은 0.8초로 복구했다. 앞의 0.5초 첫 요청 변경은 현재
동작이 아닌 이전 라운드 기록이다. [적응형 정책](ADAPTIVE_PARTIALS.md)을 따른다.

부분 접수 이벤트의 `adaptive_policy`는 `Initial`, `ConfirmSoon`, `StableProgress`,
`EmptyBackoff`, `UnchangedBackoff`, `DecodeCostBackoff` 또는 기본 `Fixed`다.
`adaptive_growth_s`는 이전 접수 이후 필요한 **새 오디오 샘플 길이**이며
벽시계 timer가 아니다. 첫 요청은 별도의 0.8초 VAD 조건을 따른다.
`asr.completed.outcome_kind`는 Text/NoSpeech/OverlapOnly/Cancelled/Failed만 기록한다.
Text는 모델 원문 반환의 분류이며 최종 적용/품질 성공을 뜻하지 않는다.

요약은 `adaptive_policy_observations`, `asr_completion_outcomes`와
`adaptive_required_audio_growth_s`를 출력한다. 없는 구형 필드는 Unreported로
남긴다. 진단 상태의 `requested`는 VAD 후보 수이며 실제 추론 수는 아니다.
`adaptive_deferred`는 새 오디오 부족으로 보류한 후보 수다. 후보/처리 수를
구분해 고갈·취소·반복 처리와 함께 비교한다.

## 세션 전환 기록 (P3.2)

2026-10-02 Hy-MT2 실제 사용에서 같은 단위 수정 83건과 읽기 전 교체/소실을
보고받아 draft 일반 수정 간격을 0.25초에서 1.25초로 늘렸다. 첫 번역과 원문
무효화/최종 교정은 기존 즉시 경로를 유지한다. 최신 대기 하나와 표시 시각 기준
기한을 유지하므로 수정이 계속 들어와도 기한이 늘어나지 않는다.
새 segment/단위 전환과 두 카드가 모두 찬 경우에는 이 일반 수정 제한이 적용되지 않는다.
화면의 모든 자막에 1.25초 읽기를 보장하는 정책은 아니다. 빌드만 완료했으며
변경 후 실제 읽기 개선은 미확인이다.

상태 변화와 적용 모델/profile, 카드 적용 문맥, 번역 오류·취소를 추가로 기록한다.
같은 상태는 약 5초마다 기록하고 요약의 `session_epochs`에서 세션/epoch별로 나눈다.
구형 필드 부재나 취소 응답을 정상/실패로 단정하지 않는다.
[실제 사용 확인 방법과 제한](SESSION_TRANSLATION_REVIEW.md)을 참고한다.
