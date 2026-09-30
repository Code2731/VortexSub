# ASR 경계·빈 결과 처리

## 겹침 정합

Windows live VAD의 `continued_from`을 세션/epoch/제품 segment ID에 대응시켜
native ASR owner로 전달한다. 대기 metadata는 ASR 큐와 함께 교체/회수하며
최대 3개다. owner는 직전 확정 decode의 원문 span만 보존한다. partial은
직전 확정 context를 바꾸지 않는다. epoch/세션이 다르거나 이전 chunk가
처리되지 않았다면 정합을 생략한다. 파일 전사는 continuation을 제공하지 않는다.

제거에는 아래 조건이 모두 필요하다.

1. VAD가 명시한 이전 구간과 owner의 직전 확정 구간 ID가 일치한다.
2. 이전/현재 PCM이 시간상 겹친다. 기본 0.6초는 512 sample 단위 반올림으로
   **0.608초(9,728 samples)**이며 이 범위를 넘는 겹침은 제거하지 않는다.
3. Whisper 상대 timestamp를 실제 session sample로 변환할 수 있고, PCM 범위
   안에서 모든 span이 순서대로 배치된다.
4. 현재의 완전한 prefix span과 이전의 완전한 suffix span이 겹침 시간 안에
   들어오고, 결합한 문자열이 정확히 일치한다(바깥 공백만 제거).

완전한 native span만 제거한다. 문자/byte 위치로 단어를 잘라 내거나 세션 전체의
같은 문장을 삭제하지 않는다. 경계를 가로지른 span, 잘못된 timestamp,
띄어쓰기/문장부호 차이, predecessor 누락은 원문을 보존한다. 한/일 원문과
emoji도 UTF-8 부분 byte로 자르지 않는다. 실제 반복 대사는 보존한다.
`asr.completed.overlap_segments_removed`는 제거한 native span 수다.

## NoSpeech와 OverlapOnly

- 정확히 0인 PCM은 Whisper 호출 전 제외한다. VAD 앞단의 디지털 무음 억제도 유지한다.
- 완료된 native 결과가 비었거나 공백뿐이면 `NoSpeech`다.
- 유효 원문이 겹침 span 제거 뒤 비면 `OverlapOnly`다.
- 확정 결과는 `Skipped`와 사유를 남기고 번역을 예약하지 않는다. SRT 대상에서도 제외한다.
- partial은 마지막 적용 원문/revision을 보존한다. 새 성공 원문으로 통지하거나
  UI 만료 시간을 연장하지 않는다. 뒤의 유효 결과는 다시 적용할 수 있다.
- 과대 출력/NUL은 실패다. 빈 결과와 잘못된 텍스트/실행 오류를 구분한다.

native text 최대 4,096 UTF-8 bytes, span 최대 4,096개, 이전 context 1개를
유지한다. 모델 no-speech score/반복률/신뢰도 임계값은 새로 적용하지 않았다.
효과음·음악 환각 억제와 coarse timestamp의 토큰 단위 정합은 추가 자료가 필요하다.

## 검증

`scripts/check.ps1`은 정합/반복 보존/빈 결과의 코어·worker·C# IPC를 확인한다.
`scripts/probe-worker-live-asr.ps1 -NoBuild -Offline -Boundaries`는 3초 디지털 무음
WAV와 기존 en-10 TTS 4회 연결 WAV를 재생한다. 연결 fixture의 끝 padding은
PCM16 절댓값 128 기준으로 제거하며 정답/자연 음성 품질 자료로 사용하지 않는다.
다른 시스템 오디오를 격리하지 않으므로 무음 실패는 그대로 보존한다.
전체 원문과 WAV/보고서는 ignored 결과 폴더에만 저장한다.
[실행 결과와 한계](evidence/T02-04b-windows-asr-reconciliation.md).
