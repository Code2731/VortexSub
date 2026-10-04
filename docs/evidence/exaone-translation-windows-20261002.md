# EXAONE 3.5 2.4B 첫 비교 — Windows / RTX 3080

2026-10-02, RI-01/02/06. 사용자 동의 후 공식 Q4_K_M을 설치하고 기존
llama.cpp build 6047로 Qwen3 4B와 실제 HTTP 비교했다. 기본 모델 전환은 보류한다.

## 자산과 실행 계약

* 공식 repository: `LGAI-EXAONE/EXAONE-3.5-2.4B-Instruct-GGUF`.
* 고정 revision: `142acae803a41c206e8d0fa978c6102c748911bb`.
* 크기: 1,644,918,272 bytes. SHA-256:
  `1660a3794acc33437137faab5cb3fb2b2c4191d0a4ec991d4c31a7197cb2cdbf`.
  [공식 파일 메타데이터](https://huggingface.co/LGAI-EXAONE/EXAONE-3.5-2.4B-Instruct-GGUF/blob/142acae803a41c206e8d0fa978c6102c748911bb/EXAONE-3.5-2.4B-Instruct-Q4_K_M.gguf).
* `models/`에 저장했고 크기·SHA-256 검증을 통과했다. Hy-MT2는 다운로드하지 않았다.
* 서버 build 6047 (`952a47f4`), architecture `exaone`, 31/31 GPU layer.
  model/KV/compute buffer는 각각 1424.09/300.00/284.00 MiB다.
  총 프로세스 메모리/동시 게임 실행 비용을 측정한 값은 아니다.
* 공식 identity system과 embedded Jinja template를 사용했다. `/apply-template` 성공,
  system/user/assistant 경계와 generation prompt를 확인했다. ChatML 대체는 없었다.
  EOS/EOG 361 `[|endofturn|]`, BOS 1 `[BOS]`를 서버 로그에서 확인했다.
  실제 내부 chat 토큰열과 별도 tokenization의 BOS 동등성까지 검증한 것은 아니다.
* 원문 66항목 × 3회 × 2모델 = 396응답. 모델/문장 순서를 교대로 뒤집었고
  매 실행 warmup 1응답은 집계에서 제외했다. 모델은 하나씩 로드했다.
* temperature 0.2, max_tokens 256, top_k 40, top_p 0.9, min_p 0.1,
  repeat_penalty 1, context 4096, GPU layers 99, parallel 1.
  공통 greedy 비교·seed 결정론·모델별 권장 sampling 비교는 남아 있다.

## HTTP 처리시간

모든 기간은 초다. p95는 nearest-rank이며 모델 로딩·warmup·template endpoint는
제외한다. 작성한 완성 문장 결과이며 음성→자막 지연이나 ASR 성능이 아니다.

| 언어 | 모델 | 응답/고유 case | 중앙값 | p95 | 최대 | 응답 실패 |
|---|---|---:|---:|---:|---:|---:|
| 영어 | Qwen3 4B | 117/39 | 0.141 | 0.219 | 0.265 | 0 |
| 영어 | EXAONE | 117/39 | 0.078 | 0.125 | 0.125 | 0 |
| 일본어 | Qwen3 4B | 81/27 | 0.140 | 0.188 | 0.203 | 0 |
| 일본어 | EXAONE | 81/27 | 0.078 | 0.109 | 0.125 | 0 |

영어/일본어 평균 출력 token은 Qwen 19.19/17.51, EXAONE 14.03/13.75다.
토크나이저와 출력 길이도 다르므로 시간 차이 전부를 엔진 효율로 해석하지 않는다.
영어 중앙값은 약 45%, p95는 약 43% 짧았다. EOS로 정상 종료했다는 판정은
의미 보존을 뜻하지 않는다. 게임 실행 상태는 확인하지 않았다.

## 원문 대조에서 확인한 오류

아래는 에이전트가 출력/원문을 대조한 사례 확인이다. 전체 blind CSV의 독립적인
사람 평가나 전체 오류율 집계가 아니다. 반복 3회는 독립 문장 3개가 아니다.

* `fresh-en-context-11-related`: silver key 문맥을 EXAONE이 **금색 열쇠**로
  3/3회 바꿨다. Qwen 첫 라운드는 색을 만들지 않고 대명사를 유지했다.
* `until-return-negative-related`: 현재 문장은 돌아올 때까지 문을 열지 말라는
  지시인데 EXAONE 3/3회가 이전 문맥의 “나는 다섯 분 후에 돌아올게요”를 출력했다.
  그중 2회는 번역하지 않은 영어 현재 문장도 덧붙였다.
* `fresh-ja-negation-13-related`: 종이 울릴 때까지 다리를 건너지 말라는 지시를
  EXAONE 3/3회가 다리를 건너기 전까지 종을 울리지 말라는 지시로 바꿨다.
* `direction-correction-none`: 문을 창문으로 바꾸고 오른쪽 정정 정보를 생략했다(3/3).
* Qwen도 기존 return→나가기, ranger/knight 대상·명칭 오류가 있었다.
  EXAONE 실패를 Qwen이 충분히 정확하다는 근거로 사용하지 않는다.

속도 후보로는 유망하지만 이번 입력 계약은 문맥 혼입·대상/조건 오류가 있어
채택 gate를 통과한 것으로 표시하지 않는다. EXAONE 일본어는 탐색 범위다.
전체 수동 품질 검토, greedy/입력 토큰 계약, partial trace, 실제 앱은 미검증이다.
검증 문장을 보고 prompt를 수정하면 해당 자료는 보정용으로 재분류한다.

## 재현과 보관

실행한 명령:

```powershell
cargo build -p echosub-translation --locked --offline
./scripts/download-probe-models.ps1 -Catalog benchmarks/translation-research-models.json -ModelId exaone-3.5-2.4b-q4_k_m
./scripts/probe-translation-context.ps1 -NoBuild -PrepareOnly -Catalog benchmarks/translation-research-models.json -ModelId exaone-3.5-2.4b-q4_k_m -Profiles exaone -Fixtures benchmarks/translation-research-fixtures.json
./models/tabby/venv/Scripts/python.exe -X utf8 scripts/compare-exaone.py
```

다운로드는 sandbox Schannel 권한 오류, 첫 비교는 WinGet link 읽기 권한 오류로
실패했다. 승인된 작업을 제한 환경 밖에서 재실행해 성공했다. 실패 자료도 보존했다.
성공 결과: `benchmarks/results/exaone-comparison-20261002-084045-3b48c4/`.
`summary.json`, 6개 run의 원문/요청/template/응답/로그와 익명
`semantic-review.csv`·별도 `review-mapping.json`을 Git 제외 경로에 저장했다.
CSV 품질 annotation은 모두 PENDING이며 자동 채택하지 않는다.
단위/통합 테스트·자연 음성·앱·macOS는 이번 라운드에서 실행하지 않았다.
