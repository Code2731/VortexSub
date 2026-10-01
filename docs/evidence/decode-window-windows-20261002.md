# Decode window 기반 작업 (2026-10-02)

## 범위

합의한 두 번째 단계의 첫 구현이다. 제품 range와 snapshot window를 분리하고
정렬·overlap 재결합의 fail-closed 코드를 파일 probe에 연결했다.
라이브 전사 입력 축소와 속도 개선은 아직 완료되지 않았다.
IPC/화면 설정·기본 실행 정책은 변경하지 않았다.

## 검증

- `cargo test -p echosub-pipeline-core -p echosub-worker --locked --offline`:
  state fixture 36개, worker unit 34개, protocol 23개 PASS(총 93개).
- 신규 fixture: window의 실제 PCM 512 samples/제품 범위 1024 samples 분리,
  invalid window의 무변경 거부, 확정 우선과 stale revision 무시.
- 신규 alignment fixture: 절대 시간 offset, 정확한 overlap 재결합,
  이후 실제 반복 보존, 잘못된 byte coverage·길이 0 단어·시간 drift 거부.
- 기존 native CUDA/VAD release 빌드와 명시적 파일 probe 완료.
- Rust formatting과 `git diff --check` PASS.

실제 probe:

```powershell
scripts/probe-partial-scheduling.ps1 -Trim -Backend cuda `
  -WavPath benchmarks/results/paced-translation-20261002-011456-a03c0c/en-joined.wav `
  -ReportPath benchmarks/results/decode-window-20261002.json
```

Windows·RTX 3080·Whisper base CUDA·8 threads, 기존 합성 영어 7.605초.
새 모델/런타임 다운로드 없이 3초/4초 prefix와 전체 파일을 각각 한 번 전사했다.
prefix 원문은 `We should take the left path.`다.
두 prefix 모두 native ` path` token의 시간이 2.00→2.00초여서
`InvalidOrZeroLengthWordTime`으로 거부했다. trimmed decode/merge는 실행하지 않았다.
전체 전사 시간은 0.097690초, 모델 load는 제외했다. 이는 단일 관측이며
이전 라운드와의 성능 비교가 아니다. 정상 종료는 trim 품질 통과를 뜻하지 않는다.

WAV SHA-256: `ec4391bf31bd0e983cbdd265344fa13ed6f1371c16c2a572b08dc54981101615`.
Git 제외 원본: `benchmarks/results/decode-window-20261002.json`,
`benchmarks/results/decode-window-native-20261002.log`.

실제 게임·자연/일본어 음성·화면·HTTP 번역·macOS는 이번 검증에 포함하지 않았다.
전체 workspace/C# suite는 재실행하지 않았다. 다음은 단어 시간 정렬 확보,
적용된 identity/revision 기준 mapping, native owner 재결합/전체 입력 fallback이다.
