# T01-01b: 확률 기반 발화 구간과 packet-stop watchdog

## 구현 범위

`crates/audio-core/src/vad.rs`는 모델 확률과 실제 16 kHz PCM 범위를 받아
발화 시작, partial 요청, final 요청, 폐기와 모델 reset 요청을 생성한다.
처리 스레드에서 사용하며 캡처 callback에서는 호출하지 않는다.
**Silero ONNX 모델/runtime·recurrent state/context 어댑터는 아직 없다.**
fixture의 확률은 mock이다. 이벤트는 작업 의도이며 실제 ASR/번역 실행이나
worker/UI 연결을 의미하지 않는다. M0/M1 전체 gate는 미통과다.

## 설정과 시간

샘플 위치가 권위이며 범위는 [start,end)다. 시간 설정은 512 samples,
즉 0.032초 단위의 가장 가까운 프레임으로 반올림한다. 요청값과 적용값을
각각 보존한다. 표시 단위는 초다.

| 설정 | 요청값(초) | 적용값(초) |
|---|---:|---:|
| 최소 음성 | 0.160 | 0.160 |
| pre-roll | 0.300 | 0.288 |
| post-roll | 0.200 | 0.192 |
| 종료 silence | 0.480 | 0.480 |
| 최대 PCM chunk | 8.000 | 8.000 |
| 분할 overlap | 0.600 | 0.608 |
| partial 최초 최소 새 음성 | 0.800 | 0.800 |
| partial 요청 간격 | 1.000 | 0.992 |

speech threshold는 0.5다. 유한 확률 [0,1], 연속 range, 현재 session/epoch,
유한 PCM [-1,1], 단조 관측 clock을 상태 변경 전에 검증한다.
최대 chunk에는 pre/post-roll과 overlap을 포함한다. 최소 음성은 voiced
sample 합이며 음성 시작부터 끝까지의 span 길이와 구분한다. partial 최소값은
최소 발화보다 작을 수 없다. 다른 설정 조합도 8초 snapshot 예산을 검증한다.

## 상태와 소유권

- 첫 voiced frame에서 segment ID를 만들고 보유 PCM 범위 안의 pre-roll을
  포함한다. 임계값보다 짧은 발화는 `TooShort`로 폐기한다.
- partial은 새 voiced sample이 충분하고 요청 간격이 지난 경우 생성한다.
  실제 최신 partial 교체·final 우선순위·취소는 T01-02 작업이다.
- 종료 silence가 모이면 마지막 voiced sample 이후 실제 post-roll까지만
  final에 포함한다. 8초 한계에서는 다음 voiced frame이 이전 ID를 참조하는
  새 segment를 만들며 0.608초를 겹친다. overlap만으로 새 final을 만들지 않는다.
  새 음성이 있는 연결 suffix는 이전 overlap으로 최소 발화를 충족할 수 있다.
  텍스트 중복 제거는 ASR 결과 적용 단계에 남는다.
- `requires_probability`로 exact digital zero의 모델 호출을 생략한다.
  zero는 양성 확률이 주어져도 음성으로 처리하지 않는다. nonzero PCM에는
  실제 모델 확률이 필요하다. reset 이벤트는 caller가 모델 context/state에
  적용해야 한다. 새 classifier의 초기화도 caller 책임이다.
- 마지막 tail의 추론 입력을 padding해도 실제 sample만 길이와 snapshot에
  포함한다. 정상 EOF는 final 가능하지만 Pause/Stop/캡처 오류는 미확정
  구간을 폐기하고 닫는다. 복구는 증가한 epoch와 명시적 gap으로 수행한다.
- history는 최대 250개 frame 판단만 저장하며 PCM을 복사하거나 소유하지 않는다.
  caller가 frame을 rolling에 넣고 이벤트 범위의 snapshot을 즉시 확보한다.
  동일 pool을 재사용하고 고갈을 명시적으로 처리한다. source revision,
  stale 결과 적용과 queue 상한은 아직 구현하지 않았다.

## 패킷 중단

caller는 수신된 PCM을 먼저 처리한 뒤 세션 단조 관측 시계로 `poll`한다.
건강한 스트림에서는 이미 수신한 quiet 길이와 마지막 packet 이후 시간을
합쳐 0.480초에 도달하면 `PacketStopped` final을 요청한다.
타이머는 PCM cursor를 전진시키지 않으며 없는 post-roll을 생성하지 않는다.
반복 poll은 final을 중복 생성하지 않는다. 장치 오류는 먼저 폐기한다.
timestamp 불연속·장치 변경·과부하는 silence 대신 epoch/gap reset으로 처리한다.
native clock 매핑, health 관측과 backlog 폐기는 향후 캡처 어댑터 책임이다.

## 검증 결과와 다음 작업

2026-09-30 Windows x64, Rust 1.90.0; 가짜 시계/생성 PCM/mock 확률을 사용했다.
실시간 대기나 모델 다운로드 없이 다음 명령을 실행했다.

```powershell
cargo test -p echosub-audio-core --offline
$env:ECHOSUB_OFFLINE='1'
$env:ECHOSUB_NUGET_SOURCE="$env:USERPROFILE/.nuget/packages"
.\scripts\check.ps1
```

VAD fixture 15개 PASS: 600초 digital silence에서 mock 모델/ASR 요청 0,
nonzero 비음성 억제, 최소 발화·pre/post-roll·partial 간격, 8초/overlap과
짧은 연결 suffix, watchdog의 실제 PCM 경계·중복 방지, 오류/Pause/Stop 폐기,
epoch reset/stale frame, 입력 오류의 상태 보존, 실제 tail 길이와
normalizer→ring→VAD→immutable snapshot 연결을 확인했다.
전체 Rust 시험 39개, fmt/workspace·C# 빌드와 IPC smoke PASS.

UT-003/005의 결정론적 segmentation subset만 검증했다. 실제 Silero state/context,
음악/효과음 오검출, 자연 발화 경계, native 장치 중단과 Mac은 NOT_RUN이다.
다음은 T01-02의 segment/revision/epoch 상태기계, 유한 큐·취소·final 실패,
history snapshot 일관성·번역 terminal 상태 계약이다.
