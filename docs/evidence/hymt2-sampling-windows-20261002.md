# Hy-MT2 greedy·권장 sampling 비교

2026-10-02, Windows / RTX 3080 10 GB, P1.2 / RI-01/02/06.
기존 공식 Hy-MT2 Q4_K_M과 llama.cpp b11146을 재사용했다. 새 다운로드와 앱 기본
변경은 없다. 입력은 공식 user/background 형식이며 prompt를 재보정하지 않았다.

## 구성과 실행

| 설정 | temperature | top_p | top_k | repeat_penalty |
|---|---:|---:|---:|---:|
| greedy | 0 | 1 | 1 | 1 |
| 권장 sampling | 0.7 | 0.6 | 20 | 1.05 |

권장 값의 근거는 [고정 공식 카드](https://huggingface.co/tencent/Hy-MT2-1.8B/blob/9a341cd1b679d3efd23b46e847b01745a71ed792/README.md)다.
두 구성 모두 min_p 0, seed 42, max_tokens **256**, context 4096, parallel 1이다.
공식 카드의 max_tokens 4096까지 그대로 적용한 구성은 아니다. 실시간용 출력
상한은 유지하고 추가 min_p 필터는 껐다. seed가 모든 실행 환경의 결정론을 뜻하지 않는다.

`probe-translation-context.py/.ps1`에 Hy-MT2 전용 `--compare-sampling` /
`-CompareSampling`과 단독 `--sampling hymt2-recommended` /
`-Sampling hymt2-recommended`를 추가했다. 다른 모델에는 권장 설정을 거절한다.
실제 설정별 body/fingerprint와 runtime의 `sampling_configurations`를 보존한다.

공식 Hy-MT2 하나를 로드하고 90항목 × 2구성 × 3회 = **540응답**을 실행했다.
warmup 2개는 집계에서 제외했다. fixture/입력 계약은 앞선 비교와 같다.
모든 입력의 로컬/서버 template 토큰열과 chat 입력 개수가 일치했고 응답 실패 0이다.
같은 원문마다 두 구성의 첫/두 번째 실행을 교대해 `request_position`을 기록했다.
기존 prompt cache를 유지했으므로 요청 순서별 시간도 따로 집계했다.

```powershell
./models/tabby/venv/Scripts/python.exe -X utf8 scripts/probe-translation-context.py --catalog benchmarks/translation-research-models.json --model-id hy-mt2-1.8b-q4_k_m --profiles hymt2 --rounds 3 --server models/runtime-b11146/llama-server.exe --sampling greedy --compare-sampling --verify-input-contract --fixtures benchmarks/translation-exaone-ablation-fixtures.json
```

결과: Git 제외 `benchmarks/results/translation-context-20261002-172007-76f983/`.
`sampling-summary.json`은 원본 report의 partition/언어/설정/실행 위치별 후처리다.
540행 익명 CSV와 별도 mapping의 독립 사람 annotation은 PENDING이다.

## heldout 처리시간과 반복 일치

각 언어·설정 36응답, 문맥 짝 12 case, 고유 원문 6개다. 초 단위,
p95 nearest-rank. HTTP 수신 직후 측정하며 template/tokenize/warmup/기록 쓰기는
제외한다. 음성→자막 지연을 측정한 자료는 아니다.

| 언어 | 설정 | 중앙값 | p95 | 3회 출력 일치 case |
|---|---|---:|---:|---:|
| 영어 | greedy | 0.094 | 0.125 | 12/12 |
| 영어 | 권장 sampling | 0.094 | 0.156 | 9/12 |
| 일본어 | greedy | 0.094 | 0.140 | 12/12 |
| 일본어 | 권장 sampling | 0.109 | 0.125 | 10/12 |

두 구성 모두 언어당 첫/두 번째 요청이 각각 18개다. 영어 greedy의 중앙값은
첫 요청 0.109초, 두 번째 0.086초였고 권장 설정은 0.1015/0.094초였다.
캐시/실행 순서·출력 길이 차이가 있어 하나의 설정이 항상 빠르다는 결론을 내리지 않는다.
출력 일치는 재현성이지 정확도나 자막 수정량 지표가 아니다.

## 원문 대조

아래는 에이전트의 사례 대조이며 전체 blind 사람 품질 점수나 오류율은 아니다.

* 영어 `brass cog`가 greedy에서는 3/3회 bronze screw(브론즈 나사)로 바뀌었다.
  권장 설정은 3/3회 황동 기어로 보존했다. 구리로 정정하는 최종 지시도 유지했다.
* 일본어 수리공에게 수레를 고치게 하라는 related 입력은 두 설정 모두 3/3회
  “수리점/수리소에 마차를 고쳐서”로 바뀌었다. 행동 주체 오류는 해결되지 않았다.
* 일본어 약초 네 묶음 미만 related 입력은 권장 설정 1/3회에 “4봉지의 절반 이상”으로
  바뀌었다. 다른 2회와 이번 greedy 3회는 미만 경계를 유지했다.
* 직전 모델 비교의 greedy는 같은 수량 입력에서 과거 상황으로 바뀌었지만,
  이번 paired 실행의 greedy는 지시를 유지했다. 서로 다른 호출/캐시 이력의 차이를
  sampling 개선으로 돌리지 않는다. 실제 스트리밍 입력에서도 확인해야 한다.
* shelter는 greedy에서 캠핑장, 권장 설정에서는 시설/숙소로 바뀌었다.
  후자의 변화만으로 정확한 목적지 보존이 보장됐다고 판정하지 않는다.

권장 설정은 일부 용어를 개선했지만 의미 오류를 일관되게 줄인 것으로 확정할 수
없다. 영어 p95도 이번 자료에서 늘었다. 두 설정 모두 기본 채택은 보류한다.
이번에 본 자료로 설정을 선택한 뒤 같은 자료를 독립 채택 검증으로 다시 쓰지 않는다.

## 다음 단계

추가 prompt/sampling 탐색을 계속 늘리지 않고 P1.3의 고정 partial trace에서
두 구성의 첫 의미 단위·수정·최종 교정을 비교하는 경로로 넘어간다.
Qwen baseline을 함께 유지하며 새 검증 자료의 언어별 의미 판정을 분리한다.
생산 worker의 입력 계약이 Hy-MT2 probe와 다르므로 model_id만 교체해 연결하지 않는다.
앱/자연 음성/취소·재시작/동시 게임/장기 메모리/macOS는 미검증이다.
단위/통합 테스트는 실행하지 않았다.
