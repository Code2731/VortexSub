# DTW 정렬과 파일 재결합 (2026-10-02)

## 구현과 근거

고정 의존성 whisper-rs 0.14.4 / whisper.cpp 1.7.4의 로컬 소스를 확인했다.
`t_dtw`는 실험적 토큰 발화 시점이며 시작·끝 interval이 아니다.
[공식 header](https://github.com/ggml-org/whisper.cpp/blob/v1.7.4/include/whisper.h),
[공식 DTW 구현](https://github.com/ggml-org/whisper.cpp/blob/v1.7.4/src/whisper.cpp).
구현은 DTW index를 20 ms 해상도로 시간에 대응시킨다. 이를 별도 optional
`Token.dtw_ms`로 추출하며 일반 엔진은 None으로 유지한다.

명시적 base DTW load만 정렬을 켠다(128 MiB DTW buffer 설정, flash attention off).
multilingual/base 차원을 검사하며 기존 일반 load·worker IPC/옵션은 바꾸지 않았다.
byte/UTF-8 coverage·시간 범위/순서를 검사하고 마지막 lexical token의 landmark를
기준으로 후보를 만든다. 기존 길이 0 interval은 계속 거부한다.
문장부호의 늦은 landmark로 발화 끝을 늘리지 않는다. 두 관측 일치와 최소 두
단어 overlap의 정확한 텍스트/발화점 재확인을 거쳐 전체 원문을 재결합한다.

## 실제 파일 측정

Windows·RTX 3080·기존 Whisper base CUDA·8 threads, 7.605초 합성 영어 파일.
normal/dtw를 각 3회 별도 프로세스로 실행했다. 순서는 normal/dtw,
dtw/normal, normal/dtw다. 각 실행은 3초 prefix, 4초 prefix, 전체를 전사하고,
후보가 있으면 축소 입력을 전사했다. 모델 load는 제외하며 별도 ASR warmup은
없다. GPU의 다른 작업을 격리하지 않았다.

| 중앙값 | 초 |
|---|---:|
| 일반 전체 전사 | 0.182729 |
| DTW 전체 전사 | 0.205880 |
| DTW 축소 전사 | 0.174018 |

일반은 6개 prefix 모두 `path`의 2.00→2.00초 interval로 거부됐다.
DTW의 해당 단어 landmark는 6개 모두 1.54초다. 3회 모두 0.928초를 제외해
6.677초 입력을 전사했으며, exact overlap 재결합 원문이 각 전체 전사와 같았다.
이는 알려진 첫 문장 prefix를 지정한 파일 검사이며 live agreement/revision,
번역·VAD·캡처·화면·게임은 포함하지 않는다. 정렬과 텍스트 일치가 제품 품질
통과를 의미하지 않으므로 quality_gate_passed=false를 유지한다.

DTW 전체 대비 축소는 약 0.031862초, 일반 전체 대비 약 0.008710초 차이다.
앞선 prefix 전사의 DTW 비용도 있어서 전체 실시간 처리량 개선을 주장하지 않는다.
짧은 합성 영어 한 파일과 소수 반복이며 자연/일본어 음성·CPU·macOS는 미검증이다.

재현: `scripts/probe-partial-scheduling.ps1 -Trim [-Dtw] -Backend cuda -WavPath <WAV>`.
조건별 명령을 3회 반복했다. Git 제외 `alignment-normal-round{1..3}-20261002`
및 `alignment-dtw-round{1..3}-20261002`의 JSON/log가 원본이다.
초기 DTW interval 거부와 단일 재결합 성공도 `dtw-window-*20261002`로 보관한다.
WAV SHA-256: `ec4391bf31bd0e983cbdd265344fa13ed6f1371c16c2a572b08dc54981101615`.

## 계약 확인과 다음

`cargo test -p echosub-worker -p echosub-pipeline-core --locked --offline`:
36 state fixture + 35 worker unit + 23 protocol = 94개 PASS.
DTW point/interval 분리, 누락·역행 landmark 거부, 재결합 후 실제 반복 보존,
anchor 시간 drift 거부를 fixture로 확인했다. Rust fmt/diff check PASS,
native CUDA/VAD release 빌드 및 6회 비교 완료. 전체 workspace/C# suite는 재실행하지 않았다.
계약 검사 원본은 `benchmarks/results/dtw-contract-check-final-20261002.log`다.

다음은 적용된 identity/revision에 한해서 mapping을 저장하고 owner의 축소 전사/
재결합 실패 시 전체 입력 재전사를 연결하는 작업이다. 확정 전사는 전체 입력을
유지하며 DTW overhead, 거부율과 자막 수정 빈도를 함께 측정한다.
