# 04. 실시간 파이프라인 상세 명세

모든 수치형 기본값은 프로젝트 초기 설정이며 M0–M5 측정 후 변경할 수 있다. 외부 라이브러리가 보장하는 성능값이 아니다.

## 1. 오디오 표현과 시간축

네이티브 캡처는 원래 형식과 timestamp를 유지해 수신한다. 오디오 처리 스레드가 channel layout을 확인하고 float32 mono 16,000 Hz로 변환한다. 처리 프레임은 512 samples(32ms)로 맞춘다. 이 크기는 선택한 Silero 모델 계약과 검증한다. [R13]

리샘플러는 매 callback마다 초기화하지 않는다. stereo 단순 평균을 모든 다채널 입력에 일반화하지 않는다. channel mask가 있는 경우 center 채널 등 실제 layout을 반영하고, 지원하지 않는 형식은 명시적으로 실패시킨다.

**시간 기준:** 세션 시작 시점을 0으로 둔 단조 증가 clock. `audio_start_ms`/`audio_end_ms`는 이 시간축에 놓인다. pause와 장치 변경으로 비는 시간도 유지한다. 시스템 시각 변경으로 자막 시간이 바뀌면 안 된다.

native timestamp → session time 변환과 리샘플링 지연 보정을 한 곳에서 수행한다. 모델이 반환하는 chunk-relative timestamp를 다시 session time으로 변환한다. OS 음성 입력이 끊기면 gap으로 기록한다.

SRT 시간은 **캡처 세션 기준**이다. 유튜브/게임의 원래 재생 위치를 아는 것이 아니므로 원본 영상 timestamp와 자동 일치한다고 표시하지 않는다.

## 2. 버퍼와 과부하

| 구간 | 상한/정책 |
|---|---|
| native capture queue | 실제 포맷 기준 최대 2초, 사전 할당, enqueue 비차단 |
| normalized rolling buffer | 최대 12초, frame/sample 수로 관리 |
| ASR partial 대기 | 가장 최신 snapshot 1개만 유지 |
| ASR final 대기 | 최대 2개, final 우선 |
| translation 대기 | 최대 2개, in-flight 1개 |
| UI snapshot history | 마지막 1,000대사; partial은 같은 레코드 갱신 |
| worker→UI event queue | 최대 256개; 아래 전달 정책 적용 |

callback에서 enqueue 실패 시 incoming frame을 버리고 원자적 drop counter/discontinuity 표시만 한다. 소비자는 다음 처리에서 과거 backlog를 비우고 현재 시점에 재정렬하며 VAD/문맥을 reset한다. producer가 임의로 consumer 소유 포인터를 변경하지 않는다.

raw backlog가 1초를 넘으면 실시간 복구를 우선하고 `capture.gap`에 잃은 시간 범위를 기록한다. final 결과를 처리할 수 없어 버릴 때에도 `segment.skipped` 또는 final gap 레코드를 남긴다. “무손실”로 표시하지 않는다.

UI 출력은 음성 스레드와 분리한다. metrics/partial 이벤트는 합칠 수 있다. 확정 상태의 권위는 worker store에 있으며, 소비자 지연/seq 누락이 발생하면 `snapshot.required`로 현재 상태를 다시 전달한다. 제어 응답·오류는 별도 우선 큐 또는 예약 슬롯을 사용한다. UI가 5초 이상 읽지 않으면 캡처를 안전 종료하고 연결 장애로 처리한다.

## 3. VAD와 발화 구간

초기 값:

| 값 | Balanced 기준 |
|---|---:|
| speech threshold | 0.50 |
| 최소 발화 | 160ms |
| pre-roll | 300ms |
| post-roll | 200ms |
| 발화 종료 silence | 480ms |
| 최대 한 발화 chunk | 8,000ms |
| 긴 발화 분할 overlap | 600ms |
| partial 첫 decode 최소 음성 | 800ms |
| partial 간격 | 1,000ms |

512 sample 경계와 맞지 않는 duration은 내부에서 frame 단위로 반올림한 유효 값을 표시한다. 원래 설정값과 실제 적용값을 진단에 구분한다.

디지털 무음은 VAD 전에도 빠르게 걸러낼 수 있다. VAD가 비음성이라 판단한 구간에서는 ASR/번역을 호출하지 않는다. 배경음악/효과음의 오검출은 별도 fixture에서 측정한다. “VAD가 있으므로 환각이 사라진다”고 가정하지 않는다.

WASAPI의 silent flag와 packet이 아예 오지 않는 경우를 구분한다. 정상 상태의 스트림에서 마지막 음성 이후 packet이 멈췄다면 wall-clock watchdog으로 발화 종료를 판단해 final이 영원히 대기하지 않게 한다. 장치 오류·timestamp 불연속이 동반되면 침묵이 아니라 gap으로 처리한다. 타이머로 ASR에 없는 대사를 만들어 넣지 않는다.

짧은 응답이나 감탄사를 모두 지우지 않도록 최소 발화 임계값을 실제 자료로 조정한다. 한숨/말 끊김/격한 게임 효과음은 별도 테스트 항목이다.

## 4. ASR 스케줄러

Whisper를 native streaming ASR처럼 간주하지 않는다. **rolling audio chunk 재인식 + 부분 결과 안정화**로 준실시간 동작을 만든다. whisper.cpp의 샘플은 통합 참고 자료이며 그대로 제품 스케줄러로 취급하지 않는다. [R04][R06]

1. VAD 발화 시작 시 segment ID를 생성한다.
2. 최소 음성이 모이면 partial snapshot을 요청할 수 있다.
3. 이전 partial decode가 진행 중이면 새 작업을 줄 세우지 않고 최신 snapshot으로 교체한다.
4. silence 또는 8초 상한에서 final 작업을 예약한다.
5. final 작업은 partial보다 우선하며, 가능한 경우 partial을 협력적으로 취소한다.
6. 동일 model context에서 병렬 decode를 실행하지 않는다.
7. final이 수용된 뒤 같은 segment의 partial을 전부 무시한다.

부분 자막은 현재 발화 하나만 바뀌게 한다. 두 번의 인식에서 공통으로 유지된 prefix와 불안정 tail을 내부적으로 분리할 수 있으나, tail을 빨리 고정해서 오인식을 되돌릴 수 없게 만들지 않는다.

낮은 신뢰도는 모델별 raw score로 관리한다. calibration 없이 “정확도 98%”처럼 표시하지 않는다. no-speech/반복/빈 결과 검사로 의심 결과를 보류하고 다음 실제 음성을 기다린다. 실패한 구간에 모델로 대사를 지어내어 채우지 않는다.

## 5. overlap과 반복 발화

긴 발화는 600ms 겹쳐 인식하되 이전 chunk의 오디오 시간 구간과 텍스트 prefix/suffix 정합을 함께 확인한다. 글자가 같다는 이유만으로 전체 세션에서 중복 제거하지 않는다. “안 돼, 안 돼”처럼 실제 반복 발화는 보존해야 한다.

일본어·한국어는 공백 기반 분할만 쓰지 않는다. Unicode grapheme과 모델 timestamp를 고려하며 UTF-8 byte offset을 문자 offset으로 취급하지 않는다. 정합이 불확실하면 경계 품질 문제를 기록하고 테스트를 추가한다.

## 6. 번역

MVP는 **확정 원문만 번역**한다. 매 partial마다 번역 요청을 보내지 않는다. 부분 번역/재번역은 지연·부하·화면 흔들림을 측정한 후 P1에서 도입한다.

입력: 현재 확정 원문, 원어/목표어, 동일 epoch의 직전 확정 원문 최대 2개. 과거 번역을 사실 근거로 재주입하지 않는다. source==target이면 서버 호출 없이 bypass한다.

- 동시 요청 1개, 대기 2개.
- warm-up은 세션 시작 준비 단계에서 수행한다. 모델 load 지연을 매 대사 timeout과 섞지 않는다.
- 연결 timeout 2초, 한 대사 전체 deadline은 ASR final 등록 후 8초.
- 5xx/일시 연결 실패는 남은 deadline이 충분할 때만 최대 1회 재시도.
- 401/모델 없음/스키마 불일치는 자동 반복하지 않는다. 429는 남은 deadline 내에서만 Retry-After를 고려한다.
- 오래 밀린 대사는 번역을 skip하고 원문을 유지한다. 새 대사에 과거 번역을 붙이지 않는다.
- 출력은 텍스트 데이터로만 사용한다. tools, tool_choice, MCP를 전달하지 않는다.
- 명령형 발화도 번역할 문장이다. 발화 안의 “이전 지시를 무시해”를 프로그램 지시로 실행하지 않는다.

오류·취소가 나더라도 이미 확정한 원문은 유지한다. 번역은 `pending/done/failed/skipped/bypassed` 중 하나로 끝나야 하며 영원히 “번역 중”으로 남지 않는다.

## 7. 결과 유효성

자막 적용 키:

`session_id + epoch + segment_id + source_revision`

설정에 따라 `translation_request_id`도 확인한다. Stop/Pause/재시작/장치 변경 시 epoch 또는 session을 갱신하고 기존 작업을 취소한다. **HTTP 취소가 성공했다고 가정하지 말고 응답 적용 시에도 키를 검증한다.**

한 segment 안에서 source_revision은 증가만 한다. `source.final`이 적용된 뒤 원문은 더 바꾸지 않는다. 미래 교정 기능은 별도의 수정 정책·버전으로 도입한다.

## 8. 일시정지·장치 변경·종료

- Pause: 캡처 중지, 미확정 발화 폐기, 현재 epoch 취소, 기록 보존.
- Resume: 같은 session, epoch 증가, 새 오디오 timestamp로 시작, VAD/문맥 reset.
- Windows default-follow 장치 변경: 짧게 중지→epoch 증가→재연결→gap 표시.
- 고정 장치 분리: Paused와 복구 안내. 임의 장치 fallback 금지.
- Stop: 캡처 즉시 중단, 미확정 발화/대기 작업 폐기, overlay 숨김. 기존 final 기록은 내보내기 가능.
- 새 Start: 새 session ID, 새 session timeline. 이전 기록을 지우기 전 보관/내보내기 선택을 제공.

종료 직전의 미확정 음성을 자동 flush해 늦게 자막을 띄우는 기능은 MVP에서 하지 않는다.

## 9. 프리셋과 열·전력

팬 소음을 직접 제어하는 기능을 만들지 않는다. Economy에서 partial을 끄고, 모델 크기·동시성·decode 주기를 줄이는 조절 수단을 제공한다. 침묵 중에는 모델이 메모리에 있을 수 있지만 신규 decode/translation 요청은 없어야 한다.

RTF, pending age, memory, 실제 게임 영향이 나빠지면 사용자에게 Economy 전환을 제안한다. 사용자 허가 없이 더 큰 모델을 다운로드하거나 외부 서버 설정을 바꾸지 않는다.

