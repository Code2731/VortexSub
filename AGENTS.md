# Repository Guidelines

## Project Structure

The repository has an Avalonia C# UI in `apps/EchoSub.Desktop/`, a Rust process in `crates/worker/`, WASAPI owner/probe in `crates/capture-windows/`, reusable native adapter in `crates/asr-whisper/`, ASR harness in `crates/model-probe/`, and pure audio core in `crates/audio-core/`, state/queue core in `crates/pipeline-core/`, and VAD adapter in `crates/vad-silero/`. Translation diagnostics and model/fixture manifests live in `benchmarks/`. C# IPC smoke checks live in `tests/EchoSub.ProtocolSmoke/`; worker integration tests in `crates/worker/tests/`. IPC schema lives in `schemas/`; plans, requirements, status, and evidence live in `docs/`. Put the native ScreenCaptureKit bridge under `native/macos-capture/` only after its platform probe.

## Build, Test, and Development

Run `scripts/check.ps1` on Windows for formatting, Rust tests, both builds, and the C#↔Rust smoke check; run `bash scripts/check.sh` on macOS once a Mac is available. `scripts/run.ps1` builds the worker and opens the mock UI; `-Live` enables CPU source diagnostics (see `docs/LIVE_UI.md`). Use `cargo run -p echosub-capture-windows -- --list` to find render endpoints and `--seconds 600` for the loopback probe. The equivalent individual checks are `cargo test --workspace --locked` and `dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj`. The scripts pin the local package cache and disable Avalonia build telemetry. Record which OS and hardware each command actually covers.

For diagnostics, see `benchmarks/README.md`. Use `scripts/probe-asr.ps1 -Backend cpu|cuda`, `scripts/probe-translation.ps1`, `scripts/probe-cancellation.ps1`, `scripts/probe-contention.ps1`, `scripts/probe-worker-asr.ps1`, `scripts/probe-worker-vad.ps1`, `scripts/probe-worker-capture.ps1`, `scripts/probe-worker-live-asr.ps1 [-Sessions]`, or `scripts/probe-capture-startup.ps1`. Native ASR requires CMake/LLVM/MSVC and CUDA Toolkit for CUDA; default builds omit native inference. Obtain download consent before model/runtime downloads. Keep models, generated audio, and full benchmark results out of Git.

## Coding Style

Provider-independent translation contracts and the bounded HTTP owner live in `crates/translation/`; opt-in worker integration is documented in `docs/WORKER_TRANSLATION.md`. Use `scripts/probe-translation.ps1 -Contract -Offline` for the standalone HTTP owner or `-Worker -Offline` for native file ASR→HTTP→history.

Use Rust's standard formatting and idiomatic `snake_case` for functions/modules and `UpperCamelCase` for types. In C#, use four-space indentation, `UpperCamelCase` for public types and members, and `camelCase` for locals and parameters. Keep pure state and data contracts platform-independent; isolate WASAPI and ScreenCaptureKit behind platform adapters. Keep worker stdout reserved for NDJSON protocol messages and send diagnostics to stderr. Bound queues and avoid blocking, allocation-heavy, or inference work in audio callbacks.

## Tests and Validation

For IPC changes, extend `crates/worker/tests/protocol.rs` and `tests/EchoSub.ProtocolSmoke/`; run the check script. Add fixture tests with the audio pipeline in M1, including stale-result identity (`session_id`, `epoch`, `segment_id`, `source_revision`). No coverage percentage is specified. Label mocks clearly and record unrun platform/device checks explicitly in `docs/STATUS.md`.

## Commits and Pull Requests

Use short imperative Conventional Commit subjects such as `feat: add worker handshake` or `fix: reject stale translations`. Pull requests should explain scope and affected requirement IDs, list exact validation commands and results, identify untested platforms, and include screenshots for UI changes.

## Progress and Handoff

작업 시작 시 `docs/HANDOFF.md`와 `docs/STATUS.md`의 최신 항목을 읽는다.
이어서 관련 계획과 근거 문서를 확인한다. 대화 이력만으로 현재 상태를 판단하지 않는다.
매 라운드 종료 시 두 문서를 갱신한다. 중단하거나 차단된 작업도 기록한다.
`HANDOFF.md`에는 갱신 날짜, 현재 목표, 완료·진행·미착수 작업, 다음 구체적 작업,
검증 명령·결과·미검증 범위, 브랜치·기준 커밋·미커밋 상태를 기록한다.
`STATUS.md`에는 이번 변경과 검증 요약을 추가하고 상세 근거에 연결한다.
예전 기록을 최신 결과로 오해하지 않도록 날짜와 검증 범위를 구분한다.

다른 PC에서 실행할 명령은 저장소 루트 기준 경로를 사용한다.
필요한 도구, 모델·런타임 매니페스트, 로컬 설정과 Git 제외 산출물을 명시한다.
모델·로그·캐시가 Git으로 전달된다고 가정하지 않는다. 비밀값은 문서에 기록하지 않는다.
커밋·푸시 또는 파일 전달이 완료되지 않았으면 다른 PC에서 재개 가능하다고 표시하지 않는다.
문서 갱신 요청을 커밋·푸시 승인으로 해석하지 않는다.

### Current Focus (2026-10-05)

현재 작업은 배포를 위한 UI/UX 정리다. 설정 복원과 번역 연결 실패 복구의
Windows 검증 60개 항목이 통과했다. 실제 음성·모델 추론은 이 검증에 포함하지 않았다.
버튼 비활성화 사유, 빈 기록 안내, 키보드 순서·접근성 이름을 추가했다.
최신 앱 빌드와 모의 화면 확인은 통과했다. 실제 키보드 조작 검증은 남아 있다.
구현은 `a71671a`까지 커밋·푸시했다. 다음 작업과 환경 준비는 `docs/HANDOFF.md`를 따른다.
이 요약도 작업 단계가 바뀌면 함께 갱신한다.
