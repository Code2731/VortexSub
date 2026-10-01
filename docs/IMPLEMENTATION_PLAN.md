# EchoSub 단계별 구현 계획

2026-10-02 두 번째 단계의 owner 연결/fallback을 실험 옵션으로 구현했다.
실제 paced CUDA/HTTP 비교에서 첫 번역 1.830→1.841초, 누적 decode
1.296→1.477초로 개선은 확인되지 않아 기본 off다. 다음은 합의한 세 번째
단계인 ASR 후보의 동일 입력 비교다. [수용 범위](evidence/window-owner-windows-20261002.md).

2026-10-02 DTW 발화점 기반 파일 정렬/재결합을 구현했다. CUDA 동일 파일
3회 모두 축소 입력의 재결합 원문이 전체 전사와 일치했다. 기존 interval을
고쳐 쓴 것이 아니며 일반 전체 대비 처리 시간 이득은 약 0.009초로 작다.
다음은 적용 revision의 mapping과 owner fallback 연결, DTW 비용/거부율 비교다.
[실제 범위와 근거](evidence/dtw-alignment-windows-20261002.md).

2026-10-02 두 번째 단계의 기반인 product range/decode window를 분리하고
정렬·overlap 재결합을 fixture/파일 probe로 연결했다. 실제 CUDA에서도 길이 0인
단어 시간으로 cut을 거부했다. 다음은 단어 시간 정렬 확보와 적용 revision의
mapping, owner 재결합 실패 시 전체 입력 fallback이다. 라이브 trim과 추가 속도
개선은 아직 미완료다. [구현 범위](DECODE_WINDOWS.md).

2026-10-02 사용자와 고도화 순서를 합의했다: **결과별 적응형 전사 → 안정된
구간 이후 재인식 → 스트리밍 ASR 후보 비교**. 첫 단계는 빠른 모드에 연결했고
실제 CUDA/HTTP 파일 비교에서 첫 번역 2.016→1.738초를 관측했다.
기본 활성화/품질 gate는 보류하며 실제 음성 로그 확인이 필요하다.
다음은 제품 range/decode window 분리와 신뢰할 수 있는 시간 정렬 mapping이다.
[적응형 구조·다음](ADAPTIVE_PARTIALS.md).

2026-10-01 실시간 번역 OSS 4개를 고정 커밋 소스로 검토했다.
[검토 결과와 다음 순서](REALTIME_OSS_REVIEW.md)에 따라 partial 스케줄링·처리시간
계측, 두 카드 읽기 정책, 의미 단위의 조기 번역, 후보 모델 비교 순으로 진행한다.
기존 독립 prefix replay는 실행 중 native ASR의 revision 경합을 검증하지 않았다.
검토는 구현/실측 완료가 아니며 라이브 환경의 실제 병목은 아직 확정하지 않았다.

첫 스케줄러 개선과 누적 계측을 구현했다. 실제 CPU Whisper 파일 재생에서
0.25초 스트레스의 부분 결과 폐기를 재현하고 방지했지만, 기본 1초의 첫
부분 결과 지연은 거의 같았다. [검증 범위](evidence/partial-scheduling-windows-20261001.md).
이전 읽기 카드와 현재 draft 분리도 구현했다. [표시 계약](CAPTION_READING.md).
문장/짧은 절 임시 번역도 연결했다. [단위 계약](TRANSLATION_UNITS.md).
실제 Qwen 동일 trace 비교에서 요청 9→5회, 첫 출력 가속은 없었고 오역이 남았다.
token 시간 검토는 단어 길이 0으로 trim을 거부했다. 라이브 trim은 후속이다.
[실측과 다음 작업](evidence/translation-units-trim-windows-20261001.md).
화면/읽기 수용도 별도로 남아 있다.
기본 ASR 요청 간격은 유지한다.

기존 Whisper CPU/CUDA paced 비교도 완료했다. 1초 간격의 첫 텍스트 중앙값
2.781→2.182초, 확정 8.554→7.810초를 관측해 CUDA 선택 런처를 제공한다.
기본 CPU·간격은 유지하며 다음은 캡처 없이 paced ASR→Qwen의 단계별
지연/품질·GPU 동시 경쟁 비교다. [범위](evidence/partial-backends-windows-20261001.md).

실제 ASR→Qwen GPU 파일 비교에서 0.5초 간격의 첫 번역이
3.361→2.330초로 앞당겨졌다. 빠른 CUDA 모드를 선택 실행으로 제공한다.
미완성 원문 노출/오역과 비용 증가로 기본 간격은 유지한다. 다음은 UI 수신→
카드 교체 계측과 읽기/current draft 제한 검토다.
[조건과 한계](evidence/paced-translation-windows-20261001.md).

현재 draft 교체 0.25초·독립 표시 timer 0.1초와 선택 metadata 계측을 구현했다.
계측은 앱의 **자막 지연 기록** 체크박스로 실행 중 켜고 끄며 저장 경로를 확인한다.
이전 읽기 보호와 기본 IPC 조회는 유지한다. fixture 확인이며 실제 화면/log 수용은
별도다. 사용자 로그 32건은 수신→Deck 중앙값 0.033초·보호 대기 0초다.
미완성 영어 조건/숫자 조각 보류를 구현했고, 다음은 동일 trace 품질/지연 비교와
수집→ASR→안정 판별→HTTP의 단계별 계측이다.
단계별 metadata 계측은 기존 UI 옵션에 연결했다. worker 내부 시계와 UI 시계를
분리하며 샘플 길이를 별도 집계한다. 다음은 사용자 새 로그로 실제 단계 병목 확인,
기존 미완성 조건/숫자 trace의 품질·첫 출력 지연 비교다.
[계측 정의](CAPTION_READING.md).
2026-10-02 사용자 부분 모드 로그에서 안정 판별 대기를 확인했다. 빠른 모드의
첫 부분 요청을 0.8→0.5초로 앞당기고 원문 갱신별 보류 이유를 기록한다.
다음은 같은 입력의 변경 후 로그로 첫 안정/번역 시점과 보류 판정을 확인하고
오역·수정 빈도를 비교한다. [구현·한계](evidence/early-partial-windows-20261002.md).
[사용자 로그와 보류 구현 범위](evidence/caption-user-log-windows-20261001.md).
[근거](evidence/caption-display-windows-20261001.md).

2026-10-01 사용자 요청의 안정 prefix/임시 번역 실험을 opt-in으로 구현했다.
오프라인 비교는 첫 표시를 앞당길 가능성과 의미 반전을 함께 확인했다.
기본 꺼짐·품질 gate 보류를 유지한다. [결과와 범위](STREAMING_TRANSLATION.md).
후속 비교 후보는 SenseVoiceSmall, Fun-ASR MLT Nano, 비사고 모드의 Qwen3 1.7B다.
[동일 조건·품질·다운로드 선행 조건](MODEL_CANDIDATES.md)을 따르고 ASR 교체,
번역 모델 축소, 임시 번역 활성화를 각각 비교한다. 기존 live E2E·게임 경합·
자연 음성·UI·Mac 수용 작업은 남아 있다.

2026-10-01 번역 엔진 비교: 동의받은 Tabby 설치와 실제 HTTP/파일 worker 비교를
완료했다. 현재 설정 두 실행 순서에서 llama.cpp 평균 0.118728~0.130975초,
Tabby 0.157779~0.160002초로 기본 llama 구성을 유지한다. Tabby 별도 런처는
선택형이다. [실측·품질·범위](evidence/translation-engines-windows-20261001.md).
다음 성능 작업은 live VAD 종료 대기·ASR·번역·UI 적용 시간을 분리해 측정하고,
기존 CUDA ASR 후보의 live 적용 및 게임과의 GPU 경합을 평가하는 것이다.
자연 발화 품질·전체 E2E 및 기존 M3/M4 수용 gate는 별도로 완료한다.

기준: `EchoSub_Concept_and_Spec_v0.1_KO.md`의 요구사항과 M0~M5 로드맵. 이 문서는 구현 순서와 단계 종료 조건을 정한다. 최신 코드·시험·실측 상태는 [구현 상태](STATUS.md)를 따른다. 아래 성능 수치는 목표이며 실측값이 아니다.

검토 상태(2026-09-30): 요구사항·단계 의존성과 실시간 처리 계약을 독립 검토했다. 기준선과 T00-01 착수에 사용할 수 있지만, 전체 아키텍처의 실행 가능성·제품 성능·일정이 검증된 상태는 아니다. 2단계 실험 결과와 아래 미결정 계약을 반영해 다음 단계의 작업을 구체화한다.

실행 단위 보완(2026-09-30): 단계 0~2의 산출물·선행 조건·실패 대응은 [M0 실행 계획과 검증표](M0_EXECUTION_PLAN.md)에 정리했다. M3에서 M4로 진행할 조건은 아래 별도 판정 절을 따른다. 새로 추가한 probe 수량과 진행 기준은 이번 계획의 결정이며, 구현·실측 완료를 뜻하지 않는다.

## 진행 원칙

- 한 작업은 코드와 그 변경을 판정할 검증 방법까지 포함한다. 작업 완료 시 관련 요구사항 ID, 실행 명령, 결과, 미실행 플랫폼을 기록한다.
- Windows와 macOS의 실제 시스템 오디오 캡처를 공통 파이프라인 확장 전에 확인한다. macOS는 **패키징된 앱에서 권한이 어느 프로세스에 귀속되는지**까지 확인한다.
- mock, 음원 fixture, 실제 모델, 시스템 오디오 캡처, 배포판 검증을 별도 결과로 관리한다.
- 단계의 gate를 통과하지 못하면 원인과 대안을 기록하고 해당 결과에 의존하는 후속 통합을 보류한다. fixture·프로토콜 등 독립 작업은 진행할 수 있으며 미검증 플랫폼의 완료와 구분한다. 달력 기준 출시일은 gate 결과를 본 뒤 정한다.

## 단계와 종료 조건

| 순서 | 목표 | 구현 작업 | 단계 종료 조건 |
|---|---|---|---|
| 0. 기준선 | 설계와 개발 환경을 재현 가능하게 만든다 | 통합 명세의 분리 문서와 예시를 저장소에 반영하고 `요구 ID → 작업 ID → 수용 시험 ID → 결과/미실행` 대응표를 둔다. 실제 Windows/Mac 접근 가능 여부, OS·SDK·native 도구와 UI 지원 범위를 기록한다. 필요한 의존성은 호환성을 확인하며 고정한다. 초기 음원 fixture와 정답·출처·해시를 준비한다. | 첫 빌드 도구와 실행 경로가 정해져 있다. 실기기/모델/음원 미확보 항목에는 영향받는 gate와 해결 방법이 기록돼 있다. |
| 1. 최소 수직 경로 (M0/T00-01) | UI가 실제 worker를 관리한다 | Rust workspace, Avalonia 창, worker 실행·종료, `hello`/`ping`/`get_state`/`shutdown`, 프로토콜 버전·오류 응답, `check.ps1`/`check.sh`를 만든다. | UI↔worker 왕복, 버전 불일치, EOF, worker 강제 종료가 검증된다. stdout에는 NDJSON만 나온다. |
| 2. 플랫폼 위험 제거 (M0/T00-02~04) | 두 OS의 핵심 경로가 가능한지 확인한다 | Windows WASAPI loopback과 장치 전환, macOS ScreenCaptureKit 브리지·권한·최소 `.app` 번들, 모델 전사·한국어 번역 품질/처리시간/메모리의 초기 기준선, 실제 decode 중 취소·종료·재시작, 기본 오버레이를 시험한다. | 양 OS에서 마이크 없이 10분 PCM을 받는다. macOS 번들 권한 재시도가 동작한다. 모델의 파일·라이선스·해시, 품질/자원 측정, 취소 후 수명과 창 동작 결과에 근거해 채택·수정·보류를 결정한다. |
| 3. 공통 코어 (M1) | 입력과 비동기 결과를 결정론적으로 다룬다 | 단조 세션 시간축, 16 kHz mono 변환, VAD, 유한 큐와 gap, segment/revision/epoch 상태기계, IPC DTO를 구현한다. 아래 snapshot 일관성·오디오 소유권·취소 상태 계약을 먼저 확정한다. | 무음·긴 발화·장치 변경·역순 결과·큐 과부하와 추가 계약의 fixture 검증을 통과한다. 플랫폼 캡처 없이 코어 테스트가 실행된다. |
| 4. 전사 경로 (M2) | 시스템 음성을 원문 자막으로 표시한다 | whisper.cpp 컨텍스트 재사용, partial/final 스케줄러, no-speech·중복 경계 처리, 양 OS 캡처 연결, 모델 경로 검증을 구현한다. | Windows/macOS에서 영어·일본어·한국어 실제 음성의 원문 자막이 나온다. Stop/Pause 뒤 늦은 결과가 표시되지 않는다. |
| 5. 개발 MVP (M3) | 외부 로컬 번역 서버로 사용 가능한 앱을 만든다 | 최종 원문만 번역, 짧은 문맥·deadline·stale 응답 처리, 메인 창/오버레이/기록/TXT·SRT 내보내기, 상태·오류 복구, UI 표시 시각까지 포함한 지연 계측을 구현한다. loopback/redirect 제한, OS 비밀 저장소, 로그 필터와 다중 화면 복구도 작업으로 배정한다. | 양 OS에서 30분 시스템 오디오→전사→번역→표시를 검증한다. 번역 서버가 꺼져도 원문 전사는 유지된다. P0와 아래 M3 진행 기준을 판정하고 GO일 때 M4 통합을 진행한다. |
| 6. 베타 기능 구현 (M4) | 별도 서버 설치 없이 번역한다 | 모델 카탈로그·동의·해시 검사·취소/삭제, 앱 관리형 번역 런타임, OS별 클릭 통과와 잠금 해제 복구를 구현한다. | 관리형 설치→전사·번역→종료와 P0B 기능을 검증한다. `OPS-002` 배포 검증 및 제품 베타 최종 판정은 7단계에 남긴다. |
| 7. 출시 검증 (M5) | 성능과 배포를 실측한다 | 기준 장치의 지연·메모리·품질 비교, 게임 동시 실행, 2시간 안정성/100회 세션 제어, SDK 없는 환경 설치, macOS 서명·공증과 Windows 의존성 검사를 수행한다. | `07_VALIDATION`의 출시 gate별 결과와 미달 조치를 기록한다. 미실행 항목은 완료로 표시하지 않는다. |

주요 요구사항 대응: 1단계 `OPS-001`; 2단계 `AUD-001~004`; 3단계 `AUD-005`, `ASR-002~004`, `NF-001~004`; 4단계 `ASR-001~004`, `MOD-001`; 5단계 `TR-001~004`, `UI-001/002/004/005`, `HIS-001/002`, `SEC-001/002`, `NF-007`; 6단계 `TR-005`, `MOD-002`, `UI-003`, `NF-006`; 7단계 `OPS-002`과 성능·안정성 최종 판정. `NF-005` 부하 제어는 3~5단계에서 구현하고 7단계에서 실측하며, `NF-008` 재현성은 0단계부터 모든 작업에 적용한다. 각 ID의 세부 수용 조건은 원본 명세를 따른다.

## 첫 구현 작업: 1단계의 작은 완료 단위

1. **저장소 기준선:** 설계 문서와 요구사항 ID를 가져오고, `rust-toolchain.toml`, `global.json`, 잠금 파일의 버전을 실제 SDK 확인 후 고정한다.
2. **worker 계약:** 한 줄 한 메시지 NDJSON, `v`/`kind`/`request_id`, 최대 메시지 크기, 네 가지 명령과 `UNSUPPORTED_CAPABILITY` 오류를 구현한다. 미구현 기능의 성공 응답은 만들지 않는다.
3. **UI 연결:** Avalonia에서 worker 한 개를 인수 배열로 실행하고 stdout/stderr를 별도로 비동기 소비한다. 연결 상태와 명시적 mock 표시를 보여준다.
4. **수명 검증:** 정상 종료, worker 사망, 부모 파이프 EOF, 느린 reader, 버전 불일치를 검증한다. Windows에서 실제 실행 명령과 결과를 기록하고 macOS 결과는 별도로 남긴다.

## 중요한 결정 지점

- **macOS 캡처/권한:** 2단계에서 선택한 번들 구조로 권한 복구가 실패하면 브리지와 프로세스 경계를 먼저 수정한다. 대체 캡처 API는 실패 원인을 확인한 뒤 평가한다.
- **모델 선택:** `small`/`base`와 번역 후보는 2단계부터 동일 음원으로 품질·처리시간·메모리를 비교한다. 처리 중단/재시작과 ASR·번역의 동시 부하도 짧게 시험한다. 이 결과로 후보 유지 여부를 결정하고, 5단계에서 UI까지 포함한 지연을 측정한 뒤 7단계에서 기본 프리셋을 확정한다. 초기 짧은 시험을 출시 성능 통과로 판정하지 않는다.
- **모델 확보:** 2단계의 실제 모델 시험은 사용자 동의로 모델 파일과 라이선스·해시를 확보한 뒤 진행한다. 확보 전에는 해당 시험을 미실행으로 기록한다.
- **오버레이 범위:** 일반 창과 테두리 없는 창을 수용 대상으로 삼고, 독점 전체화면은 실제 조합별 결과로 지원 범위를 결정한다.
- **개발 MVP와 베타:** 5단계는 외부 로컬 서버가 필요하다. 별도 서버 없는 번역은 6단계의 완료 조건이다.

## M3 결과에 따른 M4 진행 판정

이 절은 M4 투자 전에 사용할 내부 진행 기준을 추가한다. 원본의 성능 목표를 조용히 변경하지 않고 측정 후 진행·수정·보류를 결정한다. M5의 배포·장시간 안정성·게임 공존·관리형 런타임 최종 판정은 별도로 남는다.

### 측정 조건과 증거

- 양 기준 장치의 실제 OS, 모델/변환본 해시, backend, thread/context/partial 설정을 기록한다. 다른 장치 결과는 대체 측정으로 표시하고 기준 장치 판정은 미실행으로 남긴다.
- warm-up 완료, 게임 미실행, 고정된 2~8초 발화 fixture로 측정한다. 각 OS에서 원문 언어별 최소 50개, 번역 방향별 최소 50개 대사를 평가한다. partial을 사용하면 별도의 8초 초과 긴 발화 fixture로 첫 표시 지연을 최소 50회 측정한다. 이 표본 수는 이번 계획의 진행 판정 기준이다. 원본의 전체 음원 corpus와 분리해 어떤 subset과 반복 횟수를 썼는지 기록한다.
- 마지막 음성 sample부터 **같은 대사의 UI 반영 시각**까지 원문/번역 지연을 구한다. 프로세스 간 시계 보정 방법·오차와 성공 표본 수를 기록한다. P95는 정렬한 N개 중 `ceil(0.95 × N)`번째 값으로 계산한다.
- 누락 원문, 실패/skip 번역, 의도치 않은 gap도 예정된 평가 대사 수를 분모로 보고한다. 성공 결과만으로 계산한 P95가 전체 처리 성공을 뜻하지 않는다. 정상 기준선 평가에서 예상 대사가 누락되면 GO로 판정하지 않는다. 별도 장애 주입 시험의 예상된 skip/gap은 복구 계약대로 판정한다.
- 각 OS에서 최소 30분 실제 시스템 오디오 E2E를 수행하고 서버 중단·장치 분리·무음·Pause/Stop을 별도 시나리오로 확인한다. 숫자/부정/명령문 등 중요한 의미 오류는 실제 ASR 결과를 번역한 최종 화면 기준으로 평가하며, 원인 분석용 정답 원문 번역 결과를 별도로 기록한다.

### 판정표

| 항목 | M4 진행 기준 | 미달 시 대응 |
|---|---|---|
| P0 기능·복구 | 요구/시험 대응표의 P0 수용 조건 통과, 양 OS HW-E2E 완료, 번역 장애 시 원문 유지 | 해당 결함 수정 후 영향을 받는 시나리오 재검증 |
| 원문/번역 지연 | 위 조건에서 원문 P95 ≤ 2초, 번역 P95 ≤ 4초. OS/언어별로 구분하며 전체 평균으로 대체하지 않음 | base/small, partial on/off, 문맥 길이를 하나씩 비교하고 품질 변화도 기록 |
| 부분 자막·제어 | partial 사용 시 긴 발화 첫 표시 P95 ≤ 2.5초. 캡처 Stop ≤ 500ms 목표를 정해진 제어 시나리오에서 확인 | 유효 설정과 수명/스케줄러 병목을 수정. 모델 종료 시간은 별도 기록 |
| 번역 품질 | 양 OS·각 방향의 고정 50문장에서 의미 전달 합격 ≥ 45개, 숫자·부정·명령 오인 관련 중대 오류 0개 | 실제 출력과 검토 판정을 보관하고 전사/번역 원인을 구분해 재평가 |
| 전사 품질 | 영어 WER·일본어/한국어 CER와 원문 차이를 공개하고 ASR의 P0 수용 조건 확인 | 원본에 없는 WER/CER 합격 임계값을 임의로 만들지 않고 오류 유형과 조치 기록 |
| 누락·안정성 | 정상 기준선 subset의 예상 대사 누락·번역 실패/skip 0, 30분 E2E crash/deadlock 0, 디지털 무음 10분 final 0 | 큐/시간축/과부하와 VAD 경로 수정. 버린 대사를 측정 대상에서 제외하지 않음 |
| 자원 | ASR와 외부 서버를 함께 실행한 메모리·대기 증가·OOM 여부 기록. 30분 중 자원 고갈이나 지속적인 backlog 증가 없음 | 후보/설정을 재비교. 외부 서버의 자원 사용을 통제·측정 가능한 범위와 구분 |

macOS 10 GiB·Windows dedicated VRAM 5 GiB는 원본의 **관리형 실행 목표**다. M3 외부 서버 결과는 동일 측정 범위로 비교할 수 있을 때 참고하며, 관리형 목표를 통과했다고 기록하지 않는다. M4에서 관리형 backend를 처음 연결할 때 같은 fixture로 품질·지연·자원 회귀를 확인하고 M5에서 최종 판정한다.

- **GO:** 두 OS에서 위 진행 기준과 필요한 증거가 충족됐다. 사용한 임시 기본 설정과 알려진 제한을 기록하고 M4로 진행한다.
- **REWORK:** 필요한 실험은 수행했으나 기준을 만족하지 못했다. 원인·변경할 변수·영향받는 시험을 정하고 수정/재측정한다. 이 경로에 의존하는 M4 통합은 보류한다.
- **BLOCKED:** 실기기·음원·모델·검토 자료가 없어 필요한 판정이 불가능하다. 확보 조건과 독립적으로 진행할 작업을 기록한다.

같은 실패를 반복하면 후보·구조·제품 목표 중 변경할 대상을 결정 기록으로 남긴다. 목표/지원 범위를 변경하면 수정한 요구사항으로 다시 판정하고 기존 목표 통과로 표시하지 않는다.

## 구현 전에 닫을 미결정 계약

| 시점 | 결정할 내용 | 완료 증거 |
|---|---|---|
| 0~2단계 | 최소 fixture 묶음과 모델 파일 확보; 이후 영어 20분·일본어 20분·한국어 10분·무음/효과음/음악 10분 및 번역 방향별 50문장 평가 자료로 확장 | 출처·사용 범위·해시·정답 전사와 구간을 포함한 manifest. 자료 확보 전 관련 시험은 미실행 |
| 2단계 | native decode가 취소 요청 이후 언제 반환하는지, 모델 컨텍스트를 언제 해제·재사용할 수 있는지 | decode 도중 Stop/Pause/재시작 실험. 캡처 중단 시간, decode 반환 시간, 다음 작업 시작 시간을 별도로 기록 |
| 3단계 | 페이지 단위 history snapshot을 읽는 동안 번역/이벤트가 갱신될 때의 일관성 | T01-02a: version 불일치 시 첫 페이지 재시도·페이지 사이 번역 갱신 fixture PASS. T01-02b: seq/로컬 고갈·301개 페이지 복구 IPC/C# smoke PASS; 실제 UI 렌더링은 후속 |
| 3단계 | rolling buffer가 덮어써져도 대기 중인 final 작업의 PCM이 유지되는 소유권과 메모리 상한 | immutable 복사·고정 버퍼 등 선택한 방식과 총 예산. decode 지연/queue drop 시 잘못된 음원이 전달되지 않는 테스트 |
| 3단계 | Pause/Stop/epoch 변경으로 취소된 번역의 기존 `pending` 기록을 어떤 terminal 상태로 바꾸는지 | T01-02a: Interrupted/EpochChanged 사유로 skipped; 확정 원문 유지·늦은 응답 거부 fixture PASS. 실제 HTTP/IPC 연결은 후속 |

## 이번 검토의 근거와 한계

- 원본 명세에는 큐 상한, epoch 검증, macOS callback 버퍼 수명, 무음 중 packet 중단 watchdog이 이미 있다. 이 규칙들을 새 요구사항으로 중복 생성하지 않고 관련 단계의 검증에 반영한다.
- [Avalonia 공식 지원표](https://docs.avaloniaui.net/docs/supported-platforms)는 현재 macOS 15를 Tier 2로 분류한다. macOS 15 지원 목표는 유지하되 선택한 Avalonia 버전과 실제 OS 조합을 0단계 기록 및 실기기 시험 대상으로 둔다.
- [whisper.cpp 공개 C 헤더](https://raw.githubusercontent.com/ggml-org/whisper.cpp/master/include/whisper.h)에는 abort callback과 동일 컨텍스트 동시 사용 제한이 명시돼 있다. 이는 API 존재의 근거이며 취소 응답 시간이나 재시작 안전성의 실측 근거는 아니다. 실험에서는 정확한 commit을 고정한다.
- 이번 검토는 문서 대조와 일부 공식 자료 확인이다. 계획 최초 검토 당시 SDK·빌드·모델·실측은 미확인이었다. 이후 실행 결과는 STATUS.md에 별도로 기록한다.

## M1 실행 단위 보완 (2026-09-30)

T02-02d에서 모델 없는 새 worker/장치별 시작 probe를 구현했다. 62회 중 Initialize timeout 1회는 ASR/VAD 없이 발생했고, Failed 뒤 제어 응답·join 전 재시작 거부·정상 종료를 확인했다. 나머지 61회 시작 성공으로 원인 해결을 주장하지 않는다. [추가 근거](evidence/T02-02d-windows-capture-startup.md). 다음 진단 UI는 캡처 진행/실패·pending join·소유 worker 종료/재연결을 표시하면서 원문 history·오버레이를 연결한다. 제품 session/Pause/품질 수용은 별도 계약을 유지한다.

2026-10-01 T02-02c 보완: 제품 UI 연결 전, 간헐적 Opening 대기의 실패 처리와 재현을 우선했다. 10초 진단 deadline·API phase/경과 시간·실패 checkpoint를 구현하고 STA 및 shared/event-driven Initialize 인수를 보정했다. 무음 새 worker의 Initialize 대기는 보정 뒤에도 재현돼 시작 안정성 gate는 미통과다. 다음 순서는 모델과 분리한 cold-start·다른 render 장치 비교, 제품 session/Pause 및 UI 오류/worker 복구·원문 history/오버레이 연결이다. [실측과 미해결 범위](evidence/T02-02c-windows-capture-startup.md)를 따른다.

T01-01은 a(정규화·sample 시간축·유한 PCM 소유권)와 b(VAD·발화 구간·watchdog)로 나눈다. a와 b의 결정론적 코어를 구현했다. 코드/fixture 범위와 남은 어댑터 책임은 [오디오 코어 계약](AUDIO_CORE.md)과 [VAD 계약](VAD_CORE.md)을 따른다. 실제 Silero 모델/state/context는 T02-01b 파일 경로에 연결했다. Windows live capture의 진단 통합은 T02-02b에서 확인했으며 제품 UI/Mac 수용은 후속이다. T01-02는 a(상태·작업 큐·history/번역 terminal 계약)와 b(worker 전달·유한 event 큐·IPC snapshot)로 나눈다. a와 b를 구현하고 Windows mock core/IPC로 검증했다. [상태·큐 계약](PIPELINE_CORE.md)과 [worker 전달](WORKER_DELIVERY.md)을 따른다. T02-01a는 실제 Whisper owner·비동기 파일 loader·ASR-only history와 epoch 취소/재시작을 Windows CPU 파일 진단으로 연결했다. [계약/범위](WORKER_ASR.md). T02-01b는 동의받은 고정 Silero v6.0/ORT 1.22.0 CPU로 probability/state reset·final 파일 분할과 ASR/history를 연결했다. [범위·품질 보류](WORKER_VAD.md). T02-02a는 WASAPI owner·bounded packet/frame 큐·정규화와 worker 제어를 연결했다. [범위](WORKER_CAPTURE.md). T02-02b는 QPC/session sample 시작점·gap·epoch와 지속 VAD state를 연결하고 final snapshot을 Whisper/history에 전달했다. [live 계약](WORKER_LIVE_ASR.md). 다음은 제품 session/Pause 계약과 UI 원문 history·오버레이 연결이다. 장치 notification/실제 전환·분리 수용은 별도 후속이다. immutable snapshot을 PCM 복사 pool 최대 4슬롯으로 구현했으며, epoch 변경에도 같은 pool을 재사용한다. history는 버전 변경 시 첫 페이지부터 재시도하고, 취소된 번역 pending은 사유 있는 skipped로 종료한다. T01-02b에서 IPC 페이지/seq 복구를 C# 클라이언트까지 연결했다. 실제 UI history 렌더링과 native 어댑터는 후속이다. M0 Mac gate 미통과와 독립 pure-core 진행을 구분한다.


## T02-02e 진단 UI 연결 (2026-10-01)

Windows live 진단에 출력 장치·언어 선택, 캡처 시작/정지, 실패 phase·pending join·소유 worker 종료/재연결, 원문 history·5초 오버레이를 연결했다. 현재 ASR epoch와 적용된 source revision으로 표시를 제한한다. 저장소 검사와 live 창/worker 연결 로그는 통과했으나 실제 클릭·원문 렌더링 수용은 미검증이다. 다음은 [수동 확인](LIVE_UI.md)과 제품 session/Pause 계약이며 간헐적 Initialize 문제의 해결을 주장하지 않는다.

## T02-02f UI 제어 응답 경로 보완 (2026-10-01)

상태/history 조회가 정지 버튼과 원문 만료를 지연시키던 경로를 분리했다.
사용자 명령이 조회를 취소하고, 취소된 snapshot은 화면에 적용하지 않는다.
Start/Stop 수락과 실제 상태를 분리하며 원문 만료는 UI tick에서 진행한다.
반복 창 닫기는 worker 정리를 건너뛰지 않는다. [계약·확인 범위](LIVE_UI.md).
실제 원문 화면과 조작 지연 수용은 미검증으로 유지한다. 다음 구현 단위는
제품 session/Pause다. UUID 제품 ID와 진단 u64 ID의 구분, session 내 segment ID
증가, Pause/Resume epoch와 capture/VAD join 및 native 반환의 관계를 먼저
고정한 뒤 worker 명령·UI를 연결한다.

## T02-03a UUID session 제어 (2026-10-01)

Windows UUID 제어 어댑터와 UI 시작/Pause/Resume/Stop을 연결했다. history는 retain만 지원하며 새 session은 새 ring을 사용한다. Resume은 capture/VAD join, Stop Idle은 native 반환까지 확인한다. mock IPC와 실제 CPU loopback final 3개 검증을 완료했다. [계약](SESSION_CONTROL.md) · [근거](evidence/T02-03a-windows-session-control.md). 다음은 전체 UUID source/history wire·시작 UTC/세션 export 시간축, native full 중 Pause/Resume·화면 수용이다. partial·경계 정합·번역·Mac도 남아 있으며 제품 gate는 미통과다.

## T02-03b UUID history·native Pause (2026-10-01)

source/history에 UUID와 세션 상대 시간 메타데이터를 추가하고 C# 검증·UI UUID 필터를 연결했다. 보존 history session+현재 session으로 대응 정보의 수명을 제한했다. 빈 세션 1,001회 IPC 및 native full 관측 후 Pause/Resume를 확인했다. 실제 시작 timeout 1회를 그대로 보존한다. [범위·근거](evidence/T02-03b-windows-session-history.md). 다음은 세션 시작 UTC·history 내보내기/시간축, 전체 제품 wire 및 화면 수용이다. 시작 안정성·partial/경계 정합·번역·Mac gate는 유지한다.

## T02-03c UTC·원문 export·시작 실패 관측 (2026-10-01)

세션 시작 UTC와 TXT/확정 원문 SRT 저장을 연결했다. Paused/Idle에서 owner/native
정리가 끝난 뒤 UUID별 남은 snapshot을 명시적으로 저장한다. Rust 94개·C# IPC,
native CPU 및 실제 두 세션 파일 저장을 확인했다. [계약](HISTORY_EXPORT.md) ·
[근거](evidence/T02-03c-windows-history-export.md). 시작 timeout은 근본 원인 미확정이다.
Initialize 전에 endpoint/mix/thread ID를 기록하고 모델 없는 실패 probe에 별도 helper
덤프 수집을 추가했다. 이번 장치별 16회는 성공해 실패 스택을 얻지 못했다.
[다음 증거 수집·분석](evidence/T02-03c-windows-startup-diagnostics.md).
다음은 실패 스택으로 대기 지점 확인 및 UI 실제 저장/조작 수용이다. 전체 제품 wire,
partial/overlap 정합·번역·장치 전환·macOS gate는 유지한다.

## T02-03d 세션별 기록 삭제 (2026-10-01)

사용자 요청으로 시작 timeout의 근본 원인 분석은 후속 과제로 보류한다.
안정된 Paused/Idle에서 UUID별 메모리 기록을 삭제하는 명령과 UI 확인 창을 연결했다.
현재 세션의 UTC/시간축/다음 구간 ID와 다른 세션·저장한 파일을 보존한다.
Rust 95개·C# IPC 및 실제 Windows 원문 기록 삭제를 확인했다.
[계약](HISTORY_EXPORT.md) · [검증·한계](evidence/T02-03d-windows-history-clear.md).
다음 구현은 M2의 opt-in 부분 전사다. 기존 최신 partial 1개/확정 우선/단일 native
예약과 동일 segment revision을 live VAD에 연결하고, Pause/Stop의 늦은 결과 거부와
UI의 인식 중 표시를 먼저 구현한다. 기본 final-only 후보와 전체 품질 gate는 유지한다.

## T02-04a 선택형 부분 전사 (2026-10-01)

기본 false 설정과 live VAD 부분 요청, 동일 제품 구간의 revision 갱신,
확정 우선 취소·native 반환 전 예약, 유한 언어 metadata와 UI 인식 중 표시를
연결했다. Rust 98개·C# IPC 및 실제 CPU loopback 부분 6개 revision→확정 3개,
Pause/Resume/Stop/export/clear가 통과했다. 첫 시작 timeout은 보존했다.
[계약](WORKER_PARTIAL_ASR.md) · [근거](evidence/T02-04a-windows-partial-asr.md).
다음은 기존 M2 계획의 overlap/무음 결과 정합이다. 화면 수용·자연 음성 품질,
번역·macOS·시작 안정성은 후속이며 timeout 원인 분석 보류를 유지한다.

## T02-04b 경계·빈 결과 정합 (2026-10-01)

명시된 continuation/오디오 겹침/native span timestamp와 정확한 prefix-suffix로
중복을 보수적으로 제거하고, NoSpeech/OverlapOnly를 사유 있는 skip으로 연결했다.
Rust 105개·C# IPC/native CPU 빌드가 통과했다. 실제 8초 분할/0.608초 겹침과
확정 결과를 확인했지만 제거 span은 0개였다. 무음 단계에 입력이 있어 해당 gate는
미통과다. [계약](ASR_RECONCILIATION.md) · [근거](evidence/T02-04b-windows-asr-reconciliation.md).
후속 T02-04c에서 token 시간/byte 정합과 통제된 파일 무음 검증을 구현했다.
실제 반복 보존 fixture와 600초 파일 PCM 무음 검증이 통과했다. 실제 live dedup,
UI/자연 음성/두 OS 수용 gate는 그대로 유지한다.

## T02-04c 및 T03-01a 번역 계약 착수 (2026-10-01)

[Token 정합](ASR_TOKEN_ALIGNMENT.md)과 [실행 근거](evidence/T02-04c-windows-token-alignment.md)를
정리하고 독립적인 M3 번역 계약 구현을 진행했다. `crates/translation/`은 기존
TranslationJob의 키/deadline을 보존하면서 로컬 주소, 모델 목록, source/context
예산, JSON prompt와 응답의 완결성/길이/tool 검사를 구현한다. 신규 fixture 8개를
포함한 Rust 118개·C# IPC가 통과했다. [계약](TRANSLATION_CONTRACT.md).
후속 T03-01b에서 proxy/redirect 없는 bounded HTTP owner·모델 선택/연결 진단을 구현했다.
이어 final-only dispatch·취소/epoch·history, UI 번역 표시를 연결한다.
M2/M3 품질과 양 OS E2E gate의 완료를 뜻하지 않는다.

## T03-01b 로컬 HTTP owner (2026-10-01)

전용 스레드/단일 실행 예약과 취소·join, 2초 연결 제한·기존 deadline 공유,
최대 한 번 재시도·응답 byte 상한을 연결했다. Rust 130개/C# IPC PASS이며 실제
Qwen 로컬 서버의 20개 응답을 확인했다. 귀환 조건 오역/누락은 품질 보류로 기록했다.
[실측·범위](evidence/T03-01b-windows-translation-http.md).
다음 T03-01c는 기존 pipeline의 final-only 번역 job을 HTTP owner에 전달하고,
전체 키로 완료를 적용해 history·실패/skip/취소 terminal과 원문 보존을 확인한다.
서버 설정/모델 조회 진단 명령을 worker에 노출하고, UI 번역 연결은 그 뒤에 진행한다.

## T03-01c worker 번역/history (2026-10-01)

번역 설정/모델 조회·disable과 final-only HTTP dispatch, 전체 키 완료 적용을
연결했다. Rust 138개·C# HTTP/IPC와 실제 Whisper CPU 파일→Qwen→history가
통과했다. 영어 3개 번역 완료·한국어 1개 bypass; 조건/위치 오역으로 품질은 보류다.
[실행·제한](evidence/T03-01c-windows-worker-translation.md).
다음 T03-02a는 데스크톱의 로컬 서버 설정/모델 준비 표시, 번역 history·오버레이다.
서버 실패 시 원문 표시를 유지하고 applied source revision과 번역 request ID로
갱신을 제한한다. UI 실제 조작·live E2E·Mac·품질 수용은 별도로 판정한다.

## T03-02a 데스크톱 번역 표시 (2026-10-01)

로컬 서버 주소/실제 모델 목록 선택, 준비·실패 상태와 번역 끄기를 연결했다.
번역 history와 원문+한국어 오버레이를 연결하고 현재 UUID/epoch/applied revision과
request ID로 표시를 제한한다. 실패 시 원문 유지, 늦은 번역의 5초 수명 연장 금지를
14개 C# 표시 검사로 확인했다. Rust 138개·C# HTTP/IPC·native CPU/VAD 빌드 PASS.
실제 UI 조작/화면·live E2E·품질·Mac 수용은 미검증이다.
[실행 안내](DESKTOP_TRANSLATION.md) · [근거](evidence/T03-02a-windows-desktop-translation.md).
다음 T03-02b는 서버 복구/다중 모델 선택 진단과 유한 번역 표시 프리셋이다.
