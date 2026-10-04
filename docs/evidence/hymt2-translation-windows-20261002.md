# Hy-MT2 1.8B 실제 비교

2026-10-02, Windows / RTX 3080 10 GB, RI-01/02/06.
사용자 승인 후 모델과 별도 b11146 runtime을 설치하고 크기/SHA-256을 검증했다.
고정 파일·revision·archive 해시는 [준비 문서](../HY_MT2_COMPARISON.md)와
`benchmarks/translation-research-models.json` / `translation-research-runtimes.json`에 있다.
새 runtime은 `models/runtime-b11146/llama-server.exe`이며 앱 기본 runtime은 바꾸지 않았다.

## 실행 계약과 GPU

* b11146 (`7fe450e19`), CUDA 12.4 Windows x64 공식 binary와 CUDA DLL ZIP을 사용했다.
  실제 RTX 3080에서 33/33 layer GPU offload를 확인했다. model/KV/compute buffer는
  1075.74/256.00/52.01 MiB, CPU mapped model은 193.57 MiB다.
  다른 프로세스·게임과 함께 측정한 총 사용량은 아니다.
* GGUF architecture `hunyuan-dense`, BOS 120000, EOS 120020은 공식 base config와
  일치했다. 공식 embedded template, system 없는 단일 user 입력을 사용했다.
  thinking-off 필드는 추가하지 않았다.
* Hy-MT2 90개 요청 × 3회에서 로컬/서버 렌더링의 토큰열과 chat prompt 개수가
  일치했다. BOS는 한 개, assistant prefix도 공식 형식이었다. ChatML 대체는 없었다.
  내부 chat token sequence를 직접 노출한 검사는 아니며 렌더링/tokenization/개수 검사다.
* 로딩 때 `special_eos_id is not in special_eog_ids` 경고가 나왔다. 별도 native
  `/completion`을 확인했으며 `stop_type=eos`, `truncated=false`, 마지막 생성 token
  **120020**으로 정상 종료했다. 상세 로그의 EOS/EOG도 같은 ID다.
  한 입력의 종료 확인을 모든 장기 실행/취소/timeout 검증으로 확대하지 않는다.

## 동일 runtime의 비교

기존 90항목(regression 18, calibration 48, heldout 24)을 Qwen/Hy-MT2로
교대 3회 실행했다. 총 540응답의 실패는 0이다. warmup 6개와 별도 EOS 진단은
집계에서 제외한다. 모델은 하나씩 GPU에 올렸다. 앱/ASR/캡처를 통과한 자료는 아니다.

두 모델 모두 **b11146**을 사용했다. 앞선 EXAONE/6047 결과와의 단순 시간 비교로
모델 효과를 주장하지 않는다. 공통 greedy는 temperature 0, top_k 1, top_p 1,
min_p 0, repeat_penalty 1, seed 42, max_tokens 256, context 4096, parallel 1이다.
Hy-MT2 공식 권장 sampling(0.7/0.6/20/1.05)은 아직 비교하지 않았다.

아래는 heldout만 집계한 초 단위 기간이다. 언어·모델마다 36응답, 문맥 짝 12 case,
고유 원문은 6개다. p95 nearest-rank. template/tokenize/loading/warmup/기록 파일 쓰기는
제외하며 HTTP 응답 수신 직후 측정한다. 음성→자막 전체 지연을 측정한 값은 아니다.

| 언어 | 모델 | 중앙값 | p95 | 최대 |
|---|---|---:|---:|---:|
| 영어 | Qwen3 4B | 0.156 | 0.188 | 0.203 |
| 영어 | Hy-MT2 1.8B | 0.094 | 0.125 | 0.141 |
| 일본어 | Qwen3 4B | 0.156 | 0.266 | 0.281 |
| 일본어 | Hy-MT2 1.8B | 0.094 | 0.140 | 0.141 |

영어 중앙값은 약 40%, p95는 약 34% 짧았다. 전체 90항목에서는 영어/일본어
중앙값이 Hy-MT2 0.093/0.093초, Qwen 0.141/0.140초다.
출력 token 수·길이도 다르므로 시간 차이를 토큰당 효율로 해석하지 않는다.
이번 GPU/환경의 처리시간이며 게임 동시 실행과 macOS는 미측정이다.

## 의미 보존 사례와 남은 오류

에이전트가 원문과 결과를 대조한 사례다. 전체 blind 사람 평가/정량 오류율은 아니다.

* 기존 until-return 문맥: Qwen의 “나가기 전까지” 대신 Hy-MT2는 3/3회
  “제가 돌아올 때까지 문을 열지 마세요”를 출력했다.
* 새 일본어 화로 금지, 두 램프가 꺼진 후에만 레버를 당기는 조건은 첫 라운드에서
  Hy-MT2가 보존했다. Qwen의 금지 누락/명칭 오류가 개선된 사례다.
* 새 영어 fewer-than-six 관련 문맥: Hy-MT2는 미만 경계를 보존했다.
  Qwen은 이하로 바꿨다. 문맥 없는 경우도 Hy-MT2의 “6회 이상은 하지 말라”는
  미만 경계를 유지했지만 장치 charge 용어의 자연스러움은 별도 검토 대상이다.
* `exaone-holdout-ja-09-related`: 수리공에게 수레를 고치게 하라는 지시를
  Hy-MT2가 3/3회 **“수리점의 마차를 고쳐서”**로 바꿨다. 행동 주체가 달라졌다.
* `exaone-holdout-ja-10-related`: 약초 네 묶음 미만을 사용하라는 지시를 3/3회
  **“4봉지의 절반도 사용하지 않았고”**로 바꿨다. 숫자 경계·행동 시제가 달라졌다.
* `exaone-holdout-en-06-related`: shelter를 3/3회 “캠핑장”으로 바꿨다.
  문맥은 지도 소지이며 목적지 변경을 뒷받침하지 않는다.
* 영어 brass cog를 bronze screw로 바꾸고 일본어 바늘 정정을 과거 상황으로 바꾼
  사례도 있었다. 번역 특화 모델이라고 용어/행동 정정을 자동으로 신뢰하지 않는다.

Hy-MT2는 EXAONE보다 좋은 후보라는 사례 근거가 있지만 이번 구성도 무오역/채택
gate 통과로 표시하지 않는다. 특히 일본어 행동 주체·수량 오류를 숨기지 않는다.
Qwen에도 오류가 남아 있다. 기본 모델은 유지하고 다음은 공식 권장 설정과 언어별
품질 비교다. 선택 구성의 새 검증과 partial trace/실제 앱 확인이 필요하다.
이번 결과를 보고 prompt를 수정하면 heldout을 calibration으로 재분류한다.

## 재현·보관·실행하지 않은 확인

```powershell
./scripts/download-probe-models.ps1 -Catalog benchmarks/translation-research-models.json -ModelId hy-mt2-1.8b-q4_k_m
./scripts/download-translation-runtime.ps1
./models/tabby/venv/Scripts/python.exe -X utf8 scripts/compare-exaone.py --candidate hymt2 --server models/runtime-b11146/llama-server.exe --sampling greedy --verify-input-contract --fixtures benchmarks/translation-exaone-ablation-fixtures.json
```

비교 결과: `benchmarks/results/hymt2-comparison-20261002-165317-cebfc8/`.
540행 익명 CSV와 별도 mapping의 독립 사람 annotation은 PENDING이다.
`partition-timing.json`은 원본 report의 기간을 partition별로 후처리한 자료다.
EOS 상세 결과: `benchmarks/results/translation-context-20261002-165756-2cb0d8/`.
앞선 EOS 진단 `165622-44c52f`도 보존했다. 두 진단의 native/chat 응답은 비교 집계에
넣지 않았다. 원문/모델/ZIP/full 결과는 Git 제외다.
단위/통합 테스트·장기 취소/종료·partial trace·앱·동시 게임·macOS는 실행하지 않았다.
