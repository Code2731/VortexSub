# 빠른 모드 첫 요청과 임시 번역 보류 이유 (2026-10-02)

## 사용자 변경 전 관측

Git 제외 `logs/caption-timing-20261002-002617-d8aac93dabde4781a0607ec5e6af7900.jsonl`을
읽었다. 부분 접수 282회, 부분 원문 273회, 임시 번역 요청 108회다.
번역이 나온 39개 segment 중 35개는 확정 전 첫 출력이었다.

| 단계 | 중앙값(초) | 최대(초) |
|---|---:|---:|
| 첫 원문→첫 안정 prefix(36개) | 0.514483 | 1.594060 |
| 전사 owner 전달→완료(318개 적용) | 0.094305 | 0.530496 |
| 번역 owner 전달→완료(136개 적용) | 0.211647 | 0.597010 |
| UI 수신→visible Deck(115개) | 0.033280 | 0.046871 |

39개 segment의 첫 관측 접수→첫 적용 번역 중앙값은 0.867117초다.
첫 접수의 샘플 음성 길이는 44개 기준 중앙값 0.8초다. 서로 다른 시계와
표본이므로 음성→화면 전체 지연으로 합산하지 않는다. 일부 segment의 안정
prefix→첫 번역 전달은 1.014~1.152초지만 기존 로그에 보류 판정이 없어
어휘 규칙/최소 길이/요청 간격 중 원인을 단정하지 않는다. 로그 drop은 0이다.
ignored ASR 7개/unapplied 번역 11개는 적용 성능 통계에서 제외된다.
다른 입력/모드 실행과 처리 중앙값을 속도 개선율로 비교하지 않는다.

## 변경

빠른 VAD 요청 간격 0.5초를 사용하는 stream에서 첫 부분 요청 조건을
0.8→0.5초로 바꿨다. 512-sample 프레임의 실효값은 0.512초이며 기본
stream은 0.8초다. 기존 두 전사 안정 prefix, 영어 단어 경계·미완성 조각,
HTTP 단일 실행·확정 우선·임시 간격 0.5초와 자막 읽기 규칙은 유지한다.

부분 원문 이벤트에 고정 `preview_hold_reason`을 추가했다. 어휘 판별은
기존 규칙의 boolean을 이름 있는 판정으로 바꿨고, 원문 후속 처리의
NoStablePrefix/확정 우선/Cadence도 관측한다. 일반 history/export 텍스트
형식은 바꾸지 않았다. UI writer는 알려진 값만 선택한다.
요약은 전체와 segment 첫 번역 전의 판정 횟수를 보여준다. 이것은 상태
갱신 관측이며 연속 보류 시간이나 모든 보류 해제 이벤트의 측정이 아니다.

## 확인과 한계

Windows `scripts/check.ps1` PASS: Rust 154개, C# 표시 39개 assertion·HTTP/IPC·빌드.
`tests/test_caption_timing_summary.py`의 Python fixture 4개 PASS.
`scripts/build-model-probe.ps1 -Backend cuda -Package echosub-worker -Vad -Offline` PASS.
`cargo fmt --all -- --check`, `git diff --check` PASS.

VAD fixture에서 빠른 첫 요청이 기본보다 9프레임(0.288초) 빠르고 확정 PCM
범위는 같음을 확인했다. 숫자/조건/관사 조각의 보류 이유와 보완 후 요청,
불안정 조건 continuation의 보류를 확인했다. Rust IPC는 NoStablePrefix,
C# logger는 고정 이유 기록과 임의 문자열/원문 제외를 확인했다. 첫 check의
export fixture 초기화 누락을 보완한 후 전체 재실행은 통과했다.
Git 제외 출력: `benchmarks/results/early-partial-check-20261002.log`.

실제 변경 후 캡처·HTTP 품질/첫 출력 지연·화면·게임·macOS는 미실행이다.
짧은 첫 입력은 전사 품질/수정·무음 판정과 추론 횟수에 불리할 수 있다.
첫 요청 시점의 fixture 차이는 첫 번역 가속 보장이 아니다. 새 자산은 다운로드하지 않았다.

다음은 같은 입력으로 첫 안정/번역 시점과 보류 판정을 비교한다. 기존 조건/
숫자 사례의 실제 HTTP 품질·수정 빈도 비교도 남아 있다.
