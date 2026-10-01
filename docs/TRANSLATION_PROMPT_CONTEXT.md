# 번역 프롬프트·참조 문맥 후보

## 목적과 계약

부분 번역의 미완성 원문 보류와, 완전한 원문을 받은 번역 모델의 의미 오류를
구분한다. Laya는 보류 판단의 후속 후보이며 이번 작업에는 도입하지 않았다.

기존 방식은 system 지시와 현재 원문/이전 원문을 합친 user JSON이다.
후보는 현재 원문만 출력하도록 지시를 구체화하고, 이전 원문을 별도 user JSON에
넣은 뒤 현재 원문/언어 JSON을 마지막 user 메시지로 전달한다. 참조 문맥은
system 메시지나 이전 assistant 답변으로 취급하지 않는다.

최근 2개 원문·600자 문맥/2,000자 전체 입력 예산, 원문 보존, 같은 언어 bypass,
request identity, 남은 deadline, 취소와 stale 적용을 유지한다.
temperature 0.2·max_tokens 256·단일 HTTP 요청이며 새 모델이나 추가 추론 호출은 없다.
프롬프트 분리가 의미 정확성을 보장하지는 않는다.

## 비교 재현

```powershell
./scripts/probe-translation-context.ps1 -Rounds 3
./scripts/probe-translation-context.ps1 -Rounds 3 -Profiles baseline,strict,isolated
./scripts/probe-translation-context.ps1 -Rounds 1 -OwnerCheck
```

`benchmarks/translation-context-fixtures.json`의 12개 영어/일본어 작성 원문을
이전 문맥 없음/있음으로 짝지었다. `translation-probe --prepare <model-id>
<fixtures.json> <requests.json>`은 생산 prepare 계약을 모델/HTTP 없이 export한다.
원래 system 문구는 `translation-prompt-profiles.json`에 고정해 비교 기준을 유지한다.
집계 이름 `production`은 opt-in `prepare_with_policy(IsolatedContext)`가 export한
실제 후보 요청이다. 기본 활성화를 뜻하지 않는다. `-OwnerCheck`는 두 정책의
실제 Rust HTTP owner를 각 fixture 1회씩 추가 호출한다.
전체 원문 HTTP 비교이며 전사·캡처·화면 지연을 측정하지 않는다.

후보 `strict`는 지시만 변경, `isolated`는 문맥 메시지도 분리한다.
`readable`은 언어명 명시, `examples`는 짧은 조건 번역 예시,
`korean`은 한국어 지시문이다. 마지막 세 후보는 진단용이며 채택하지 않았다.
실행별 fixture/profile/request/결과/runtime은 Git 제외 출력 폴더에 저장한다.
3회 동안 후보 순서를 회전하고 원문 순서를 교대한다. 자동 의미 채점은 없다.

## 판단

후속 의미 통제 비교는 `-Fixtures benchmarks/translation-semantic-controls.json`로
실행한다. 6개 원문을 문맥 없음/관련/무관으로 비교하며 `-Profiles plain`은 일반
텍스트 전달 진단 대조군이다. 앱에는 연결하지 않는다. [162회 결과와 판단](evidence/translation-semantic-controls-windows-20261002.md).

영어 문맥 혼입과 일부 명령 반전은 줄었지만 `until I return` 오역, 일본어 정정
문맥 혼입, 어휘/숫자 오류와 일부 새 오류가 남았다. 전체 품질 gate는 false다.
[측정·검토·적용 범위](evidence/translation-prompt-context-windows-20261002.md)를 따른다.
Laya의 의미 완결성 판단만으로 완전한 원문의 번역 오역이 해결된다고 보지 않는다.

## 실험 실행

앱의 **로컬 번역 서버** 영역에서 **이전 문맥 분리 번역 · 실험 기능 / 기본 끔**을
선택한다. 서버 Ready 상태면 기존 모델로 즉시 재설정하며, 연결 전 선택은
**서버 연결 / 모델 조회** 또는 **선택 모델 적용** 시 반영된다.
세션 종료와 작업 정리 뒤 변경할 수 있고 일시정지/실행 중에는 비활성화한다.
적용 상태를 체크박스 아래에 표시한다. 요청 실패 시 즉시 변경은 이전 선택으로
돌린다. 앱/worker를 다시 실행하거나 기록을 지울 필요는 없다.

```powershell
run-live-cuda-fast.bat -IsolatedTranslationContext -CaptionTiming
./scripts/probe-preview-risks.ps1 -Rounds 3 -IsolatedTranslationContext
```

기본 off다. 실험은 기존 partial/padding 설정과 별도로 선택할 수 있으며
SupportedPreview를 켤 필요가 없다. worker의
`--experimental-isolated-translation-context`는 `--diagnostic-translation`을 요구한다.
정책은 owner 생성 시 고정하고 재설정/세션 간 같은 정책을 유지한다.
기본 `prepare`/`Owner::new`는 원래 프롬프트와 합친 user JSON을 그대로 사용한다.
IPC `configure_translation.isolated_context`는 선택 boolean이며 생략하면 현재
정책을 유지한다. capability `isolated_translation_context`가 없는 이전 worker에는
앱이 새 필드를 보내지 않고 체크박스를 비활성화한다.
