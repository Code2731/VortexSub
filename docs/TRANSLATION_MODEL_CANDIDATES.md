# 번역 모델 후보 비교 준비 (2026-10-02)

## 선택과 출처

현재 Qwen3-4B-Instruct-2507 Q4_K_M을 기준으로 두 후보를 선정했다.
2026-10-02 사용자 동의 후 두 파일을 다운로드했고 크기·SHA-256을 검증했다.
새 runtime 설치는 하지 않았다. [실제 비교 결과](evidence/translation-model-candidates-windows-20261002.md).

| 후보 | 파일 크기 | 배포·라이선스 | 비교 목적 |
|---|---:|---|---|
| Qwen3 1.7B Q8_0 | 1,834,426,016 bytes | [Qwen 공식 GGUF](https://huggingface.co/Qwen/Qwen3-1.7B-GGUF), Apache-2.0 | 작은 범용 모델의 의미·속도 비교, thinking off |
| TranslateGemma 4B Q4_K_M | 2,489,909,760 bytes | [Google 원본](https://huggingface.co/google/translategemma-4b-it), [mradermacher GGUF](https://huggingface.co/mradermacher/translategemma-4b-it-GGUF), [Gemma 이용 약관](https://ai.google.dev/gemma/terms) | 번역 전용 모델 비교 |

합계 4,324,335,776 bytes(약 4.32 GB). revision/파일 SHA-256을
`benchmarks/translation-candidate-models.json`에 고정했다. 저장 위치는 Git 제외
`models/translation-candidates/`이며 제품 다운로드 manifest와 분리했다.
Qwen 공식 GGUF는 확인 시점에 Q8_0만 제공한다. 기존 Q4와 양자화가 달라
속도/품질 차이를 파라미터 수만의 효과로 해석하지 않는다.

[HY-MT 배포 라이선스](https://huggingface.co/tencent/HY-MT1.5-1.8B-GGUF/blob/265b2e615a7dc9b06c435dc878829ad99a512ba2/License.txt)는
허용 지역에서 한국을 제외하므로 이번 후보에서 제외했다.
[NLLB 모델 카드](https://huggingface.co/facebook/nllb-200-distilled-600M)는
CC-BY-NC·연구 목적을 명시하므로 제품 기본 후보로 선택하지 않았다.

## 비교 계약

기존 작성 원문과 같은 temperature 0.2/max_tokens 256을 사용한다.
모델별 권장 sampling 최적화와 자연 음성/live 비교는 별도 후속이다.
Qwen 후보는 Jinja와 `chat_template_kwargs.enable_thinking=false`를 사용하고
reasoning 출력이 있으면 실패로 기록한다. 앱 기본 모델/요청은 바꾸지 않는다.

TranslateGemma는 [공식 입력 계약](https://huggingface.co/google/translategemma-4b-it)의
user content 배열·source_lang_code·target_lang_code·원문을 사용한다.
system 역할과 참조 문맥은 넣지 않는다. 첫 단계는 설치된 llama.cpp가 이
템플릿과 필드를 올바르게 처리하는지 확인했다. 기존 build 6047은 내장 템플릿을
파싱하지 못하고 ChatML로 대체했다. 이 경로의 결과를 제외하고 GGUF 내장 템플릿을
기존 Jinja2로 그대로 렌더링해 `/tokenize`→`/completion`으로 전달했다.
BOS 한 개·2K 입력 한계를 확인하고 실제 prompt/token 요청을 결과 폴더에 보존한다.
다른 모델의 ChatML 자동 대체는 비교 시작 전에 실패로 처리한다.

동의·파일 해시 확인 후 사용할 명령:

```powershell
./scripts/probe-translation-context.ps1 -Rounds 3 -Fixtures benchmarks/translation-semantic-controls.json -Catalog benchmarks/translation-candidate-models.json -ModelId qwen3-1.7b-q8_0 -Profiles baseline,production
./scripts/probe-translation-context.ps1 -Rounds 1 -Fixtures benchmarks/translation-semantic-controls.json -Catalog benchmarks/translation-candidate-models.json -ModelId translategemma-4b-it-q4_k_m -Profiles gemma -ContextConditions none
```

원문 전체의 주체/행동/귀환/부정/조건/숫자를 수동 검토하고, HTTP 시간은 초로
표시한다. 좋은 단일 문장만으로 교체를 결정하지 않는다. 품질/지연 비교 전까지
기존 기본 모델과 문맥 분리 기본 off를 유지한다. 이후 사용자가 요청한
[Laya 출력 검증 실험](evidence/laya-translation-validation-windows-20261002.md)은
별도 진단으로 완료했으며 현재 checkpoint/질문으로 제품 연결을 채택하지 않는다.
