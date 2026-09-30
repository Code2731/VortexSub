# Worker native ASR 파일 진단 — T02-01a

2026-09-30. `ASR-001~004`, `MOD-001`, `NF-003/004/008`의 worker 소유권·전달 부분을 구현했다. 실제 캡처·Silero VAD·partial·번역·UI 렌더링·macOS 수용은 포함하지 않는다. WAV 전체를 하나의 final 후보로 처리하므로 M2 제품 gate의 통과 근거가 아니다.

## 실행

기존 동의로 확보한 Whisper base와 로컬 TTS WAV를 사용한다. 새 모델을 다운로드하지 않는다. native 빌드에는 기존 CMake/LLVM/MSVC 도구가 필요하며 CUDA는 해당 toolkit이 필요하다.

```powershell
.\scripts\probe-worker-asr.ps1 -Backend cpu -Offline
# CUDA 빌드/실행도 지원하지만 이번 worker 측정은 CPU만 실행했다.
.\scripts\probe-worker-asr.ps1 -Backend cuda -Offline
```

스크립트는 `build-model-probe.ps1 -Package echosub-worker`로 native worker를 만들고 `tests/EchoSub.NativeAsrSmoke/`에서 기존 C# 클라이언트로 실행한다. 기본 workspace 빌드에는 native inference를 포함하지 않는다. `run.ps1`의 UI는 기존 MOCK 모드다. 결과는 Git에서 제외한 `benchmarks/results/worker-asr-*/report.json`에 저장하며 전사문을 보고서/콘솔에 기록하지 않는다.

## 진단 IPC

native worker 실행 인자는 `--diagnostic-asr --asr-model <절대 경로> --asr-sha256 <64자리 해시> --asr-backend cpu|cuda --asr-threads 8`이다. mock과 동시 활성화할 수 없다. `hello`의 implementation은 `native-asr-fixture`, `asr/fixture_asr`는 true, `vad/system_audio/translation`은 false다. 모델 준비는 비동기이며 실제 준비 여부는 `get_state.model.state`로 확인한다.

- `transcribe_fixture`: `path` 절대 경로, `sha256`, `language`(`en/ja/ko`)를 받는다. 모델 Ready 이후 accepted 응답으로 숫자 session/epoch/segment ID를 반환한다. accepted는 전사 성공을 뜻하지 않는다.
- 입력은 1 MiB 이하, 16 kHz mono PCM16/float32, 유한 [-1,1] 값, 0초 초과~8초 이하 WAV다. SHA/형식 오류는 `fixture.failed`로 전달한다. exact-zero PCM은 `fixture.suppressed`이며 추론하지 않는다. 비영(非零) 잡음의 발화 판정은 아직 하지 않는다.
- `asr.started`, `asr.completed`는 작업 ID를 전달한다. 완료에는 `decode_s`, `applied`, `abort_observed`가 있다. 확정 원문은 `source.final.payload.record`와 `get_history`로 전달한다. 번역 상태는 `None`이며 가짜 번역을 만들지 않는다.
- `reset_fixture_epoch`는 epoch 증가·대기 작업 폐기·진행 중 작업 취소를 요청하고 즉시 응답한다. `awaiting_native_return=true`면 native full 반환을 아직 기다린다. 이전 history를 보존하며 늦은 이전 epoch 결과는 적용하지 않는다.
- `get_state.diagnostic_asr`는 pending_inputs, decoding(예약), native_running(실제 native 호출), completed_jobs, model_load_s를 제공한다. session은 실제 capture session이 아니므로 계속 Idle이다.

## 스레드·메모리·종료

IPC owner는 모델/파일 I/O와 추론을 실행하지 않는다. loader 입력/결과 채널은 각각 1칸, 미완료 입력 ID는 최대 2개다. PCM은 기존 12초 ring·4개 8초 snapshot pool을 사용한다. final 대기는 2개, native 실행은 1개다. admission 초과는 기존 skipped 기록으로 남긴다.

모델 해시 검증·context 생성·decode·context 해제는 한 native 스레드가 소유한다. epoch가 바뀌어도 context와 snapshot pool을 재사용한다. 취소 token만 요청하고 full 반환 전 snapshot이나 예약을 해제하지 않는다. 새 epoch 작업을 받아도 이전 full 반환 이후에 실행한다. 취소 callback 관측과 제어 응답 시간은 서로 다른 값이다.

shutdown/EOF/프로토콜 오류는 취소 후 loader/native를 join한다. stdout stall 감지는 5초지만 native 반환/join 시간까지 5초라는 보장은 없다. C# 소유 프로세스 종료는 기존 5초 종료 기한 이후 프로세스 kill을 사용할 수 있다. native thread/context를 강제로 해제하지 않는다. 예기치 않은 native 채널 종료는 worker 오류로 처리한다.

## 검증과 다음 단계

[Windows 측정](evidence/T02-01a-windows-worker-asr.md)은 합성 en/ko 각 10개, digital silence, 파일/모델 해시 오류, 실제 native running 관측 뒤 10회 epoch 취소·재시작, 추론 중 정상 shutdown을 확인한다. 취소 반환 전에 새 epoch WAV를 제출하고 같은 context의 재전사가 기준 원문과 일치하는지 확인한다. ping을 모델 로딩·추론 중 반복한다.

T02-01b에서 동의받은 고정 Silero 모델·ONNX runtime을 별도 opt-in [VAD 파일 경로](WORKER_VAD.md)에 연결했다. 위 T02-01a 단독 경로는 여전히 VAD=false다. 다음 T02-02에서 WASAPI callback 큐·device/clock·capture 수명을 통합한다. 파일 진단의 빠른 제어 응답을 실제 게임 자막 지연으로 해석하지 않는다.
