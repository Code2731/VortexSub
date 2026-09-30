# 실제 Silero VAD 파일 경로 — T02-01b

2026-09-30. `crates/vad-silero/`가 실제 Silero probability·recurrent state를 소유하고 기존 `VadSegmenter`에 연결한다. opt-in worker에서는 VAD가 확정한 PCM 범위만 Whisper final 작업으로 보낸다. 실제 캡처·partial·화면 자막·번역·Mac 수용은 후속이다.

## 모델과 실행

사용자가 Silero v6.0 ONNX 2.33 MB와 ONNX Runtime 1.22.0 Windows x64 CPU ZIP 72.37 MB 다운로드에 동의했다. 파일은 Git 제외 `models/`에 있다. 고정 revision·크기·SHA-256·출처·MIT 라이선스는 [카탈로그](../benchmarks/vad-assets.json)에 기록했다. 모델은 GitHub blob SHA-1도 공식 metadata와 일치했다. runtime SHA-256은 공식 다운로드에서 측정한 값이며 upstream release digest는 없었다. 제3자 notices는 runtime 압축 내 파일을 유지한다.

```powershell
# 기존 파일 검증. 누락된 파일은 동의 없이 다운로드하지 않는다.
.\scripts\download-vad-assets.ps1
# 다운로드 동의를 이미 받은 경우에만 -Consent를 추가한다.
.\scripts\probe-worker-vad.ps1 -Offline
```

Rust binding은 `ort = 2.0.0-rc.10`으로 고정했다. `download-binaries`/모델 자동 fetch를 끄고 `load-dynamic`만 사용한다. default workspace는 모델/DLL 없이 빌드한다. native worker는 `native-vad` feature와 명시적 절대 모델/DLL 경로·SHA가 필요하다. CPU session의 intra/inter threads는 각 1이다. 새 binary/모델 버전은 자동 채택하지 않는다.

## 상태와 범위 계약

공식 [v6 wrapper](https://github.com/snakers4/silero-vad/blob/v6.0/src/silero_vad/utils_vad.py)를 기준으로 16 kHz mono의 512개 샘플 앞에 이전 64개 context를 붙인다. 입력은 float32 `[1,576]`, recurrent state `[2,1,128]`, scalar int64 sample rate다. 출력 이름·shape·type·유한 확률/상태를 검사한다.

loader/VAD 스레드 하나가 ONNX session을 만들고 재사용·해제한다. Whisper owner와 IPC는 분리돼 있다. 각 파일은 독립 스트림으로 recurrence와 sample cursor를 초기화한다. detector 내부 epoch는 단조 fixture ID이고, 실제 결과 적용은 worker session/epoch로 검사한다. 늦은 이전 epoch의 VAD 결과도 폐기한다. segmenter의 ModelReset은 recurrence를 지운다. 정확한 zero PCM은 probability=0으로 처리하고 모델을 호출하지 않는다. 비영 PCM은 실제 모델에 넣는다.

최대 8초 파일을 512-frame으로 처리한다. 끝의 짧은 frame은 모델 입력만 zero-pad하며 원래 sample 범위·시간을 유지한다. 오류 시 recurrence를 초기화하고 PCM cursor를 진행시키지 않는다. partial 이벤트는 ASR로 보내지 않는다. 모든 final 범위는 기존 ring/snapshot·final 2개 대기 상한을 따른다. 여러 발화가 한 파일에 있어도 segment ID를 각각 발급하며 초과는 skipped history다.

## 진단 IPC

기존 `--diagnostic-asr`에 `--diagnostic-vad --vad-model <path> --vad-sha256 <hash> --vad-runtime <dll> --vad-runtime-sha256 <hash>`를 추가한다. `hello.capabilities.vad=true`는 이 모드의 지원 표시다. `model.Ready`는 Whisper 준비만 의미하며 VAD 준비/실행 오류는 `fixture.failed`로 전달한다.

VAD 모드의 `transcribe_fixture` accepted 응답은 `fixture_id`를 반환한다. 이후 `fixture.segmented`는 fixture_id/epoch, 실제 segment ID와 queued 여부·audio_start_s/audio_end_s 목록, vad_calls/vad_s를 반환한다. 무음/비음성이면 목록이 비어 있고 ASR이 없다. accepted fixture ID와 결과 segment ID는 다른 namespace다. source.final/segment.failed/history는 기존 segment identity를 사용한다. 실패/취소 이벤트의 fixture_id도 입력을 식별하며 segment_id는 기존 진단 호환 필드다.

## 검증과 남은 문제

[Windows CPU 근거](evidence/T02-01b-windows-worker-vad.md): 합성 en/ko 20개 모두 원문 final을 하나 이상 전달했다. ko-08은 두 후보 중 짧은 하나가 빈 ASR 출력으로 Failed/InvalidText가 됐다. 이 기록을 보존하며 품질 gate는 false다. 임의로 발화 범위나 확률을 바꾸어 성공 처리하지 않았다.

epoch reset 10회, 두 발화 파일, 무음·톤·저레벨 잡음, 모델/DLL hash 오류를 검증한다. 자연 발화·음악·게임 효과음·일본어 경계, live stream gap/Pause/Stop, 장기 안정성과 Mac은 미검증이다. 다음 T02-02는 WASAPI callback의 bounded PCM 큐·device/clock/epoch 수명과 이 처리 경로를 연결한다. 실제 파일 경로 통과만으로 M2 gate를 통과시키지 않는다.
