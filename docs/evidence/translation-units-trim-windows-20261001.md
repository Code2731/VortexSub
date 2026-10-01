# 문장 단위 번역 비교와 오디오 trim 가능성 (2026-10-01)

## 조건과 재현

Windows, 기존 Whisper base CPU 8 threads와 Qwen3 4B Instruct 2507
Q4_K_M/llama.cpp GPU 서버를 사용했다. 자산 추가 다운로드는 없다.
기존 합성 영어 3개 파일을 CPU Whisper로 독립 prefix 전사한 뒤, 동일한
전사 기록을 현재 worker와 `27aae55`의 누적 prefix worker에 재생했다.
일본어 2개는 작성한 가설이며 실제 일본어 ASR이 아니다.

```powershell
scripts/probe-streaming-translation.ps1 -NoBuild -ReferenceWorker target/unit-reference/debug/echosub-worker.exe
scripts/probe-partial-scheduling.ps1 -Trim -WavPath benchmarks/results/streaming-translation-20261001-093831-71c7ed/en-joined-prefix-10.wav
```

참조 worker는 `git archive 27aae55`를 Git 제외 결과 폴더에 풀고,
`cargo build --manifest-path <reference>/Cargo.toml -p echosub-worker --locked --offline --target-dir target/unit-reference`
로 만들었다. 현재 checkout은 유지했다. `-NoBuild`는 이미 빌드한 probe와
worker가 필요하다. trim은 이 영어 문장이 포함된 기존 7.605초 fixture 전용이다.

단위 방식→prefix 방식 순서로 조건별 1회, temperature 0.2로 실행했다.
양쪽 모두 확정 전용 대조군을 포함한다. 서버 준비/워밍업 시간은 제외했다.
실제 HTTP·production 번역 pipeline을 쓰지만 ASR 전달은 MOCK이며,
native scheduler·VAD·캡처·화면 렌더링은 비교에 포함하지 않았다.
전사 가용 시점은 파일 끝 시간+독립 decode 시간으로 추정했다.

처음 sandbox 실행은 서버 실행 권한 오류(WinError 5)로 실패했다.
기존 런타임 실행을 허용한 재실행은 성공했고, 소유 서버 종료와 임시 key 삭제를 수행했다.

## 번역 결과

시간은 replay 시작 기준 초다. 요청/관측 횟수는 확정 번역을 포함한다.

| 영어 사례 | 기존 첫 임시 / 확정 | 단위 첫 임시 / 확정 | 기존→단위 HTTP / 관측 업데이트 |
|---|---:|---:|---:|
| en-01 | 없음 / 3.316 | 없음 / 3.381 | 2→2 / 1→1 |
| en-03 | 3.342 / 3.795 | 3.364 / 3.800 | 2→2 / 2→2 |
| en-joined (7.605초) | 3.352 / 8.942 | 3.409 / 8.918 | 9→5 / 8→5 |

긴 음원 요청은 44% 감소했다. 첫 임시 번역의 속도 개선은 관측하지 못했다.
단위 방식의 긴 음원 확정 전용은 9.047초, 임시 사용은 첫 출력 3.409초다.
이는 임시 출력의 조기 제공이며 전체 확정 완료의 큰 가속을 뜻하지 않는다.
관측 업데이트는 IPC snapshot 기준이며 카드 수정/읽기 시간의 실측이 아니다.
한 번씩의 비결정적·순서 고정 실험으로 작은 시간 차이를 일반화하지 않는다.

### 의미 검토: 품질 gate false

- `There are three` → 양쪽 모두 “세 개가 있습니다.”: 대상 없는 미완성 정보.
- 단위 `There are three enemies near the gate.` → “구문 근처에 세 명의 적이 있다.”:
  gate 오역. 기존은 “문 근처에 세 명의 적이 있습니다.”였다.
- 단위 `Do not open the door until I` → “나를 보지 않기 전에 문을 열지 마십시오.”:
  미완성 조건의 오역. 확정 `until I return`도 “나가기 전까지”로 잘못 옮겼다.
- 작성 일본어 귀환 조건의 단위 확정은 “문을 열지 마십시오.”로 조건을 누락했다.
- 작성 일본어 긍정→부정 수정은 먼저 건너라는 임시 출력 후 부정으로 바뀌었다.
  단위 확정은 “다른 다리”라는 원문 없는 수식어도 추가했다.

단위 분리는 요청 중복을 줄이지만 의미 안정성을 보장하지 않는다.
닫히지 않은 tail의 조기 번역과 모델 오역을 구분해 후속 비교한다.
기본 활성화 및 제품 품질 수용은 보류한다.

## 오디오 trim: 적용 보류

명시적 ignored 파일 probe는 3초/4초 prefix의 정확한 원문·token byte coverage,
UTF-8 경계·시간 순서·단어의 양수 길이를 검사한다. 두 경계가 0.16초 이내로
일치할 때만 앞쪽 경계에서 0.3초 overlap을 남기고 tail을 재전사한다.

두 전사 모두 첫 문장의 ` path` token이 2.00초의 길이 0으로 기록돼
`InvalidOrZeroLengthWordTime`으로 거부했다. 전체 7.605초 timed decode는
약 0.703초였으며 trimmed decode는 실행하지 않았다. 이 측정의 정상 종료는
trim 성공을 뜻하지 않는다. coarse segment는 다음 문장의 일부/전체까지 포함해
대신 자르는 기준으로 채택하지 않았다.

라이브 trim은 미구현이다. worker가 alignment를 원문 정규화 뒤까지 보존하는
유한 mapping, 제품 오디오 범위와 decode window 분리, overlap 재결합 및
전체 확정 원문 보존이 선행돼야 한다. 문자 비율로 시간을 추정하지 않는다.

## 보관과 다음 작업

이번 확인: native CPU/VAD release 및 C# replay client 빌드, 명시적 native
trim 파일 측정, 두 worker의 실제 HTTP replay 정상 종료. `cargo fmt --all -- --check`,
`cargo build -p echosub-worker --locked --offline`, Python/PowerShell 구문 검사와
`git diff --check` PASS. 전체 회귀 suite와 화면 검사는 이번 라운드에 재실행하지 않았다.

Git 제외 원본: `benchmarks/results/streaming-translation-20261001-192918-a74d88/`
(`report.json`, `prefix-reference.json`, `comparison.json`, trace/해시/log)와
`benchmarks/results/partial-scheduling-20261001-193025-20664/report.json`.
trace SHA-256: `1672295c928469afcd3c329d50b848a6ea561d8122505bacceae3ebad0e909f7`.

다음은 기존 Whisper CPU/CUDA의 paced partial 비용 비교와 위 오류 사례의
품질 회귀 기준 정리다. 새로운 ASR/소형 번역 모델 비교는 이후 자산 동의를
받아 진행한다. 자연/일본어 음성·게임 경합·음성→화면 지연·macOS는 미검증이다.
