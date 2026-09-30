# T01-01a: 공통 오디오 정규화·시간축·PCM 소유권

## 구현 범위

`crates/audio-core/`는 OS·native 모델·네트워크 의존성 없는 Rust 코어다.
T01-01을 정규화/버퍼(a)와 VAD/발화 구간(b)으로 나눴다. 현재는 a만
구현했다. 캡처/worker/UI에 연결되지 않았으며 M0·M1 전체 gate는 미통과다.

입력은 이미 float32로 디코딩된 interleaved PCM이다. 지원 rate는
16,000/44,100/48,000 Hz, layout은 mono 또는 FL/FR stereo다. mono mask는
없음/FL/FC, stereo mask는 없음/FL|FR만 허용한다. mask 없음은 명시된
mono/stereo 형식으로 해석한다. surround·다른 mask/rate는
`UnsupportedFormat`으로 거부한다. integer PCM 디코딩과 다채널 downmix는
후속 플랫폼 어댑터 작업이다.

## 처리 계약

- **처리 스레드 전용:** `push`/`finish`/snapshot은 allocation과 필터 계산을
  수행한다. callback에서 호출하지 않는다. native 사전 할당 큐와 비차단
  producer는 아직 미구현이다.
- packet은 최대 원본 2초, 채널 수에 맞는 nonempty PCM이어야 한다.
  NaN/Infinity와 잘못된 길이는 전체 검증 후 상태 변경 없이 거부한다.
  stereo는 L/R 평균이며 다른 layout에 이 규칙을 적용하지 않는다.
  유한 입력과 필터 출력은 [-1,1]로 clamp한다. `clipped_values`는 혼합
  입력/필터 출력 clipping 횟수이며 epoch 변경 시 초기화한다.
- 16 kHz는 filter 없이 통과한다. 다른 두 rate는 257-tap Blackman-windowed
  sinc 저역통과 필터, cutoff 7.2 kHz, 사전 계산한 rational phase를 사용한다.
  필터는 packet 사이에 유지한다. 이 구현의 제품 음질/CPU 예산은 미판정이다.
- full frame은 **512 samples**다. `finish`는 FIR 경계만 zero-extend하고
  실제 출력 개수를 `floor(native_frames × 16000 / native_rate)`로 유지한다.
  마지막 512 미만 sample은 별도 `AudioTail`로 돌려준다. 가짜 padded frame을
  실제 음성으로 표시하지 않는다. VAD에서 tail을 다루는 정책은 T01-01b다.

## 시간과 불연속

세션 위치의 권위는 16 kHz **sample index**이며 범위는 [start,end)다.
`start_s`/`end_s`로 표시할 때만 초로 변환한다. 패킷마다 반올림하지 않는다.
`session_sample_from_ns`는 어댑터의 단조 세션 기준 ns를 최초 anchor로
변환하며, wall-clock/UTC 입력을 요구하지 않는다.

필터 출력 시간은 입력의 filter center에 맞춘다. future input 대기는
44.1 kHz에서 약 0.002902초, 48 kHz에서 약 0.002667초이며 오디오 시간에
더하지 않는다. 일반 실행의 처리/표시 지연은 이 값과 별도다.

`reanchor`는 같은 session의 더 큰 epoch와 이전 실제 입력 종료 이상인
새 위치를 요구한다. 필터/부분 frame을 reset하고 마지막으로 공개한 audio
end부터 새 anchor까지 `AudioGap`을 반환한다. 이 범위는 아직 전달하지
못한 filter/frame tail과 pause 공백을 포함한다. gap 길이 0인 경우도 장치
변경의 reset marker는 유효하다. 새 session은 새 normalizer/ring을 사용한다.

**남은 어댑터 책임:** packet의 실제 native frame counter/timestamp 불연속을
감지해 reanchor를 호출하고 native clock↔session clock을 매핑한다. 현재
`push`는 한 stream 안의 입력이 연속이라고 가정한다. backwards anchor와
재사용 epoch는 거부하지만, 실제 WASAPI/SCK clock이나 clock drift를 검증한
것은 아니다. epoch 기반 UI/번역 결과 적용은 T01-02에 남는다.

## 버퍼와 snapshot 예산

| 항목 | 상한 / 정책 |
|---|---|
| rolling PCM | 192,000 samples = 12초 = 768,000 bytes, 고정 circular storage |
| snapshot PCM | 슬롯당 최대 128,000 samples = 8초 = 512,000 bytes |
| snapshot pool | 최대 4슬롯, PCM 합 2,048,000 bytes |
| 둘의 PCM 합 | 최대 2,816,000 bytes; 객체/필터/출력 batch 메모리는 별도 |

pool 4슬롯은 ASR in-flight 1, final 대기 2, 최신 partial 1의 향후 계획에
맞춘 상한이다. 현재 작업 큐/우선순위가 구현됐다는 뜻은 아니다. 8초 상한은
pre/post-roll·overlap을 포함한 snapshot 전체 길이다. 초과하면 거부하고
upstream에서 분할해야 한다. 임의로 현재 음성을 잘라내지 않는다.

`PcmSnapshot`은 immutable Arc lease다. ring wrap·epoch reset·pool drop 뒤에도
기존 PCM과 `session_id/epoch/segment_id/source_revision` 키가 유지된다.
lease/clone이 하나라도 남으면 슬롯을 재사용하지 않는다. 모두 사용 중이거나
요청 길이가 상한을 넘으면 `ResourceExhausted`, 덮어쓴 구간은
`RangeUnavailable`, 다른 session/epoch는 `StaleIdentity`다.

pool은 epoch 변경마다 새로 만들지 않고 같은 소유자가 재사용한다. 오래된
lease를 무제한 보관하거나 새 pool을 계속 만드는 구조는 전체 메모리 상한을
보장하지 않는다. caller는 output batch를 ring에 넣고 해제하며 native queue와
job queue에도 상한을 적용해야 한다. final job을 수용하지 못하면 후속
스케줄러가 명시적인 skipped/gap 기록을 남긴다. callback에서 이 pool을
사용하거나 실제 PCM을 조용히 버리는 정책으로 옮기지 않는다.

## 검증 결과

2026-09-30 Windows x64 / Rust 1.90.0, native/GPU 없이 실행했다.

```powershell
cargo test -p echosub-audio-core --offline
$env:ECHOSUB_OFFLINE='1'
$env:ECHOSUB_NUGET_SOURCE="$env:USERPROFILE/.nuget/packages"
.\scripts\check.ps1
```

fixture는 시험 코드에서 생성한 zero/DC/impulse/sine PCM이다. 자연 발화나
Silero 모델 입력/확률을 대체하지 않는다.

| 시험 | 결과 / 확인 범위 |
|---|---|
| UT-001 subset | PASS: 두 rate의 sample 수/길이/연속 range; packet 1/7/113/511/4096 frame과 큰 packet의 결과 동일; 1 kHz gain 오차 0.5% 미만; 8.1/8.5/10/12/15 kHz alias RMS 비율 <0.001(60 dB 억제); DC downmix; impulse center timestamp |
| UT-002 subset | PASS: simulated 장치 변경/epoch에서 session 위치 유지, filter reset, backward 시간/재사용 epoch 거부 |
| UT-003 framing subset | PASS: 512 경계, real short tail, 디지털 무음은 exact zero. 실제 VAD state/context는 미검증 |
| UT-008 PCM subset | PASS: ring wrap, immutable PCM/키, clone이 남은 슬롯 재사용 거부, pool drop 뒤 다른 thread에서 접근, 상한/고갈/stale/덮어쓴 구간 거부 |
| workspace/IPC | PASS: Rust fmt, 기존 9개 + audio core 15개 시험, workspace/C# 빌드, Unicode/64요청/worker 수명 smoke |
| macOS/실제 캡처/모델/VAD | NOT_RUN: 이 fixture 코어 시험의 범위 밖 |

주파수 시험은 위 고정 사인파와 1초 입력의 중앙 구간 RMS 비교다. 전체
spectrum/제품 전사 품질/필터 성능 수용을 통과했다고 일반화하지 않는다.

## 다음 단위

T01-01b에서 확률 입력을 받는 VAD 상태와 pre/post-roll·최소 발화·8초 분할·
overlap·packet-stop watchdog을 먼저 결정론적으로 구현한다. 실제 Silero
모델/runtime/hash/license는 별도로 고정하고 연결 전에는 mock을 명시한다.
T01-02는 segment/revision/epoch 적용과 작업 큐, 취소·final 실패 상태를 담당한다.
M2에서 실제 native 형식/clock·캡처 수명과 이 코어를 연결한다.

