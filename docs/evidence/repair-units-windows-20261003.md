# 정정 번역 단위 수정 — Windows, 2026-10-03

## 재현과 실패한 후보

이전 72회 비교에서 base의 `No.` 단독 번역이 방향 정정 6/6회 `번호.`로 나왔다.
small은 3/6회 `Take the left path, no`를 미완성으로 번역했다.
생산 Pipeline fixture에 두 입력 경로를 추가하고 수정 전 실패를 확인했다.

처음에는 `No. Take the right path.`를 한 단위로 묶었다. 실제 base 재생의
부분 결과 5회는 여전히 `번호가 있으면 오른쪽 길로 가세요.`였다.
나머지 1회는 그 부분 결과 없이 final이 적용됐다. 단위 묶기만으로 충분하지 않았다.
이 후보의 로그도 보존하며 성공으로 계산하지 않는다.

## 최종 구현

`crates/pipeline-core/src/units.rs`의 영어 preview 선택을 수정했다.

- 같은 구간의 처리된 문장 뒤 `No.`/`No!`가 나타나면 다음 안정된 문장 경계를 기다린다.
- 완성되면 직전 처리 단위까지 실제 번역 원문에 포함해 정정 단위를 다시 번역한다.
  예: `Take the left path. No. Take the right path.`
- `..., no`와 쉼표로 나뉜 정정 절이 미완성이면 `IncompleteRepair`로 보류한다.
  안정 접두사 끝 쉼표 뒤의 관측 `no`는 보류에만 사용한다.
- 다시 번역하는 원문도 384 bytes를 넘지 않는다. 초과하면 `RepairTooLong`으로
  preview를 보류하고 final 전체 원문 번역 경로를 유지한다.

관측 꼬리를 원문으로 승격하지 않는다. 영어의 제한된 표기 규칙이며 의미 분류기는
아니다. 같은 구간의 정상 답변이 정정 표지처럼 보이면 preview가 보류될 수 있다.
단독 응답 `No.`의 final, `The answer is no.`, `No, thank you.`, 새 identity와
일본어 단위 선택을 검사했다. 실제 부정 응답의 모델 오역까지 해결한 것은 아니다.
세션/epoch와 prefix 변경 시 기존 reset·stale 처리, final 전체 번역은 유지한다.
앱 기본 ASR·번역 모델·supported preview 옵션 기본값은 변경하지 않았다.

## 실제 재생 — 각 조건 n=3, 시간은 초

Windows / RTX 3080, 기존 Whisper base/small CUDA, Hy-MT2 greedy,
b11146 llama.cpp를 사용했다. 다운로드는 없다. 동일 Zira 합성 영어 WAV 4.030초를
실시간 속도로 공급했다. 파일 끝을 알고 final을 요청하며 HTTP만 warmup했다.
ASR owner 준비 시간은 제외한다. 같은 수정본 worker/server/profile/입력 해시로
base→small, 각 supported off/on 교대 3회, 총 12/12 final 완료했다.

| ASR | supported | 첫 부분 결과 | 오른쪽 정정 첫 결과 | final |
|---|---|---:|---:|---:|
| base | off | 1.140 | 3.332 | 4.201 |
| base | on | 1.146 | 3.918 | 4.253 |
| small | off | 1.245 | 3.342 | 4.245 |
| small | on | 1.284 | 3.366 | 4.239 |

중앙값은 PCM 시작 시각 기준이며 padding을 포함한다. 45개 번역 갱신을 검토해
`No.` 단독 입력, `No. Take ...`만의 입력, 미완성 `..., no` 입력과 `번호` 출력을
확인했고 모두 0건이었다. 12회 모두 오른쪽 정정과 final 번역을 보존했다.
base는 `왼쪽 길을 가세요. 아니요. 오른쪽 길을 가세요.`, small은
`왼쪽 길을 가세요, 아니, 오른쪽 길을 가세요.`로 정정했다.
비익명 assistant 검토이며 독립 품질 gate는 아니다.

속도 개선 일반화보다 중간 정정 오역 제거가 이번 수용 목적이다. 이전 문장을
다시 번역하므로 요청 원문/연산량이 늘 수 있다. 미래 정정을 예측하지 않으며
정정 이전의 왼쪽 지시는 계속 먼저 표시될 수 있다. 실제 앱/물리 화면·자연 음성·
VAD/캡처·게임/30분·다른 언어/모델은 이번 재생의 검증 범위가 아니다.

## 명령과 증거

```powershell
cargo test -p echosub-pipeline-core --locked --offline repair_ -- --nocapture
scripts/probe-paced-translation.ps1 -Backend cuda -Rounds 3 -TranslationModel hymt2 -AsrModel base -SupportedPreview -FixtureManifest J:/MyProject/VortexSub/benchmarks/fixtures/local-tts/preview-risks/manifest.json -FixtureId direction-repair -OutputDir J:/MyProject/VortexSub/benchmarks/results/paced-repair-v2-base-direction-20261003
$env:ECHOSUB_OFFLINE='1'; & scripts/check.ps1
```

small은 별도 OutputDir와 `-AsrModel small`로 실행했다. 정정 관련 4개 테스트,
core fixture 전체 40개와 전체 check(Rust tests/build, C# build/IPC,
표시 상태 41/63/26 assertions)가 최종 수정본에서 통과했다.
실제 화면 렌더/연속 프레임 검사는 이 check에 포함되지 않는다.

Git 제외 `benchmarks/results/paced-repair-v2-{base|small}-direction-20261003/`에
runtime/report/native/server/GPU 로그가 있다. 통합은
`repair-unit-review-20261003/summary.json`, `translation-review.csv`,
전체 검사는 `repair-unit-v2-full-check-20261003.log`다.
폐기 후보는 `paced-repair-{base|small}-direction-20261003/`에 보존했다.
후속은 실제 정정 결과와 앱의 두 줄 표시/읽기 보호 연결 회귀이며 자연 음성 수용은 별도다.
