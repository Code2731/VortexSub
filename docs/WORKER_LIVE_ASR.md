# Windows 연속 캡처→VAD→Whisper — T02-02b

2026-10-01. 실제 WASAPI PCM을 지속 Silero state/segmenter와 기존 Whisper owner/history에 연결했다. 진단 worker 경로이며 제품 session/UI·번역·partial은 포함하지 않는다. 일반 `scripts/run.ps1`은 MOCK이다.

## 실행과 IPC

```powershell
.\scripts\probe-worker-live-asr.ps1 -Offline
```

기존 동의받은 Whisper base·Silero v6.0·ORT 1.22.0 CPU 자산과 명시적 SHA를 사용한다. 새 다운로드는 없다. 스크립트는 기존 영어 TTS 앞에 실제 무음 0.5초, 뒤에 1초를 붙인다. 두 final 검증에서는 캡처 Running 뒤 음원의 첫 부분부터 반복 재생하므로 소리가 들린다. 다른 시스템 오디오는 격리하지 않는다. 종료 시 자체 재생을 중지한다. 생성 WAV와 counts/timings만 Git 제외 `benchmarks/results/`에 남기며 전사문은 report에 저장하지 않는다. 실패 보고서는 이전 검증 checkpoint와 마지막 캡처 상태를 보존한다.

worker는 `--diagnostic-capture --live-asr --diagnostic-asr --diagnostic-vad`와 기존 모델/DLL 절대 경로·해시 옵션을 요구한다. implementation=`wasapi-live-asr-diagnostic`, live_asr/asr/vad/capture_pcm=true, fixture_asr/translation=false다. 파일 입력과 live 입력을 함께 허용하지 않는다. Whisper Ready 이후 `start_capture`에 명시적 `language: en|ja|ko`를 전달한다. 언어 옵션은 실제 품질 채택을 뜻하지 않는다.

`capture.segmented`는 worker segment_id, 내부 vad_segment_id, continued_from, queued, reason, audio_start_s/audio_end_s를 전달한다. 확정 구간만 final ASR에 넣으며 결과는 source.final/segment.failed/get_history로 전달한다. overlap 텍스트 중복 병합은 아직 없다. 모든 새 기간은 초다. 제품 session은 Idle이며 숫자 session=1/epoch는 진단 namespace다.

## 스레드와 유한 저장소

캡처 owner가 WASAPI packet을 복사·반환하고, 정규화 owner가 512-sample PCM을 만든다. IPC owner는 ring append·큐 전달·snapshot admission을 수행한다. 별도 live VAD owner가 ONNX session·recurrent state·VadSegmenter를 소유하며 Whisper owner는 context를 계속 재사용한다. VAD 모델은 실행마다 로드하고 해당 실행의 모든 packet에서 재사용한다. per-packet reset은 하지 않으며 exact-zero/ModelReset에서 recurrence를 초기화한다.

raw pool 8개·정규화 큐 32개에 이어 VAD 입력 32 frames(1.024초), 결과 32 batches 상한이다. 결과에는 final/discard 이벤트만 있고 PCM 복사본은 없다. ring 12초, immutable snapshot 4슬롯×8초, ASR final 대기 2개 상한을 유지한다. VAD 큐 초과/오류는 stream Failed와 새 epoch로 끝낸다. ASR admission 초과는 이유 있는 skipped history다. 모델 로딩/추론의 고정 deadline은 보장하지 않는다.

## 시간축·Stop·오류

Start의 monotonic worker sample origin과 QPC를 짝짓고 첫 packet의 QPC 차이를 16 kHz sample offset으로 변환한다. [GetBuffer QPC는 100ns 단위](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)이며 [QueryPerformanceCounter](https://learn.microsoft.com/en-us/windows/win32/api/profileapi/nf-profileapi-queryperformancecounter)를 frequency로 변환해 비교한다. 첫 frame으로 빈 epoch ring을 한 번 고정하고 이후 native position의 연속성과 rational resampling sample count를 따른다. delivery/look-ahead 시간을 audio range에 더하지 않는다.

audio_start_s/audio_end_s는 worker 시작 이후 진단 시간축이다. gap_before_s는 이전 수신 끝과 다음 첫 frame의 차이다. 빈 시간에 silence를 넣지 않고 새 epoch로 끊는다. QPC drift 보정/정밀 latency 정합·제품 session 시계는 후속이다. native gap/glitch/장치 변경은 기존 fail-stop 정책이며 자동 복구/전환하지 않는다. healthy packet-stop watchdog은 실제 받은 PCM 범위만 확정한다.

Stop은 즉시 epoch를 증가시키고 미확정 VAD 상태·대기 final을 폐기하며 실행 중 Whisper에 취소를 요청한다. tail을 flush하지 않는다. 캡처 Stopped와 diagnostic_live_vad.awaiting_join=false를 별도로 확인한다. 두 owner가 join된 뒤 새 입력을 시작할 수 있고, 이전 full이 반환 중이어도 실행 예약/PCM은 반환까지 유지된다. 늦은 완료는 applied=false다. shutdown/부모 EOF도 소유 thread들을 정리한다.

## 수용 범위

T02-02c는 [시작 10초 실패 정책·STA·관측 메타데이터](WORKER_CAPTURE.md)를 추가했다. client의 Running 대기는 worker 정책보다 긴 12초다. 무음 시작의 native Initialize 대기 재현 및 whole-fixture 회귀는 [추가 근거](evidence/T02-02c-windows-capture-startup.md)를 따른다. 시간 초과의 제어 처리와 발화 경계/제품 품질 수용은 별개다.

[Windows 근거](evidence/T02-02b-windows-live-asr.md): 영어 합성 음원의 실제 loopback final/history, 추론 중 Stop/재시작, VAD 해시 실패, 자체 재생 중단 후 관측, 활성 shutdown/EOF를 확인했다. 중간 final 하나가 8초 chunk 상한에 도달했고 추가 run은 client Opening timeout으로 끝나 경계/부하 수용은 보류한다. native_phase로 API 대기 위치를 구분하고, 보완 후 최종 PCM/live 회귀는 통과했다. 자연/일본어/한국어 live 경계·음악/게임·UI·Pause·실제 장치 전환/분리·큐 고갈/출력 stall stress·10분 soak·CUDA worker·Mac은 미검증이다. 품질 및 M2 제품 gate는 false다. 다음은 제품 session 계약과 UI 원문 history/오버레이 연결이다.

## T02-04b 경계·빈 결과 처리

T02-04c는 [token 시간 정합](ASR_TOKEN_ALIGNMENT.md)과 PCM/token 진단을 추가했다.
600초 디지털 무음 파일 검증이 통과했으며 실제 live 경계 품질은 미검증이다.

Live continuation을 제품 ID로 전달하고 native timestamp/완전한 span 문자열로 보수적으로 정합한다. 빈 결과/겹침만 남은 결과는 NoSpeech/OverlapOnly 사유로 skip한다. 실제 dedup 효과와 격리된 무음은 아직 수용하지 않았다. [계약](ASR_RECONCILIATION.md) · [실측](evidence/T02-04b-windows-asr-reconciliation.md).
