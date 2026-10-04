# EXAONE 입력 계약·greedy 대조

2026-10-02, Windows / RTX 3080 10 GB. RI-01/02/06.
[첫 비교](exaone-translation-windows-20261002.md)의 입력 계약과 sampling 의문을
확인했다. 새 모델/runtime는 설치하지 않았다. 앱의 기본 모델·옵션은 유지한다.

## 변경과 실행

`probe-translation-context.py/.ps1`에 `--sampling greedy` / `-Sampling greedy`와
EXAONE 전용 `--verify-input-contract` / `-VerifyInputContract`를 추가했다.
기존 실행의 기본값은 temperature 0.2다. greedy는 두 모델 요청에 temperature 0,
top_k 1, top_p 1, min_p 0, repeat_penalty 1, seed 42, max_tokens 256을 명시한다.
새 설정은 candidate 요청/fingerprint/runtime 기록에도 반영된다.

기존 공식 EXAONE GGUF와 llama.cpp 6047 (`952a47f4`)를 재사용했다.
Qwen/EXAONE을 교대하며 원문 66항목 × 3회 × 2모델 = 396응답을 얻었다.
6개 실행마다 warmup 1개는 시간 집계에서 제외했다. 서버는 하나씩 로드했다.

```powershell
./models/tabby/venv/Scripts/python.exe -X utf8 scripts/compare-exaone.py --sampling greedy --verify-input-contract
```

기존 WinGet 서버 읽기와 실행에는 제한 환경 밖의 접근이 필요했다.
최종 결과는 Git 제외 `benchmarks/results/exaone-comparison-20261002-085427-32aa73/`.
396행 익명 review CSV와 별도 mapping을 보존했다. 사람 품질 annotation은 PENDING이다.
초기 greedy 396응답(`084943-e2c7fc`)은 계약 결과 파일 쓰기가 시간에 포함됐다.
HTTP 응답 수신 직후 타이머를 종료하도록 수정하고 396응답을 다시 실행했다.
아래 시간표는 후자의 결과만 사용한다. 두 실행 모두 원본을 보존했다.

## 입력 계약 결과

EXAONE 매 라운드 66개 요청에서 다음을 모두 통과했다.

* embedded GGUF template를 Jinja로 로컬 렌더링하고 서버 `/apply-template` 결과와
  각각 `/tokenize` (`add_special=true`, `parse_special=true`)로 토큰화했다.
  두 토큰열이 정확히 일치했다.
* assistant generation 경계와 context 4096 이내 입력 길이를 확인했다.
* 모델 architecture는 `exaone`, EOS ID는 361 `[|endofturn|]`다.
* BOS ID 1은 등록돼 있으나 metadata에 `add_bos_token`은 없었다.
  실제 렌더링/토큰화의 BOS 수는 0이며 chat usage의 prompt_tokens와 토큰 수가
  모든 요청에서 일치했다. BOS를 수동으로 더하는 변경은 하지 않았다.
* ChatML 대체는 없었고 응답은 모두 stop으로 정상 종료했다.

[설치 build의 공식 server 소스](https://github.com/ggml-org/llama.cpp/blob/952a47f4/tools/server/server.cpp)에서
`/apply-template`와 chat이 같은 chat parser를 사용하며 non-multimodal prompt는
special token 추가/파싱을 켜고 토큰화하는 경로를 확인했다.
실제 내부 chat token sequence 자체를 endpoint가 돌려주는 것은 아니다.
따라서 렌더링/토큰화 동등성·chat 입력 개수와 소스 경로를 확인한 결과이며,
모든 runtime 구현의 내부 동등성을 보장하는 검사는 아니다.

## HTTP 처리시간

초 단위, p95 nearest-rank. template/tokenize 검증·loading·warmup은 제외한다.
tokenize 요청이 서버의 추론 측정 밖에서 실행되므로 지난 라운드와의 속도 차이는
sampling만의 인과 효과로 해석하지 않는다. 음성→자막 지연은 측정하지 않았다.

| 언어 | 모델 | 응답/고유 case | 중앙값 | p95 | 최대 | 응답 실패 |
|---|---|---:|---:|---:|---:|---:|
| 영어 | Qwen3 4B | 117/39 | 0.156 | 0.234 | 0.250 | 0 |
| 영어 | EXAONE | 117/39 | 0.078 | 0.125 | 0.125 | 0 |
| 일본어 | Qwen3 4B | 81/27 | 0.141 | 0.188 | 0.203 | 0 |
| 일본어 | EXAONE | 81/27 | 0.078 | 0.109 | 0.109 | 0 |

평균 output tokens는 영어/일본어에서 Qwen 19.59/17.44, EXAONE 14.28/13.52다.
게임 실행 상태·총 GPU 메모리·열/부하 상태는 미측정이다.
같은 환경의 3회 출력이 동일했던 case는 Qwen 66/66, EXAONE 65/66이었다.
greedy+seed가 모든 환경의 완전한 결정론을 보장하거나 동일 출력이 정확성을
의미하는 것은 아니다.

## 오류 재현과 판단

원문과 결과를 에이전트가 대조한 아래 사례는 3/3회 반복됐다.
전체 독립 사람 평가나 전체 오류율은 아니다.

* `until-return-negative-related`: 현재 문장을 번역하지 않고 이전 문맥의
  “나는 다섯 분 후에 돌아올게요”를 출력했다.
* `before-return-negative-related`: 이전 문맥 번역 뒤 영어 현재 문장을 덧붙였다.
* `fresh-en-context-11-related`: silver key 문맥을 **금색 열쇠**로 바꿨다.
* `fresh-ja-negation-13-related`: 종이 울릴 때까지 다리를 건너지 말라는 지시를
  다리를 건너기 전까지 종을 울리지 말라는 지시로 바꿨다.

이번 관측은 템플릿/BOS 누락 또는 stochastic sampling만으로 오류를 설명할
근거를 제공하지 않았다. 입력 구분과 모델의 의미 처리 능력이 남은 원인 후보다.
고정 설정에서 반복되는 오역을 안정적인 좋은 번역으로 판정하지 않는다.
EXAONE은 빠르지만 현재 profile은 기본 채택을 보류한다. 일본어는 탐색 범위다.

다음 작업은 현재 문장/참고 문맥을 구분한 입력과 문맥 없는 입력의 제한된 비교다.
입력을 보정하면 이번 자료는 calibration으로 재분류하고 별도 새 heldout을 준비한다.
반복된 오류를 고친 것만으로 전체 품질 개선을 주장하지 않는다.
모델별 권장 sampling·partial trace·앱/자연 음성·macOS는 미검증이다.
단위/통합 테스트는 실행하지 않았다.
