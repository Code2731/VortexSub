# 번역 모델 후보 실제 비교 (2026-10-02)

## 자산·실행

사용자가 두 후보 진행에 동의했다. manifest의 고정 revision에서 다운로드한
Qwen3 1.7B Q8_0(1,834,426,016 bytes)와 TranslateGemma 4B Q4_K_M
(2,489,909,760 bytes)는 크기·SHA-256이 일치한다. 파일은 Git 제외
`models/translation-candidates/`에 보관하며 새 runtime은 설치하지 않았다.
모델 출처·라이선스는 [선정 문서](../TRANSLATION_MODEL_CANDIDATES.md)에 있다.

Windows/RTX 3080, 기존 llama.cpp build 6047(952a47f4), 단일 슬롯,
GPU offload 99, context 4096, temperature 0.2, max_tokens/n_predict 256.
top_k 40/top_p 0.9/min_p 0.1/repeat_penalty 1. Qwen 1.7B는 Jinja와
enable_thinking=false를 사용했고 reasoning 출력 오류가 없었다.
ASR·캡처·앱 E2E와 일반 테스트는 실행하지 않았다.

## 템플릿 호환성

TranslateGemma의 최초 공식 user 배열 실행에서 build 6047이 내장 Jinja의
dictionary 끝 쉼표를 파싱하지 못하고 ChatML로 자동 대체했다. 원문에 응답하는
영어 문장이 출력됐으며 이 6회(+warmup 1회)는 모델 품질 집계에서 제외했다.
결과는 `translation-context-20261002-065232-b0d0ee/`에 보존했다.
어댑터 수정 중 저장 함수 호출 순서 오류 1회는 HTTP 전에 실패했다.

다운로드 모델은 수정하지 않았다. GGUF 내장 템플릿(16,979자)을 추출하고
기존 Jinja2 3.1.6 Sandbox/StrictUndefined로 공식 입력을 그대로 렌더링했다.
`/tokenize`의 add_special=false/parse_special=true로 BOS가 정확히 한 개인지
확인하고 토큰 배열을 `/completion`에 전달했다. 공식 2K 입력 한계를 확인했다.
이 경로는 서버의 ChatML 대체를 사용하지 않는다. 다른 모델에서 템플릿 자동
대체가 발생하면 추론 비교 시작 전에 실패로 처리하도록 보완했다.

## 유효 실행과 시간

아래 경로는 모두 `benchmarks/results/` 아래이며 Git 제외다. warmup은 제외했다.

| 결과 폴더 | 조건 | 유효 요청 | HTTP 중앙값 |
|---|---|---:|---:|
| `translation-context-20261002-065146-23b97d` | Qwen 1.7B, 의미 통제 6문장×3문맥×2정책×3회 | 108 | 기존·문맥 분리 각각 0.094초 |
| `translation-context-20261002-065533-f7dcdd` | TranslateGemma, 의미 통제 6문장·문맥 없음×3회 | 18 | 0.125초 |
| `translation-context-20261002-065647-6fd1f4` | TranslateGemma, 기존 12문장·문맥 없음×3회 | 36 | 0.1325초 |
| `translation-context-20261002-065704-1205bf` | Qwen 4B, 같은 기존 12문장·문맥 없음×3회 | 36 | 0.125초 |

유효 요청 합계 198회, 응답 오류 0회. TranslateGemma 시간에는 tokenize HTTP도
포함한다. prompt cache가 켜져 있고, 모델·양자화·입력 지시·endpoint가 다르므로
모델 파라미터 수만의 인과 비교나 실시간 음성→화면 지연으로 해석하지 않는다.
원문 전체의 HTTP 비교이며 partial 번역을 확인한 것은 아니다.

최종 도구 보완 확인은 `translation-context-20261002-065955-32c4f1/`에서
추가 6회 완료·오류 0으로 확인했다(비교 집계와 별도). 자동 대체 감지=true,
공식 raw prompt 경로로 우회=true이며 실제 completion 토큰 요청 6개를 보존했다.
입력은 83~87토큰이고 모두 BOS 한 개다. Python 문법과 Git diff 확인도 완료했다.

## 수동 의미 검토

* TranslateGemma: 영어 귀환 3문장×3회 총 9/9에서 귀환/부정을 보존했다.
  기존 12문장에서도 영어 귀환 3/3은 “제가 돌아올 때까지 문을 열지 마세요.”다.
* 하지만 `Turn off the engine, not the shield.`는 3/3에서
  “엔진을 끄는 것이 아니라, 보호 장치를 끄세요.”로 행동을 반전했다.
  일본어 `戻るまでドアを開けないで。`도 3/3에서 귀환을 문 닫힘으로 바꿨다.
* 물약 통제 문장은 3/3에서 가져가기를 섭취하기로, 세 개 미만을 세 개 남음으로
  바꿨다. 일본어 왼쪽 문 지시는 방향을 유지했지만 “여주시길” 표현 오류가 남았다.
  `Take the right path.`의 “올바른 길” 해석은 게임 fixture의 방향 의미와 다르나
  원문 자체에도 중의성이 있어 확정적인 모델 오류로만 집계하지 않는다.
* Qwen 1.7B: 문맥 분리에서 before 귀환은 개선됐으나 숫자 조건 반전·부정 누락·
  번역 지시 재출력·러시아어/중국어 혼입 등이 남았다. 기존 정책의 일본어 정정
  관련 문맥 3/3은 왼쪽 지시를 오른쪽으로 바꿨다. 속도만으로 교체하지 않는다.
* 기준 Qwen 4B도 영어 귀환 3/3 “나가기 전까지”, 30→20 오류 1회가 남았다.
  반면 엔진/방어막 행동 구분은 3/3 보존했다. 후보가 모든 항목에서 우세하지 않다.

## 결정

두 후보 모두 품질 gate false. 기본 모델 교체·자동 fallback·언어별 자동 분기는
채택하지 않는다. 앱 기본 모델/문맥 분리 기본 off와 Laya 보류를 유지한다.
다음 후보 검토에는 단일 좋은 번역보다 행동 반전·귀환·숫자 경계 회귀를 우선한다.
TranslateGemma의 공식 입력 어댑터는 진단 전용이며 앱/worker 번역 경로에는
연결하지 않았다. 라이선스·출력 속도만으로 제품 적용 완료를 주장하지 않는다.
