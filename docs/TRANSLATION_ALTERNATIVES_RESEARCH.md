# 실시간 한국어 번역 대안 조사

조사일: 2026-10-02. Windows / RTX 3080 / 현재 Whisper CUDA +
Qwen3-4B-Instruct-2507 Q4_K_M 구성을 기준으로 한다.
Exa Search로 품질 검증·동시 번역·번역/ASR 모델 세 영역에 총 62건 규모의
결과를 요청했다. 반환 URL은 63건, 중복 제거 후 61개다. 같은 논문의 PDF/HTML은 하나의 연구로
취급했다. 아래 판단은 핵심 논문·저자 코드·모델 카드와 공식 배포 메타데이터를
확인한 결과다. 새 모델/런타임 다운로드·설치·추론 비교는 하지 않았다.
가중치 크기는 메타데이터 조회값이고 VRAM이나 전체 설치 용량은 아니다.

## 1. 결론과 우선순위

1. **번역 품질:** 최신 Hy-MT2 1.8B를 영어/일본어→한국어의 첫 비교 대상으로
   선정한다. 공식 GGUF와 Apache-2.0 배포본이 있어 현재 런타임을 재사용할
   가능성이 있다. 영어 전용 소형 encoder-decoder 대조군은 OPUS-MT 209M,
   다국어 encoder-decoder 후속은 MADLAD-400 3B다.
2. **검증:** Laya의 다음 후보는 원문/번역을 평가하도록 학습된 MetricX-24
   Large QE다. 먼저 오프라인 진단으로 한국어 판별력을 확인한다. 자막을
   기다리게 하는 동기식 차단기는 초기 설계에서 제외한다.
3. **지연:** 현재 원문 단위 정책에 번역 후보의 안정성과 수정 비용 평가를
   추가한다. 첫 admission 전 지연이 미측정이므로 먼저 그 구간을 분해한다.
   ASR 모델 교체나 AlignAtt 도입이 주된 개선책인지는 아직 결정할 수 없다.

위 순서는 프로젝트 적용 가능성에 따른 제안이다. 우리 오류 문장에서의
개선, RTX 3080 처리시간, 게임 동시 실행을 확인한 결론이 아니다.

## 2. 현재 근거와 해결해야 할 문제

* [Laya 실험](evidence/laya-translation-validation-windows-20261002.md)은 전체
  의미 오류 검출 0~3/12, 조건 검출 0/6이었다. 모델/질문 조합의 한계로
  제품 연결을 채택하지 않았다.
* [번역 모델 비교](evidence/translation-model-candidates-windows-20261002.md)에서
  Qwen 1.7B와 TranslateGemma 4B 모두 부정·조건·대상 오류가 남았다.
* 기존 정상 세션 로그
  `logs/caption-timing-20261002-060448-780c00d553014ebebb9922422c3b354c.jsonl`을
  `scripts/summarize-caption-timing.py`로 다시 집계했다. ASR dispatch→completion
  중앙값 0.1083초(851건), 번역 dispatch→completion 0.1733초(374건),
  첫 원문→첫 nonempty stable 0.2651초(81건), 최초 관찰 admission→첫 번역
  update 0.4994초(87건), UI 수신→deck 적용 0.0349초(338건)였다.
* 서로 다른 표본의 중앙값을 합산하지 않는다. 음성이 들린 시점→첫 admission,
  물리적 화면 출력, 사용자가 읽기 시작한 시간은 이 집계로 알 수 없다.
  sampled voice span 2.912초는 오디오 범위이며 대기 지연이 아니다.
  따라서 추론 전체가 2초 병목이라고도, 지연 원인이 확정됐다고도 말하지 않는다.

원문 오인식, 완전한 원문의 오역, 미완성 입력의 의미 부족, 큐/표시 지연은
서로 다른 문제다. 품질 검증기가 아직 발화되지 않은 조건을 알아낼 수는 없다.

## 3. 번역 품질 추정: 범용 판단기 대신 QE

QE(Quality Estimation)는 정답 번역 없이 원문과 후보 번역으로 품질을 추정한다.
점수는 오류 가능성에 관한 신호이고, 검증 통과나 정답 번역 자체가 아니다.

| 후보 | 논문/공식 구현에서 확인한 기능 | 적용 판단 |
|---|---|---|
| MetricX-24 Large | DA/MQM 및 합성 오류로 학습; reference-free와 reference-based 모두 지원; 오류 점수 0~25, 낮을수록 좋음 | **첫 QE 비교 후보**. 한국어 검출력·단일 요청 시간 미확인 |
| COMETKiwi | 원문+번역만으로 점수; 한국어를 포함하는 기반 언어 범위 | 공개 checkpoint CC-BY-NC-SA-4.0. 제품 기본 후보에서 보류 |
| XCOMET-XL | 오류 span·심각도와 문장 점수, 약 3.5B | 공개 checkpoint CC-BY-NC-SA-4.0, 상용 사용 별도 승인 안내. 메모리도 큼 |
| XCOMET-lite | 278M 증류, 원 논문의 큰 모델 성능 일부 유지 | 카드 언어 en/de/es/ru/zh, 예제에 ref 포함; weight 라이선스 명시 없음. 작은 크기만으로 한국어 QE 후보로 채택하지 않음 |

### MetricX를 먼저 검토할 근거와 제약

[MetricX-24 논문](https://aclanthology.org/2024.wmt-1.35/)은 유창하지만 무관한
번역, 누락 등 기존 지표가 놓치는 오류에 대한 합성 학습/평가를 설명한다.
[공식 코드](https://github.com/google-research/metricx)는 `--qe`에서 정답을
빈 문자열로 넣고 원문/후보로 평가한다. 자유 문장을 생성해 판정을 읽는 방식이
아니며 [회귀 모델](https://github.com/google-research/metricx/blob/main/metricx24/models.py)은
한 decoder step으로 점수를 계산한다. 속도를 기대할 이유지만 실측 보장은 아니다.

공개 [Large BF16 카드](https://huggingface.co/google/metricx-24-hybrid-large-v2p6-bfloat16)의
평가 표는 en-de/en-es/ja-zh다. MQM 학습도 en-de/en-ru/en-zh/zh-en 중심이다.
한국어를 표현할 수 있는 기반 모델과 한국어 오류 판별 성능 검증은 구분한다.
이 논문의 인간 판단 상관관계를 '우리 부정 오류 검출률'로 바꾸어 읽으면 안 된다.

모델 메타데이터는 Apache-2.0, BF16 weight 2,459,348,101 bytes를 명시한다.
처음에는 기존 24개 사례의 정답 후보 점수가 오류 후보보다 좋은지, 유형별
순위 역전이 얼마나 있는지 평가한다. 별도의 한국어 검증 자료 없이 임의 점수
임계값으로 자막을 차단하거나 점수를 확률로 해석하지 않는다.

라이선스/한국어 범위 근거:
[COMETKiwi 카드](https://huggingface.co/Unbabel/wmt22-cometkiwi-da),
[xCOMET 논문](https://aclanthology.org/2024.tacl-1.54/),
[XCOMET-XL 카드](https://huggingface.co/Unbabel/XCOMET-XL),
[xCOMET-lite 논문](https://aclanthology.org/2024.emnlp-main.1223/),
[lite 카드](https://huggingface.co/myyycroft/XCOMET-lite),
[저자 구현](https://github.com/NL2G/xCOMET-lite).
lite의 ref 없는 실행 가능성과 배포 허용 조건은 추가 확인 사항이다.

## 4. 번역 엔진 후보: 영어 전용 작은 모델부터

| 후보 | 언어·구조 | 확인된 weight 크기/라이선스 | 비교 목적과 제한 |
|---|---|---|---|
| Hy-MT2 1.8B | 번역 특화 decoder-only, 한국어/일본어 포함 | 공식 Q4_K_M/Q6_K/Q8_0 GGUF; Apache-2.0. 개별 GGUF 크기는 이번 조회에서 미확정 | **첫 엔진 후보**. 현재 llama.cpp 호환성과 짧은 입력 품질은 미검증 |
| OPUS-MT `opus-mt-tc-big-en-ko` | 영어→한국어, Marian transformer-big, 209,158,401 parameters | FP16 safetensors 418,346,322 bytes; CC-BY-4.0 | 영어 전용 소형 대조군. 품질/지연 우위는 미측정. 일본어 경로를 대체하지 않음 |
| MADLAD-400 3B | 한국어 포함 다국어, T5 encoder-decoder, 약 2.94B | 원본 F32 safetensors 11,761,587,872 bytes; Apache-2.0 | 영어/일본어→한국어 통합 후보. INT8 변환과 동시 VRAM을 검증해야 함 |
| M2M100 418M | 일본어/한국어 포함 100개 언어, many-to-many | weight bin 1,935,796,948 bytes; MIT | 작은 다국어 속도 대조군. 최신 품질 우위 근거는 없음 |
| TranslateGemma 4B | 한국어 포함 번역용 Gemma | 현재 파일 확보/기존 비교 완료; Gemma Terms | 공식 template/권장 설정 재확인 가능. 기존 실패를 무시하고 기본 교체하지 않음 |
| HY-MT1.5 1.8B | 번역 특화 소형 모델 | Tencent HY Community License | 고정 배포 라이선스가 한국을 허용 지역에서 제외하므로 현재 제품 후보 제외 |

OPUS-MT의 [영어→한국어 카드](https://huggingface.co/Helsinki-NLP/opus-mt-tc-big-en-ko)는
언어 방향과 예제를 확인할 수 있다. CC-BY-4.0의 출처 표시 조건을 반영해야 한다.
오래된 모델이며 카드의 FLORES BLEU 13.7은 게임 자막 품질 보장이 아니다.
카드에 자동 생성된 빈 다국어 태그 문구도 있어 실제 tokenizer/config와
예제를 확인한 후 사용할 입력 계약을 정해야 한다. 영어→한국어 모델을
일본어→한국어 모델로 잘못 확대하거나, 일본어→영어→한국어 우회를 기본화하지 않는다.

[OPUS-MT 논문](https://aclanthology.org/2020.eamt-1.61/),
[MADLAD 논문](https://arxiv.org/abs/2309.04662),
[Google namespace MADLAD 카드](https://huggingface.co/google/madlad400-3b-mt),
[M2M100 카드](https://huggingface.co/facebook/m2m100_418M).
MADLAD 카드의 PyTorch 변환은 원 논문 저자와 별개 기여자가 작성했다고 명시한다.
배포 provenance와 변환 버전을 고정해야 한다.
HY 제외 근거는 [확인한 고정 라이선스](https://huggingface.co/tencent/HY-MT1.5-1.8B-GGUF/blob/265b2e615a7dc9b06c435dc878829ad99a512ba2/License.txt)다.
이 제외 판단은 MT1.5에 한정되며 MT2에 적용하지 않는다.

### 새 Hy-MT2 배포본은 별도로 판단

2026-05-21 공개된 [Hy-MT2 논문](https://arxiv.org/abs/2605.22064)과
[공식 카드](https://huggingface.co/tencent/Hy-MT2-1.8B)는 번역 특화 학습,
용어·문체·배경 문맥 지시, 한국어/일본어를 지원한다. 원본 모델 revision은
`9a341cd1b679d3efd23b46e847b01745a71ed792`이며,
[해당 weight 라이선스](https://huggingface.co/tencent/Hy-MT2-1.8B/blob/9a341cd1b679d3efd23b46e847b01745a71ed792/LICENSE.txt)는
Apache-2.0이다. 이전 MT1.5의 한국 지역 제외 조항을 계승하지 않는다.
[공식 GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF) revision은
`a0c709d9fac510f2c807aa3af52872340dc37a4a`이며 API 라이선스도 Apache-2.0이다.

논문의 440 MB/1.5배 속도 주장은 1.25-bit 특수 양자화와 Apple A15 비교다.
RTX 3080의 Q4_K_M 성능으로 환산하지 않는다. 공식 저장소는 극저비트 GGUF의
STQ kernel PR을 안내하므로 첫 비교는 일반 Q4_K_M으로 한정한다. 현재 build 6047의
모델 아키텍처/template/EOS 지원을 확인해야 하며 새 빌드가 필요할 수 있다.
가중치 파일 크기 조회는 연결 실패로 미확정이며 다운로드 전에 확정한다.

공식 문구의 fast-thinking을 긴 chain-of-thought 출력으로 해석하지 않는다.
[고정 chat template](https://huggingface.co/tencent/Hy-MT2-1.8B/blob/9a341cd1b679d3efd23b46e847b01745a71ed792/chat_template.jinja)에는
`enable_thinking` 분기나 `<think>` 태그가 없다. 별도 thinking-off 옵션이 있다고
가정하지 않고 실제 출력과 종료를 확인한다. 권장 user 번역 지시/배경 문맥 형식을
사용하고 현재 Qwen용 JSON/system 계약을 그대로 이식하지 않는다.

### CTranslate2 사용의 이유

[공식 Transformers 지원 목록](https://opennmt.net/CTranslate2/guides/transformers.html)은
MarianMT/M2M100/T5를 지원한다.
[설치 문서](https://opennmt.net/CTranslate2/installation.html)는 Windows x64 CUDA
실행 경로를 제공하며 [양자화 문서](https://opennmt.net/CTranslate2/quantization.html)는
`int8_float16` 등을 설명한다. 현재 llama.cpp와 다른 런타임이므로 변환·초기화·
취소·오류·토큰화 계약을 별도로 검증해야 한다. 모델 지원 목록만으로 특정
checkpoint의 변환 성공이나 3080 처리시간을 단정하지 않는다.

MADLAD 원본 다운로드 11.76 GB와 변환 후 INT8 파일·추론 메모리는 별개다.
다운로드/설치 규모와 모델/runtime 해시를 확정한 뒤 기존 동의 절차를 따른다.

## 5. 실시간 구조 대안: 원문 안정성과 번역 안정성을 분리

### 직접 참고할 논문

* [Re-translation versus Streaming (IWSLT 2020)](https://aclanthology.org/2020.iwslt-1.27/):
  수정 가능한 자막에서 재번역은 작은 수정 허용량 아래에서도 강한 대안이 될 수
  있음을 비교한다. 모든 출력을 즉시 변경 불가로 만드는 것이 유일한 해법은 아니다.
* [영한/한영 분할 연구 (PACLIC 2020)](https://aclanthology.org/2020.paclic-1.16/):
  운율/POS/의존관계/담화와 지연을 고려한 분할을 비교하며 입력 언어에 따라
  적절한 방법이 다르다고 보고한다. 문장 끝 문자나 길이만으로 의미 완성을
  보장할 수 없다는 점을 뒷받침한다. 강연 자료 연구이며 게임 대화 검증은 아니다.
* [Whisper-Streaming (2023)](https://arxiv.org/abs/2307.14743): LocalAgreement와
  자기 적응 지연. 보고된 3.3초는 특정 ASR 자료/A40 조건이며 우리 한국어 번역
  지연의 목표값이나 성능 예측치가 아니다.
* [CUNI/SimulStreaming (IWSLT 2025)](https://arxiv.org/abs/2506.17077):
  Whisper AlignAtt + EuroLLM cascade; 번역에도 이전 확정 prefix와 미확정 tail,
  연속 결과의 공통 prefix를 사용한다. 영→독/중/일의 보고된 4~5초는
  계산 시간을 제외하는 CU 조건이며, 모델 구성 그대로의 10 GB 실행을 보장하지 않는다.
* [AlignAtt4LLM (2026)](https://arxiv.org/abs/2606.03967): decoder-only LLM에서
  source prompt 구간, alignment head 선택, Q/K 관찰로 target commit을 제어한다.
  A40·vLLM·영→독/이/중 연구이며 한국어/우리 Qwen4B 검증은 없다.

### 기존 구현과 새로 해야 할 일

이미 [원문 LocalAgreement](STREAMING_TRANSLATION.md),
[원문 절 단위/조건 보류](TRANSLATION_UNITS.md), 두 카드 읽기 정책을 구현했다.
`translation_prefix`는 현재 단위까지의 **원문 guard**이며 확정 한국어 prefix가 아니다.
따라서 새로 제안하는 핵심은 다음과 같다.

1. 동일 원문 흐름의 한국어 후보 두 개를 비교해 번역의 유지/변경 영역을 기록한다.
2. 앞부분의 낮은 수정 가능성만으로 의미 정확성을 선언하지 않는다. 반복 오역은
   계속 안정적으로 반복될 수 있다. 조건·부정 등 늦게 붙는 정보의 반박을 처리한다.
3. 원문 guard와 target의 draft/revisable prefix를 분리하고, 이미 읽는 카드의
   불필요한 수정은 줄이되 핵심 오류는 수정 가능한 정책을 평가한다.
4. 전체 확정 원문을 새로 번역하는 현재 교정 경로를 유지하고 잘못된 target
   prefix를 강제로 영구 고정하지 않는다.

문자열 target agreement는 현 엔진으로 진단할 수 있다. AlignAtt는 내부 attention
관찰이 필요하므로 HTTP 옵션 하나로 구현할 수 없다. 특히 AlignAtt4LLM의
Q/K 캡처·head 선택은 별도 runtime 연구 규모다. 단순 prefix 비교부터 측정하고,
효과가 부족할 때 attention 기반 접근으로 넘어가는 편이 현실적이라는 판단이다.

[SimulStreaming 구현](https://github.com/ufal/SimulStreaming),
[현재 MIT 라이선스](https://github.com/ufal/SimulStreaming/blob/main/LICENCE.txt).
기존 [오픈소스 검토](REALTIME_OSS_REVIEW.md)와 겹치는 내용은 신규 발견으로
계산하지 않으며, 이번 조사에서 논문 근거와 대체 모델/평가기 조건을 보완했다.

## 6. ASR 교체는 조건부 후속

[Qwen3-ASR 보고서](https://arxiv.org/abs/2601.21337)와
[공식 코드](https://github.com/QwenLM/Qwen3-ASR)는 0.6B/1.7B, 한국어/일본어 포함
언어 범위, streaming/offline 모델을 제공한다. 현재 공식 streaming API는
vLLM backend만 지원하고 streaming timestamp 반환은 지원하지 않는다.
[vLLM 공식 설치 조건](https://docs.vllm.ai/en/latest/getting_started/installation/gpu/)은
native Windows 미지원이며 WSL 등의 경로를 안내한다.

따라서 0.6B는 ASR 병목이 확인되면 검토할 후보다. 보고서의 첫 토큰 0.092초와
동시 128요청 throughput을 단일 게임 PC의 최초 안정 전사/한국어 자막 지연으로
환산하지 않는다. 현재 Whisper base CUDA의 약 0.108초 처리 자료만으로
ASR 교체가 가장 큰 이득이라고 결정할 수 없다. 기존 SenseVoice 비교도
[해당 환경에서는 가속을 확인하지 못했다](MODEL_CANDIDATES.md).

## 7. 다음 실험의 구체적인 순서와 종료 조건

### A. 다운로드 없이 입력·대기·번역 안정성 확인

현재 4B 공식 template/비사고 계약과 서버에 렌더링되는 실제 요청을 확인한다.
이미 비사고 전용인 모델에 thinking 옵션을 추가한 것만으로 개선을 주장하지 않는다.
기존 로그에서 최초 입력 admission 전 지연을 알 수 없으면 worker 내부의
동일 clock/identity로 그 시작·대기 경계를 보완한다. 한국어 target 수정량,
첫 읽을 수 있는 자막 시간, 조건 보류 시간을 따로 집계한다.

이 과정에서 translation HTTP가 병목이 아니면 엔진 축소를 지연 해결의
주력으로 삼지 않는다. 중간값만 빠르고 최악 상황이 악화되는 변경은 채택하지 않는다.

### B. Hy-MT2와 OPUS-MT 엔진 비교

먼저 Hy-MT2 1.8B 일반 Q4_K_M을 현재 Qwen4B 대조군과 같은 완성 원문/부분 원문으로
비교한다. 영어/일본어 부정·조건·대상·수량과 배경 문맥을 각각 평가한다.
기존 llama.cpp 호환성, 공식 template, EOS/불필요 출력과 요청 취소를 확인한다.
공개 평균 성능이 좋아도 기존 핵심 오류가 반복되거나 p95가 나빠지면 채택하지 않는다.
1.25-bit/STQ 경로는 이 비교가 통과하고 추가 속도 연구가 필요한 때로 미룬다.

OPUS-MT는 영어 전용 소형 구조 대조군으로 둔다.
기존 전체 원문 오류 + 새로운 영어 게임 대사/짧은 문맥을 분리한다.
Qwen4B 대조군과 같은 원문·문맥 요구·시드·출력 한도 조건을 기록한다.
모델별 권장 입력은 따르며 JSON chat 입력을 Marian에 그대로 넣지 않는다.
beam1/beam4, FP16과 INT8은 각각 분리한다. 한국어 수동 검토로 부정·대상·
수량·조건 오류, 짧은 prefix 오역, 자연스러움을 평가한다.
작다는 이유만으로 채택하지 않고 품질이 나쁘면 이 후보 비교를 종료한다.

### C. MetricX의 한국어 QE 진단

Laya의 24개 사례는 smoke 역할이며 새 문장·패러프레이즈를 별도 평가 자료로 둔다.
source/MT만 제공하고 gold reference/정답 label은 넣지 않는다. 오류 유형별
순위 판별과 정상 오탐을 확인하고 threshold 보정 자료와 검증 자료를 분리한다.
판별력이 부족하면 실시간 경로 연결을 종료한다. 통과하더라도 처음에는
비동기 관찰/확정 결과 비교에만 사용한다. 후보 둘의 선택이나 재번역은 추가
연산 비용과 실제 품질 향상을 확인한 뒤 결정한다.

### D. 일본어·다국어가 필요할 때 MADLAD 비교

OPUS 성공을 일본어 성공으로 확대하지 않는다. MADLAD의 `<2ko>` 입력 계약,
CT2 변환/양자화, 일본어 부정·조건과 고유명사를 별도 비교한다.
3080에서 ASR/게임과 동시 메모리·p95 지연이 요구를 만족하지 않으면 채택하지 않는다.

이번 조사만으로 새 다운로드를 실행하지 않았다. 다음 라운드에서 선택한
실험의 모델·runtime·용량을 확정한 뒤 동의를 받고 실행한다.
