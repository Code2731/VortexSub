# ASR 불안정 분류와 Whisper small 비교 — 2026-10-03

## 환경과 범위

Windows / RTX 3080, 기존 승인된 Whisper base/small CUDA와 Hy-MT2 greedy,
b11146 llama.cpp를 사용했다. 새 다운로드는 없다. 같은 합성 영어 PCM을 실시간
속도로 공급하고 생산 스케줄러/실제 HTTP를 사용했다. HTTP만 warmup하며 새 ASR
owner의 준비 시간은 제외한다. 파일 끝을 알고 final을 요청한다.
자연 음성·VAD/캡처·앱/물리 화면·게임·장시간 검증은 아니다.

## 기존 24회 전사 분석

`scripts/summarize-asr-stability.py`로 이전 두 조건문 기록을 분석했다.
132개 전환은 동일 36개, 단어 확장 36개, 단어 교체 60개였다.
대소문자/공백만 변경 또는 문장부호만 변경은 각각 0개다.
if 문장에서 부정 토큰 수 변화는 24개 전환이었다. 반복 실행의 어휘 신호이며
24개의 독립 의미 오류를 뜻하지 않는다. 보류 관측 건수는 대기 시간이 아니다.
따라서 표기 정규화로 지연을 줄일 근거는 이번 자료에서 나오지 않았다.

## 발견한 조건 누락과 수정

small은 `Attack the enemy only.`를 관측했는데 안정 구간 `Attack the enemy`를
먼저 번역했다. 약 1.24~1.29초의 첫 결과 `적을 공격하라`에는 조건이 없었다.
이를 정상적인 속도 개선으로 계산하지 않는다.

생산 fixture에서 실패를 재현한 뒤, 관측 suffix의 `only`를 부분 번역 보류에
포함하고 구두점을 제거해 검사했다. `only.`로 끝난 부분 전사도 미완성 조건으로
보류한다. 관측 suffix를 번역 입력으로 추가하지 않는다. 완성된 목적어 표현
`Take only the blue key.`와 final 입력은 계속 허용한다.
아직 관측하지 않은 미래 조건을 예측하는 의미 판별기는 아니다.

## 같은 수정본 비교 — 초, 각 행 n=3 중앙값

음성은 `Attack the enemy only if the shield is down.`(3.235초)이다.
supported preview off/on을 교대하며 base/small 각각 6회 실행했다.

| ASR | supported | 첫 조건 포함 번역 | 최종 번역 |
|---|---|---:|---:|
| base | off | 3.033 | 3.394 |
| base | on | 3.086 | 3.404 |
| small | off | 2.829 | 3.468 |
| small | on | 2.832 | 3.473 |

수정 후 12/12 실행의 첫 번역과 최종 번역은 조건을 포함했다.
small은 첫 결과가 약 0.204/0.254초 빨랐지만 최종 결과는 약 0.074/0.069초
늦었다. PCM 시작 기준이며 TTS padding을 포함한다. 음성 onset이나 화면 지연은
아니다. 한 합성 문장, 순차 모델 실행, 비익명 assistant 검토이므로 독립 품질
gate나 p95 일반화 근거가 아니다. 앱의 기본 ASR은 base로 유지한다.

## 재현과 검증

```powershell
scripts/probe-paced-translation.ps1 -Backend cuda -Rounds 3 -TranslationModel hymt2 -AsrModel small -SupportedPreview -FixtureManifest J:/MyProject/VortexSub/benchmarks/fixtures/local-tts/preview-risks/manifest.json -FixtureId if-condition -OutputDir J:/MyProject/VortexSub/benchmarks/results/paced-whisper-small-guarded-if-20261003
models/tabby/venv/Scripts/python.exe -X utf8 tests/test_asr_stability.py
cargo test -p echosub-pipeline-core --locked --offline only_ -- --nocapture
$env:ECHOSUB_OFFLINE='1'; & scripts/check.ps1
```

base는 위 명령의 `-AsrModel base`와 별도 OutputDir로 실행했다.
분류 테스트 2건/11 assertions, 조건 관련 fixture 6건, 전체 check가 통과했다.
전체 check에는 Rust workspace tests, C# build/IPC, 표시 상태 41/63/26 assertions가 포함된다.

Git 제외 `benchmarks/results/asr-stability-{base,small,review}-20261003/`,
`paced-whisper-{small,small-guarded,base-guarded}-if-20261003/`에
분류 CSV, 비교 summary, runtime/입력/모델 해시, native/server 로그를 저장했다.
전체 검사 로그는 `benchmarks/results/asr-stability-full-check-20261003.log`다.
다음은 다른 조건문/자연 음성으로 같은 비교를 넓히고 파일→앱과 P3.2로 진행한다.
