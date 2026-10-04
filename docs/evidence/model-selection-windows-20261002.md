# P3.1 앱 모델/input profile 연결 — Windows (2026-10-02)

## 변경

`configure_translation.input_profile`과 capability, 적용/준비 profile 상태를 추가했다.
목록 조회와 모델/profile 검사가 완료될 때 owner를 교체한다. 실패 시 기존 Ready
owner/모델/입력을 보존한다. 구성 중 세션·fixture·mock 입력은 거절한다.
앱은 입력 선택·실제 적용 상태·런처 로드 모델을 표시하고 실행 중 변경을 잠근다.
후보 입력과 문맥 분리 옵션은 함께 사용하지 않는다.

`run-live-hymt2.bat`은 설치된 Hy-MT2/b11146, CUDA 빠른 부분 전사를 선택한다.
런처가 모델 해시와 runtime build를 확인하고 자신이 시작한 서버를 정리한다.
기존 Qwen 기본값은 유지한다. 사용법: [모델 선택](../TRANSLATION_MODEL_SELECTION.md).

## 실행 기록

Windows x64 / RTX 3080 10GB. 새 다운로드 없음.

* `cargo fmt --all`
* `cargo build -p echosub-worker -p echosub-translation --locked --offline`: 성공.
* `dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore`: 성공, 경고/오류 0.
  최초에는 telemetry 로그 경로 권한 오류로 실패했고 저장소 지침의
  `AVALONIA_TELEMETRY_OPTOUT=1`, 로컬 `NUGET_PACKAGES` 적용 후 성공했다.
* `dotnet build tests/EchoSub.NativeAsrSmoke/EchoSub.NativeAsrSmoke.csproj --no-restore` 및
  `dotnet build tests/EchoSub.ProtocolSmoke/EchoSub.ProtocolSmoke.csproj --no-restore`:
  모두 성공, 경고/오류 0. 테스트 실행은 하지 않았다.
* `scripts/build-model-probe.ps1 -Backend cuda -Package echosub-worker -Vad -Offline`:
  성공, CUDA architecture 86. CPU의 같은 명령도 성공.
* PowerShell parser의 `scripts/run-live.ps1` 구문 오류 0.
* 기존 Python runtime으로 `scripts/compare-fixed-translation.py --trace
  benchmarks/results/streaming-translation-20261001-192918-a74d88/source-trace.json --rounds 1`:
  성공. `fixed-translation-20261002-193645-679c56`의 Qwen/Hy-MT2 각각 10/10 완료.
  진단 전용 profile CLI 대신 생산 configure IPC를 사용했고, 각 실행 전 실제 적용
  profile과 모델 ID가 요청값과 일치하는지 확인했다.

## 해석과 미검증

이번 비교는 설정 연결 확인이다. 영어 3건은 이전 CPU Whisper 전사, 일본어 2건은
작성한 가설이다. ASR admission은 MOCK이며 실제 HTTP·worker·history를 사용했다.
모델별로 `qwen-greedy`/`hymt2-greedy`를 적용했다. Qwen 런처 기본인 `standard`의
새 비교나 새로운 음성 인식 측정은 아니다. 품질 채택과 실제 화면 지연을 입증하지 않는다.

실패 복원/오래된 worker 호환 거절/후보 조합 거절의 실제 분기, 배치 런처 전체 실행과
UI 조작·화면, 자연 음성·게임 경쟁·30분 세션·macOS는 미검증이다.
다음 P3.2에서 설정/세션 재시작과 실제 사용 확인 범위를 보완한다.
