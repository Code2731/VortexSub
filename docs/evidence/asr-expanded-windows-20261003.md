# Whisper base/small 비교 확장 — Windows, 2026-10-03

## 구성과 실행

Windows / RTX 3080, Whisper CUDA base/small, Hy-MT2 greedy, b11146 llama.cpp.
이전 라운드의 `only` 보호 수정본을 그대로 사용했다. 새 다운로드는 없다.
기존 해시 검증된 Microsoft Zira 합성 영어 6문장을 실시간 PCM으로 공급했다.
문장×ASR×supported preview off/on×3회=72회 모두 최종 번역 완료했다.
문장마다 base→small / small→base 순서를 번갈아 실행했고 off/on은 probe가 교대했다.
각 모델을 따로 로드하므로 동시 모델 실행 비교는 아니다.

```powershell
scripts/probe-paced-translation.ps1 -Backend cuda -Rounds 3 -TranslationModel hymt2 -AsrModel base -SupportedPreview -FixtureManifest J:/MyProject/VortexSub/benchmarks/fixtures/local-tts/preview-risks/manifest.json -FixtureId until-condition -OutputDir J:/MyProject/VortexSub/benchmarks/results/paced-expanded-base-until-condition-20261003
```

ASR은 base/small, fixture는 아래 6개로 바꿨다. 동일 worker/server/profile/manifest
해시와 문장별 동일 WAV 해시를 집계 도구에서 확인했다. HTTP만 warmup하고 각 ASR
owner는 새로 시작한다. 준비 시간은 제외하며 파일 끝을 알고 final을 요청한다.
자연 음성·VAD/캡처·앱/화면·게임·장시간 확인은 아니다.
현재 관리되는 fixture manifest는 합성 자료뿐이어서 자연 음성 비교는 수행하지 못했다.

## 결과 — 초, 각 조건 n=3 중앙값

각 셀은 supported **off / on**이다. 시각은 PCM 재생 시작 기준으로 TTS padding을
포함한다. 첫 결과가 완전한 문장 또는 의미가 정확한 번역임을 뜻하지 않는다.

| 문장 종류 / fixture | base 첫 번역 | small 첫 번역 | base 최종 | small 최종 |
|---|---:|---:|---:|---:|
| until 조건 / until-condition | 1.169 / 1.205 | 2.806 / 1.238 | 3.029 / 3.036 | 3.087 / 3.102 |
| 부정 / negation | 1.725 / 1.771 | 1.997 / 1.987 | 2.459 / 2.441 | 2.480 / 2.484 |
| 숫자 정정 / number-repair | 1.725 / 1.734 | 1.783 / 1.772 | 3.911 / 3.901 | 3.938 / 3.940 |
| 방향 정정 / direction-repair | 1.150 / 1.175 | 1.230 / 1.241 | 4.263 / 4.244 | 4.268 / 4.298 |
| 대상 대비 / object-contrast | 1.717 / 1.179 | 1.752 / 1.229 | 3.362 / 3.356 | 3.431 / 3.412 |
| 일반 문장 / complete-object | 1.757 / 1.757 | 1.754 / 1.764 | 2.430 / 2.403 | 2.479 / 2.467 |

small은 대부분의 첫 결과에서 더 빠르지 않았다. until 조건 전체를 포함한 결과는
base 약 2.770/2.784초, small 약 2.806/2.839초였다. 방향 정정 전체 입력을 처음
번역한 시점은 base의 final 약 4.263/4.244초, small 약 3.381/3.852초로 달랐다.
문장과 부분 전사/분할에 따른 상충 관계이며 이전 if 문장의 개선을 일반화하지 않는다.
n=3의 p95나 자연 음성 효과는 주장하지 않는다. 기본 ASR은 base로 유지한다.

## 부분 결과의 문제 — 최종만 보면 놓치는 사례

214개 번역 갱신의 고유 source→target 23쌍과 최종 결과를 비익명으로 검토했다.
최종 72건은 이 자료의 부정·조건·숫자·방향 정정을 보존했다. assistant의 좁은
검토이며 독립 사람 평가나 새 holdout 품질 gate가 아니다.

- base 방향 정정 6/6회에서 `No.`가 독립 단위로 전송되어 `번호.`로 번역됐다.
  전체 final은 `왼쪽 길을 가세요. 아니요. 오른쪽 길을 가세요.`로 교정됐다.
- small 방향 정정 3/6회는 `Take the left path, no`를 번역해
  `왼쪽 길을 가세요, 아니요`를 먼저 출력했다. 정정 방향이 아직 없는 중간 표현이다.
- small 부정문 supported on 1회는 `guard`가 아직 안정되지 않은
  `You must not attack them.`을 번역했다. 부정은 보존했지만 대상은 임시 가설이었다.
- 숫자 13/30, engine/shield 대비와 until 조건의 최종 의미는 보존됐다.
  앞부분을 먼저 내보내는 것은 미래의 정정을 예측한 결과가 아니다.

다음 구현은 관측된 정정 표지 `No.`를 다음 절과 함께 처리하는 번역 단위 설계다.
정정 절이 아직 미완성이면 보류하고, 완성되면 제한된 크기 안에서 묶는 후보를
fixture로 검증한다. 정상적인 단독 응답 `No.`와 final 전체 번역을 함께 검사한다.
모든 짧은 문장 차단이나 모델 기본 전환으로 이 문제를 처리하지 않는다.
이번 라운드에서는 생산 단위 선택 규칙을 변경하지 않았다.

## 도구·증거·남은 검증

`scripts/summarize-paced-asr-comparison.py`를 추가했다. 구성/입력 해시가 다른
비교를 거부하고 조건별 시간 n/min/median/max, 최종 완료 수, 수동 검토 CSV를 저장한다.
누락 시간을 0으로 계산하지 않는다. reference와 단어가 같은 최초 입력 번역 시각은
의미 평가가 아니다. `thirteen`/`13`은 이 표면 비교에서 다르므로 숫자 문장의
reference 일치 시각은 null이며 오류로 판정하지 않는다.

분류 결과는 294개 전환(단어 확장 141, 동일 98, 단어 교체 55)이었다.
대소문자/공백만 또는 문장부호만 바뀐 전환은 없었다. 보류 건수는 시간이 아니다.
집계 도구 stdlib 테스트 2건/11 assertions 통과, 실제 12 report 집계 성공.
Rust/C# 생산 변경은 없으며 전체 check는 이번 라운드에서 재실행하지 않았다.

Git 제외 `benchmarks/results/paced-expanded-{base|small}-{fixture}-20261003/`에
각 runtime/report/native/server/GPU 로그가 있다. 장치 GPU 표본은 프로세스별 메모리가 아니다.
`paced-expanded-review-20261003/summary.json`, `translation-review.csv`와
`asr-stability-expanded-20261003/summary.json`, `transitions.csv`에 집계했다.
기존 합성 자료 반복이므로 독립 영어/일본어 품질, 자연 음성→앱, 물리 화면 프레임,
세션 재시작·30분/게임·macOS 확인은 남는다.
