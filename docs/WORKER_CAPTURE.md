# Worker WASAPI PCM 연결 — T02-02a

후속 T02-02b에서 QPC/sample 시작점·재시작 gap과 별도 opt-in live 추론을 연결했다. 현재 추가 계약은 [WORKER_LIVE_ASR.md](WORKER_LIVE_ASR.md)를 따른다. 아래는 T02-02a 당시의 PCM 진단 범위이며 해당 모드의 live_asr=false는 유지한다.

2026-09-30. `crates/capture-windows/`를 probe와 reusable owner 라이브러리로 분리했다. 실제 loopback→bounded packet pool→처리 스레드의 16 kHz mono 정규화→worker ring까지 연결했다. 이번 범위는 PCM 진단이며 live VAD/ASR·자막 UI·번역은 포함하지 않는다.

## 실행·진단 IPC

```powershell
.\scripts\probe-worker-capture.ps1 -Offline
# endpoint 목록은 기존 probe를 사용한다.
cargo run -p echosub-capture-windows -- --list
```

스크립트는 release worker를 빌드하고 기존 영어 TTS WAV를 기본 장치로 잠깐 반복 재생한다. 소리가 들린다. 종료 시 자신이 시작한 재생을 정리한다. PCM·전사문은 저장하지 않으며 counts/timings만 Git 제외 `benchmarks/results/`에 저장한다.

Windows worker의 `--diagnostic-capture`는 파일 추론/mock 모드와 함께 사용할 수 없다. `system_audio/output_device_selection/capture_pcm=true`, `live_asr=false`, implementation=`wasapi-capture-diagnostic`다. 일반 UI 런처는 여전히 MOCK이다.

- `start_capture`: 선택적 `device_id`를 받는다. 없으면 console render 기본 endpoint를 시작 시점에 선택한다. accepted는 Opening 접수다. 실제 성공/실패는 `capture.state` 또는 `get_state.diagnostic_capture`로 확인한다.
- 상태는 Idle→Opening→Running→Stopping→Stopped이며 오류는 Failed다. 이전 owner의 join이 완료돼야 다시 시작할 수 있다. `awaiting_capture_join`으로 확인한다. 잘못된 endpoint ID는 DEVICE_UNAVAILABLE이며 다른 장치로 넘어가지 않는다. render endpoint만 허용한다.
- `stop_capture`: Stop을 요청하고 즉시 응답한다. 중복 Stop은 epoch를 반복 증가시키지 않는다. capture.state Stopped가 스레드 종료 완료다. 이전 epoch의 대기 frame을 반영하거나 filter tail을 확정하지 않는다.
- `capture.metrics`는 약 1초마다 coalesce한다. 진단에는 native packet/frame 수, 정규화 frame 수, accepted_audio_s, endpoint format, QPC 100ns 관측값과 error가 있다. history는 비어 있고 자막을 만들지 않는다. 제품 session은 아직 Idle이며 진단의 숫자 session/epoch는 별도 계약이다.

## 소유권과 상한

COM·WASAPI client/reader/event의 생성·GetBuffer/ReleaseBuffer·Stop·해제는 한 캡처 스레드가 소유한다. [공식 GetBuffer 계약](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)에 따라 borrowed packet을 전체 복사하고 같은 스레드에서 반환한다. 빈 packet은 읽지 않는다. sample position과 QPC는 첫 frame의 timestamp 관측값이다.

raw pool은 8개×9,600 float32 sample=307,200 bytes다. mono/stereo 16/44.1/48 kHz float32만 허용한다. 슬롯은 stream 시작 전에 할당한다. free 슬롯 예약과 큐 전달은 WASAPI 메모리를 빌린 구간 밖에서 수행한다. borrowed 구간에는 범위/flag 검사·PCM 복사·atomic count만 있다. 정규화의 할당과 FIR 연산은 별도 처리 스레드다.

정규화 frame 큐는 32개(1.024초), frame마다 512개 sample이며 worker ring은 기존 12초다. packet pool/frame 큐 고갈, nonfinite PCM, 첫 packet 이후 discontinuity, position gap/backwards QPC/timestamp error는 오류로 Stop한다. 무한 대기·무한 backlog·가짜 silence를 만들지 않는다. 정상 Stop/오류/EOF/shutdown도 처리 스레드와 캡처 owner를 join한다. output stall의 기존 5초 감지 뒤에도 owner를 정리한다; 캡처 API 종료의 고정 deadline은 보장하지 않는다.

## 장치·시간·다음 단계

장치는 한 실행 동안 고정한다. 기본 장치 변경/장치 비활성은 250ms polling으로 오류 종료하며 자동 전환하지 않는다. IMMNotificationClient와 실제 전환·분리 검증은 후속이다. accepted_audio_s는 전달받아 처리한 PCM 누적 시간이며 elapsed_s와 다르다. QPC를 전체 session의 단조 audio timestamp로 매핑하거나 Stop/재시작 사이의 gap을 시간축에 반영하는 기능은 아직 없다.

[Windows 측정](evidence/T02-02a-windows-worker-capture.md)은 실제 loopback의 반복 Start/Stop, 제어 응답, 잘못된 endpoint·활성 shutdown·부모 EOF를 확인한다. 실제 큐 고갈/장치 전환/10분/게임 부하·Mac은 미검증이다. 다음 **T02-02b**는 live PCM의 QPC/session 시간·gap/epoch와 지속 VAD state를 연결하고 확정 구간을 기존 Whisper owner/history로 전달한다. 파일 경로의 독립 스트림 reset을 live packet마다 적용하지 않는다.
