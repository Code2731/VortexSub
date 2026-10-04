# EXAONE 문맥 전달 입력 비교

2026-10-02, Windows / RTX 3080 10 GB. RI-01/02/06.
[greedy·입력 계약 확인](exaone-greedy-contract-windows-20261002.md)에 이어
현재 문장과 참고 문맥의 구분/제거 입력을 한 번 보정해 비교했다.
새 자산은 다운로드하지 않았다. 앱 연결이나 기본 전환은 하지 않는다.

## 고정한 비교 구성

| 구성 | 실제 입력 |
|---|---|
| Qwen baseline | 기존 영어 system + 원문/문맥 JSON |
| EXAONE existing | 공식 identity system + 기존 영어 user 지시와 앞선 문맥 |
| EXAONE separated | 같은 identity system + 한국어 보존 지시, `[참고 문맥]`/`[현재 자막]` 블록 |
| EXAONE source-only | separated와 같은 지시/블록, 참고 문맥은 `(없음)`으로 대체 |

separated는 지시 언어와 입력 형식을 함께 바꾼 구성이다. existing과의 차이를
문맥 위치 하나의 효과로 해석하지 않는다. source-only는 바뀐 한국어 지시 아래에서
문맥 유무를 비교한다. 기존 영어 입력에서 문맥만 뺀 구성은 이번 범위에 없다.
모든 EXAONE 구성은 official embedded template와 EOS를 유지했다.

`probe-translation-context.py/.ps1`에 `--exaone-input` / `-ExaoneInput`
(`existing|separated|source-only`)을 추가했다. 다른 모델에 이 옵션을 적용하면
거절하며 기존 입력은 기본값으로 유지한다. 요청 fingerprint와 runtime에 실제
variant를 기록한다. 비교 도구는 variant별 익명 CSV 매핑과 시간 집계를 저장한다.

## 평가 자료와 실행

* 기존 66항목: regression 18, calibration 48. 이전 heldout 48항목은 결과를
  보고 입력을 수정했으므로 calibration으로 재분류했다. 과거 결과의 복사본은 보존했다.
* 새 heldout 24항목: 영어/일본어 각 6개 새 원문 × 문맥 없음/관련 문맥.
  각 언어에서 부정·조건·대상·숫자·정정·문맥 의존을 한 문장씩 다룬다.
* 두 수정 입력을 먼저 고정하고 새 결과를 확인했다. reference/판정 메모는
  모델 입력에 넣지 않았다. 결과를 보고 이번 입력을 다시 조정하지 않았다.
* authored full source다. 자연 음성/partial trace/모국어 화자의 자료 검증은 아니다.
* 90항목 × 4구성 × 3회 = 1,080응답, 응답 실패 0. 새 heldout은 구성당 72응답
  (24 case, 12개 원문)이다. 반복 호출을 독립 원문으로 세지 않는다.
* greedy temperature 0, top_k 1, top_p 1, min_p 0, repeat_penalty 1,
  seed 42, max_tokens 256. 모델을 하나씩 로드하고 구성/fixture 순서를 바꿨다.
  각 실행의 warmup은 시간 집계에서 제외했다. 게임 상태와 총 GPU 메모리는 미측정이다.
* EXAONE 810응답의 계약 확인을 통과했다. 라운드당 existing/separated는 각각
  90개 고유 요청, source-only는 문맥 짝이 같아져 42개 고유 요청이다.
  같은 fingerprint의 렌더링/토큰화는 재사용하되 chat 입력 토큰 수는 응답마다 확인했다.

실행한 명령:

```powershell
./models/tabby/venv/Scripts/python.exe -X utf8 scripts/compare-exaone.py --sampling greedy --verify-input-contract --context-ablation --fixtures benchmarks/translation-exaone-ablation-fixtures.json
```

결과는 Git 제외 `benchmarks/results/exaone-comparison-20261002-161117-d231af/`.
12개 run의 입력/출력/계약/로그, 1,080행 익명 CSV와 private mapping을 보존했다.
`summary.json`은 전체 시간 집계이며 `partition-timing.json`은 원본 report의 기간을
partition별로 따로 집계한 후처리 결과다. 전체 독립 사람 품질 annotation은 PENDING이다.

## 새 heldout의 HTTP 기간

초 단위. 각 셀은 중앙값 / p95(nearest-rank), 각 언어·구성마다 36응답이다.
원문은 언어별 6개이며 문맥 짝으로 12 case다. HTTP 응답 수신 직후 측정이 끝나므로
계약 파일 쓰기는 포함하지 않는다. template/tokenize/loading/warmup도 제외한다.
음성→자막 전체 지연을 측정한 표가 아니다.

| 구성 | 영어→한국어 | 일본어→한국어(탐색) |
|---|---:|---:|
| Qwen baseline | 0.1880 / 0.2340 | 0.1720 / 0.2340 |
| EXAONE existing | 0.0940 / 0.1250 | 0.1090 / 0.1410 |
| EXAONE separated | 0.1015 / 0.1410 | 0.0935 / 0.1570 |
| EXAONE source-only | 0.0930 / 0.1100 | 0.0935 / 0.1250 |

세 EXAONE 구성의 출력 길이·토큰 수도 다르다. 처리시간이 짧아도 누락/오역을
품질 개선으로 취급하지 않는다. 서로 다른 라운드의 시간 차이를 입력만의 인과 효과로
확정하지 않는다. 반복 출력 일치율도 품질 정확도가 아니다.

## 관측한 의미 오류와 부분 개선

아래는 에이전트의 원문 대조다. 전체 수동 오류율이나 blind 사람 평가 결과가 아니다.

* 보정용 silver key 문맥: source-only는 3/3회 대명사를 유지해 색 변경을 피했다.
  separated는 은색으로 고쳤지만 “당신은 은색 열쇠를 쥐고 있습니다”라는
  이전 문맥을 3/3회 덧붙였다. 색만 맞아졌다고 입력 보정 성공으로 세지 않는다.
* 보정용 until-return 문맥: source-only는 3/3회 “나의 귀환까지 문을 열지 마세요”로
  번역했다. separated는 이전 문맥 번역 뒤 금지 지시를 덧붙여 혼입이 남았다.
* 새 `exaone-holdout-en-01-none`: “Do not restart the generator”를 두 수정 입력
  모두 3/3회 **“발전기를 끄지 마세요”**로 바꿨다. existing은 다시 시작하지 말라는
  지시를 유지했다. 문맥이 없는 항목에서도 새 행동 오류가 생겼다.
* 새 `exaone-holdout-en-06-related`: separated는 “대피소에 갈 때까지 젖지 않게
  유지하라”를 3/3회 **“물을 아껴 사용하세요”**로 바꿨다. existing/source-only는
  건조 유지와 대피소까지의 관계를 유지했다.
* 새 `exaone-holdout-ja-10-none`: existing도 붕대를 갑옷으로 바꿨다. 수정 입력은
  여기에 “4개 미만 사용하지 않고”라는 새 부정까지 더해 3/3회 수량 지시를 바꿨다.
* Qwen도 새 자료에서 fewer-than-six→6개 이하, copper→철, 일본어 금지 누락
  사례가 있었다. 현재 Qwen을 충분히 정확한 모델로 확정하는 실험은 아니다.

두 수정 입력은 대조군에 없던 새 행동/조건 오류가 있으므로 채택하지 않는다.
빠른 기존 EXAONE 입력도 앞선 문맥 혼입과 이번 자료의 의미 오류가 남아 있어
기본 전환을 보류한다. 이것은 이번 모델·양자화·입력·런타임 구성의 판정이며
EXAONE 제품군 전체나 다른 규모/정밀도의 성능을 일반화하지 않는다.

## 다음 단계와 검증 경계

EXAONE의 반복적인 prompt 보정은 이번 한 번으로 종료한다. 계획의 다음 후보
Hy-MT2의 자산/입력 계약 확인으로 진행한다. 새 다운로드는 기존 동의 규칙을 따른다.
해당 후보에 맞게 입력을 튜닝하면 이번 heldout도 calibration으로 바꾸고 새 검증을
준비한다. 설치만 된 EXAONE을 앱의 선택 가능한 검증 완료 모델로 표시하지 않는다.

Python AST, PowerShell parser, JSON 파싱/고유 ID 수를 확인했다.
단위/통합 테스트·앱·캡처·partial trace·자연 음성·동시 게임·macOS는 실행하지 않았다.
