# 실제 ASR/HTTP paced PCM 비교 — Windows, 2026-10-03

## 실행 환경과 범위

Windows / RTX 3080, Whisper base CUDA(8 threads), 기존 b11146 llama.cpp를 사용했다.
모델을 하나씩 로드하고 기존 승인된 TTS mono 16 kHz PCM을 실시간 속도로 공급했다.
입력 파일/model/server/worker 해시를 보존했고 다운로드는 없다.

Qwen은 앱의 standard 요청(temperature 0.2), Hy-MT2는 앱의 hymt2-greedy 요청이다.
모델별 현재 입력 구성 비교이며 같은 sampling으로 모델 자체만 분리한 실험은 아니다.
Hy 전용 template에는 `--jinja`를 사용했다. HTTP만 warmup했고 각 native owner는 새로
시작했다. owner 준비 시간을 제외한다. 파일 끝을 알고 final 요청하며 VAD/캡처/앱/게임은 없다.

조건은 같은 adaptive padded ASR에서 supported preview off/on이다. 각 3회 순서를
교대했다. until은 Hy→Qwen, if는 Qwen→Hy로 실행했으나 같은 문장 내 모델 순서를
완전히 균형화하지 않았다. 2문장×2모델×2조건×3회=24회 모두 최종 완료했다.

## 명령

```powershell
scripts/probe-paced-translation.ps1 -Backend cuda -Rounds 3 -TranslationModel hymt2 -SupportedPreview -FixtureManifest J:/MyProject/VortexSub/benchmarks/fixtures/local-tts/preview-risks/manifest.json -FixtureId until-condition -OutputDir J:/MyProject/VortexSub/benchmarks/results/paced-hymt2-until-20261003
```

같은 명령에서 모델은 qwen/hymt2, fixture는 until-condition/if-condition으로 변경했다.
Qwen에도 `-ServerPath J:/MyProject/VortexSub/models/runtime-b11146/llama-server.exe`를 지정했다.
전체 회귀는 `$env:ECHOSUB_OFFLINE='1'; & scripts/check.ps1`로 실행했고 통과했다.
native 파일 검사는 CUDA/native-vad feature의 명시적 ignored probe를 실행했다.

## 결과 — 모든 시간은 초, 각 행 n=3 중앙값

| 음성 | 모델 | supported | 첫 번역 결과 | 최종 결과 |
|---|---|---|---:|---:|
| until | Qwen | off | 1.182 | 3.034 |
| until | Qwen | on | 1.182 | 3.047 |
| until | Hy-MT2 | off | 1.153 | 3.039 |
| until | Hy-MT2 | on | 1.185 | 3.058 |
| if | Qwen | off | 3.149 | 3.429 |
| if | Qwen | on | 3.179 | 3.429 |
| if | Hy-MT2 | off | 3.061 | 3.428 |
| if | Hy-MT2 | on | 3.103 | 3.410 |

음성 파일 길이는 until 2.875초, if 3.235초다. 시각은 PCM 재생 시작 기준이며
파일의 TTS padding을 포함한다. 첫 의미 단위/음성 onset annotation이나 화면 시각은 아니다.
범위/min/max는 원본 summary에 보존했다. n=3에서 p95 일반화를 하지 않는다.

if 문장의 번역 접수 중앙값은 약 2.927~2.942초, 접수→결과는 약 0.133~0.241초다.
초기 ASR은 `Attack`/`Attach`, `shield is not`/`shield is down` 등으로 바뀌었다.
안정 구간 없음과 IncompleteCondition 보류 후 약 2.9초에 번역을 요청했다.
이 사례의 대기는 번역 모델 속도보다 ASR 변동과 안정/조건 보류가 크다.
부분 전사 오류를 감춘 채 조건 보류를 해제하는 개선은 하지 않는다.

## 좁은 최종 의미 검토

작성 원문: `Do not open the door until I return.`
모든 최종 ASR은 원문과 같았다. Qwen 6회는 `나가기 전까지` 또는 `나가기 전까지는`으로
return을 다른 사건으로 변경했다. Hy 6회는 `내가 돌아올 때까지 문을 열지 마세요.`였다.
if 원문 `Attack the enemy only if the shield is down.`은 두 모델 각 6회에서 최종 조건과
대상을 보존했다. assistant의 비익명 회귀 검토이며 독립 사람 평가가 아니다.

이 2개는 이전에 사용한 합성 자료다. 새 holdout 품질 통과/언어 전체 오류율/기본 모델
전환의 근거로 확대하지 않는다. supported on은 이번 자료에서 첫 결과가 빨라지지 않았다.
구성 변경 없이 더 넓은 자료를 확인한다. 최종 번역 검토가 모든 부분 번역의 무오역을 뜻하지 않는다.

## 증거와 다음 단계

각 `benchmarks/results/paced-{qwen|hymt2}-{until|if}-20261003/`에 runtime/report,
native/server 로그와 장치 전체 GPU 표본을 보존했다. GPU 값은 프로세스별 메모리가 아니다.
통합 `paced-model-review-20261003/summary.json`, `review.csv`, `full-check.log`는 Git 제외다.
시각은 같은 worker clock의 사건으로 비교하고 서로 다른 clock의 중앙값을 합하지 않았다.

다음은 ASR의 표기 변화/단어 교체/부정 변경을 분류해 안정 구간 지연을 검토하는 것이다.
자연 음성 파일→앱 표시, 독립 영어/일본어 품질, 장시간/게임·macOS·물리 화면 검증은 남는다.
