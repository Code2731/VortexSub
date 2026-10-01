# Windows 부분 전사 계약

## 켜는 방법

`run.cmd -Live -Offline`에서 모델 Ready 뒤 **부분 전사 켜기**를 선택하고
세션을 시작한다. 기본값은 꺼짐이며 설정은 다음 세션 시작 시 적용한다.
IPC는 `start_session`의 `config.partial_enabled: true`로 켠다.
`source_partial` capability는 지원 여부이며 실제 활성화 여부는
`diagnostic_asr.partial_enabled`다. 파일 전사 진단은 계속 확정 전사만 사용한다.

## 요청과 결과

기존 VAD가 유성 음성 0.8초 이상에서 첫 부분 요청을 만들고 이후 약 1초마다
누적 PCM을 재인식한다. 512 sample 프레임에 맞춰 간격이 반올림된다.
부분 결과와 확정 결과는 같은 session/epoch/segment ID를 쓰고
`source_revision`을 증가시킨다. 적용된 결과는 `source.partial` 이벤트와
버전 history로 전달한다. 실패한 재인식은 마지막 적용 원문을 보존하며
성공한 새 부분 결과로 통지하지 않는다.

확정 FIFO 2개, 최신 대기 부분 요청 1개, native 실행 1개, PCM pool 4개를
유지한다. 확정 요청은 진행 중인 부분 추론에 취소를 요청한다. native 반환
전에는 실행 예약을 해제하지 않는다. 교체된 요청의 언어 메타데이터도 제거해
대기 언어 항목을 최대 3개로 제한한다. 번역은 기본 확정 전용이며
[임시 번역 옵션](STREAMING_TRANSLATION.md)을 별도로 켤 수 있다.
후속 [token 시간 정합](ASR_TOKEN_ALIGNMENT.md)은 partial을 이전 확정 context로 저장하지 않는다.

실행 중 ASR·대기 확정 ASR·반환 전 임시 HTTP가 있으면 새 부분 요청은
revision을 바꾸지 않고 최신 메타데이터 하나로 대기한다. 완료 결과를 적용한
뒤 대기 요청의 PCM을 snapshot으로 복사한다. 임시 HTTP가 끝나야 다음
부분 요청을 admission하므로 새 요청이 표시 전 번역을 계속 취소하지 않는다.
확정 요청은 이 대기를 우회하고 대기 부분 요청을 지운다. 대기 오디오가
rolling buffer에서 덮어써졌으면 폐기한다. Pause/Stop/epoch 변경도 대기를 지운다.

`get_state.diagnostic_asr.partial_scheduler`는 worker 실행 동안 누적한
`requested/deferred/replaced/dropped`, 모든 native 완료의 `asr_applied/asr_ignored`,
`asr_decode_total_s`와 마지막 최신 대기 요청의 `last_deferred_wait_s`를 제공한다.
`pending`은 메타데이터 대기 여부다. 적용 완료 수에는 NoSpeech도 포함되므로
화면에 표시한 부분 자막 개수와 같지 않다. 모든 시간 단위는 초다.

Pause/Stop/epoch 변경은 진행 중 구간을 폐기하며 늦은 결과를 거부한다.
VAD의 로컬 구간 번호가 재시작해도 제품 구간 번호는 세션에서 증가한다.

## 화면과 저장

오버레이에 `[인식 중]`, 마지막 부분 원문을 유지하는 확정 대기에는
`[확정 처리 중]`을 표시한다. [두 카드 읽기 정책](CAPTION_READING.md)으로
이전 자막을 유지하고 표시할 새 내용에 4~10초를 제공한다.
history 원문에는 표시용 접두어를 저장하지 않는다.
TXT는 상태가 포함된 기록을, SRT는 유효한 확정 원문만 저장한다.

## 검증과 한계

`scripts/check.ps1`은 기본 꺼짐·동일 구간 revision·Pause/Resume와
확정 우선 취소/실행 예약·VAD 부분 요청을 검사한다.
`scripts/probe-partial-scheduling.ps1 -WavPath <1~8초 WAV>`는 실제 CPU
Whisper owner에 PCM을 실제 속도로 공급해 기존/new admission을 비교한다.
기본 1초와 스트레스 0.25초를 분리한다. VAD·HTTP·화면을 포함한 지연은 아니다.
[측정 결과](evidence/partial-scheduling-windows-20261001.md)를 참고한다.
`scripts/probe-worker-live-asr.ps1 -NoBuild -Offline -Partials`는 기존 영어
TTS `en-10.wav`를 loopback으로 재생해 실제 부분→확정 및 세션 제어를 검사한다.
전체 원문/보고서/WAV는 Git에 넣지 않는다. 실제 화면 조작, 자연 음성/게임 품질,
실제 overlap 중복/누락 품질, macOS는 별도 수용 대상이다. 시간/span 기반 정합은 [T02-04b 계약](ASR_RECONCILIATION.md)을 따른다. 간헐적 시작 timeout의
근본 원인 분석은 사용자 요청에 따라 보류했다.
