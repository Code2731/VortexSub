# 번역 변경 진단 — P2.1

번역의 변화와 읽기 카드 적용을 구분해 기록한다. 진단은 번역 요청, 큐, 보호 시간이나
표시 내용을 바꾸지 않는다. 앱의 기존 **자막 지연 기록** 옵션으로 수집한다.

## 분류

| 종류 | 기준 |
|---|---|
| `Initial` | 비교할 이전 번역이 없는 첫 후보/카드 적용 |
| `SegmentTransition` | 다른 segment로 이동 |
| `SameUnitRevision` | 같은 session/epoch/segment·부분 번역 시작 위치의 새 요청 |
| `UnitTransition` | 보호 원문이 유지된 상태에서 다음 부분 번역 시작 위치로 이동 |
| `SourceUnitReset` | 시작 위치가 되돌아가거나 보호 원문이 무효화된 뒤 위치 변경 |
| `FinalRestoration` | 부분 번역에서 전체 확정 번역으로 전환 |
| `FinalRevision` | 같은 확정 범위의 새 요청 |
| `PreviewRestart` | 확정에서 부분으로 되돌아간 관찰; 정상 흐름으로 가정하지 않음 |
| `UnidentifiedRevision` | 구형 부분 결과에 단위 정보가 없어 구분 불가 |

부분 단위 위치는 `translation_prefix.Length - translation_source.Length`이며 UTF-16
인덱스다. Rust byte offset이나 모델 token offset과 섞지 않는다. 시작 위치는 의미를
완전히 식별하는 ID가 아니다. 원문 보호 구간 반박 여부도 같이 봐야 한다.

## 앱 기록

`target_change_observed`에는 `Candidate`(history 후보), `Displayed`(카드에 적용),
`SourceGuardInvalidated`(기존 원문 prefix가 새 원문에 없는 경우)를 기록한다.
원문·번역 문자열은 저장하지 않는다. session/epoch/segment/revision/request,
단위 위치, 공통 접두부 길이, 제거/추가 글자 수와 최초 관찰 시각만 보존한다.
기존 bounded 기록 큐와 dropped 알림을 사용한다.

`same_unit_removed_utf16`은 같은 단위 수정에서만 숫자이며 단위 전환/확정 복원에서는
null이다. 일반 교체 글자 수와 별도로 집계한다. 동일 요청 재조회와 역순 요청은
다시 세지 않는다. session/epoch 전환과 Pause/Stop은 상태를 비운다.

후보 비교는 최신 하나, 카드 비교는 두 카드 각각 하나만 보존한다. 카드가 새 단위로
생성되면 `Displayed.Initial`이므로 후보의 `UnitTransition`과 일대일 분류가 아니다.
최초 카드 적용은 렌더링 완료 시각이 아니다. 옵션을 중간에 켜거나 dropped가 있으면
전체 변화 이력을 복원할 수 없다.
`Displayed`는 유효 번역을 카드에 적용한 관찰이다. 카드 삭제/읽기 만료 자체를 이
변경량에 합산하지 않으므로 전체 화면 변화 횟수로 해석하지 않는다.

```powershell
models/tabby/venv/Scripts/python.exe scripts/summarize-caption-timing.py <caption-timing.jsonl>
scripts/compare-fixed-translation.ps1 -Trace <source-trace.json> -Rounds 1
```

재생은 동일한 `CaptionTargetChanges`, `CaptionPresentation`, `CaptionDeck`을 사용한다.
`target_observations`와 `first_deck_applied_s`를 보존하고 후보/카드별 종류와 동일 단위
수정량을 집계한다. 실제 worker/HTTP를 사용하지만 MOCK ASR admission, 진단 polling
시계이며 Avalonia·화면 출력은 없다.

## 해석 제한과 다음 단계

원문 prefix 무효화는 어휘 변화 신호다. 긍정→부정 교정뿐 아니라 문장 재배열도
발생시킨다. `semantic_contradiction=UNASSESSED`와 검토 CSV의 수동 판정 칸을 둔다.
문자 합의나 수정량을 번역 정확도·신뢰 확률로 표시하지 않는다.

P2.2에서 일반적인 동일 단위 변경을 줄이는 정책을 구현하더라도 첫 번역, 원문 반박,
최종 전체 번역은 별도로 다뤄야 한다. 조건/부정 교정을 단순히 동결해서는 안 된다.
기존 보호 시간을 변경하거나 지연 감소를 주장하려면 별도 비교가 필요하다.
