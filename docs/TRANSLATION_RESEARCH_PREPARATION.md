# 번역 비교 기반 준비 — P0.1/P0.2

후속 P1 EXAONE 다운로드·요청 export·실제 Qwen 비교를 완료했다.
[396응답의 시간·오류 사례와 남은 검증](evidence/exaone-translation-windows-20261002.md).
아래의 미실행 표시는 최초 P0 준비 라운드 기준이다.
후속 [greedy·입력 계약 대조](evidence/exaone-greedy-contract-windows-20261002.md)도
완료했다. `compare-exaone.py --sampling greedy --verify-input-contract`로 실행한다.
PowerShell probe는 `-Sampling greedy -VerifyInputContract`를 지원한다.
계약 확인은 EXAONE 실제 추론 전용이며 PrepareOnly/다른 모델에는 적용하지 않는다.

후속 [EXAONE 입력 구분/문맥 제거 비교](evidence/exaone-context-ablation-windows-20261002.md)를
완료했다. `--context-ablation --fixtures benchmarks/translation-exaone-ablation-fixtures.json`
을 비교 명령에 추가한다. probe의 `--exaone-input` / `-ExaoneInput`은
`existing|separated|source-only`이며 기본은 existing이다. 별도 앱 연결은 없다.
기존 48 heldout 항목은 calibration으로 전환했으며 새 24 heldout 항목은
`benchmarks/translation-exaone-holdout.json`에 있다. 아래 평가 자료의 초기 heldout
설명은 P0 당시 상태이며 현재 판정에는 새 partition을 적용한다.

재현은 translation probe 빌드 후 기존 Python으로 `scripts/compare-exaone.py`를
실행한다. 기본 3라운드에서 모델/fixture 순서를 교대하고 각 실행의 warmup을
제외한다. 한 모델씩 로드하며 results에 통합 익명 CSV와 별도 매핑을 저장한다.
추론은 `installed_verified` 후보만 허용한다. 다운로드 도구의 `-Catalog`·`-ModelId`로
동의된 단일 후보를 지정한다. Hy-MT2는 여전히 동의 대기다.

2026-10-02 구현. [개선 실행 계획](TRANSLATION_IMPROVEMENT_PLAN.md)의 입력 계약과
평가 자료를 준비했다. 이번 작업에서 실행 테스트·요청 export·서버 실행·새 모델
다운로드·추론 비교는 하지 않았다. 템플릿/런타임 호환성과 품질 판정은 미검증이다.
앱의 기본 요청·모델·설정은 변경하지 않았다.

## 요청 준비와 모델별 계약

`scripts/probe-translation-context.ps1 -PrepareOnly`는 기존 Rust `translation-probe`
export를 이용해 bounded 원문/문맥으로 후보 요청을 만든다. 모델 파일과 서버 없이
요청·fingerprint·fixture/profile/catalog/probe 해시를 Git 제외 results에 저장한다.
Rust export binary와 기존 Python 환경은 필요하다. `-NoBuild`를 생략하면 Rust
probe를 빌드한다. PrepareOnly는 template 검증/추론/품질 통과가 아니다.

* `exaone`: 공식 identity system, 번역용 user 지시와 현재 원문, 별도 참조 문맥,
  `[|endofturn|]` stop, repetition penalty 1.0. 실제 chat template 확인은 P1에서 한다.
* `hymt2`: 공식 번역/배경 문맥 형식에 맞춘 단일 user 메시지, target 언어 전체 이름.
  system이나 가정한 thinking-off 필드를 추가하지 않는다.
* 모델별 profile은 manifest의 `comparison_profile`과 일치해야 한다.
  새 후보는 `asset_status=pending_consent`이며 inference 실행은 차단한다.
  설치 이후 동의·파일 path/크기/SHA-256·상태를 기록하고 나서 실행 가능하게 한다.
* 실제 비교 시 embedded GGUF template 해시와 서버 `/apply-template` 결과를 저장한다.
  endpoint 미지원·빈 결과·ChatML 자동 대체는 비교 실패로 처리한다.
  BOS/EOS 및 실제 chat 요청과의 동등성 검토는 아직 P1의 선행 확인으로 남아 있다.
  `/apply-template` 왕복은 번역 HTTP 시간에서 제외한다. 현재
  [공식 server 문서](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)의
  messages 입력/prompt 응답 계약을 사용하며 설치된 build 6047 지원은 미검증이다.

준비용 명령(작성했으나 이번 라운드에서는 실행하지 않음):

```powershell
./scripts/probe-translation-context.ps1 -PrepareOnly -Catalog benchmarks/translation-research-models.json -ModelId exaone-3.5-2.4b-q4_k_m -Profiles exaone -Fixtures benchmarks/translation-research-fixtures.json
./scripts/probe-translation-context.ps1 -PrepareOnly -Catalog benchmarks/translation-research-models.json -ModelId hy-mt2-1.8b-q4_k_m -Profiles hymt2 -Fixtures benchmarks/translation-research-fixtures.json
./scripts/probe-translation-context.ps1 -PrepareOnly -Profiles baseline -Fixtures benchmarks/translation-research-fixtures.json
```

## 평가 자료와 수동 검토

`benchmarks/translation-research-fixtures.json`은 66항목이다.
기존 의미 통제 원문 6개 × 문맥 3조건의 18항목은 regression이다.
영어 12개·일본어 12개 × 문맥 없음/관련의 48항목은 새 heldout이다.
새 원문은 여섯 유형(부정·조건·대상·숫자·정정·문맥 의존)을 각 언어에 두 개씩 둔다.
reference는 예시 정답이며 정확한 문자열 일치를 요구하지 않는다.
대명사 유지와 관련 문맥으로 지시 대상을 해석하는 번역을 모두 문맥에 맞춰 검토한다.
자연 음성·부분 입력 trace의 평가 자료는 별도 준비해야 한다.

reference/오류 유형/검토 메모는 모델 요청에 넣지 않는다. 새 자료를 prompt 보정에
사용하면 heldout을 보정용으로 재분류하고 새 검증 문장을 준비한다.

실제 비교 완료 후 `semantic-review.csv`를 무작위 순서로 만들고 모델/profile/round
대응은 `review-mapping.json`에 별도 저장한다. 한 실행 내 model/profile 이름을
가리는 경로이며, 세 모델을 통합한 완전한 blind review 도구는 P1에서 보완한다.
CSV의 `critical_error`/`omission`/`extra_explanation`은 `0` 또는 `1`, 자연스러움은
`1..5`로 수동 기입한다. 미검토는 `PENDING`을 유지한다. 응답 실패는 의미 오류와
분리하며 미검토를 정상으로 집계하지 않는다.

```powershell
./models/tabby/venv/Scripts/python.exe -X utf8 scripts/summarize-translation-review.py <results/semantic-review.csv> --mapping <results/review-mapping.json>
```

mapping 없이 집계하면 익명 검토 진행률이며 모델 우열 표가 아니다.
반복 호출 행 수와 고유 case 수를 함께 표시한다. 자동 품질 gate/모델 채택은 없다.

## 남은 확인

Python 2개 파일 AST, PowerShell parser, JSON 3개 파일의 문법 검사와
`git diff --check`를 통과했다. 실행 테스트나 실제 추론 검증과는 별개다.

P0.1/P0.2는 코드·자료 준비 상태다. 실행/원문 보존/label 비노출과 기존
probe 회귀 확인은 미실행이다. 후속 P0.3 최초 admission 앞단 계측은
[구현·Windows 빌드를 완료했다](PRE_ASR_TIMING.md). 새 로그 검증은 미실행이다.
P1에서 모델 파일 준비·동의와 template/BOS/EOS 확인을 거쳐 실제 비교를 진행한다.
