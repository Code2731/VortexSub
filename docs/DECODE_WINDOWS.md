# 전사 입력 구간 분리

## 이번 구현

`Pipeline::submit_asr_window`는 자막 전체 `product_range`와 실제 PCM suffix
`window`를 분리한다. 기존 `submit_asr`는 두 범위를 같게 전달한다.
기록·export의 시간 범위와 revision은 전체 구간을 기준으로 유지하고,
snapshot pool은 window만 복사한다. window는 전체 범위 안에 있어야 하며
끝은 전체 구간의 끝과 같아야 한다. 잘못된 범위는 상태 변경 전에 거부한다.

fixture에서 짧은 입력 snapshot, 전체 기록 범위, 확정 우선, 진행 중 부분
작업의 취소 예약, 오래된 revision 무시를 확인했다.

## 정렬과 재결합

worker의 `decode_window`는 현재 **fixture/파일 probe에만 연결**되어 있다.
native 텍스트의 byte offset과 입력 기준 밀리초를 보존해 절대 sample로 변환한다.
연속 token coverage, UTF-8/단어 경계, 시간 순서, PCM 범위를 검사한다.
단어의 길이 0, 누락된 시간, prefix 변경은 잘라낼 근거로 인정하지 않는다.

같은 안정 prefix를 두 번 관측하고 끝 시간 차이가 0.16초 이내일 때만
0.608초 문맥을 남긴 후보를 만든다. 시작은 512-sample 경계로 내림한다.
남겨 둔 구간에는 정확히 일치하는 최소 두 단어의 anchor가 필요하다.
새 전사의 앞부분과 anchor 텍스트·시간이 모두 일치하면 안정 원문에 suffix를
붙인다. 이후의 실제 반복 단어는 유지하며 fuzzy matching은 사용하지 않는다.

## 적용 상태와 근거

라이브는 계속 전체 PCM을 입력한다. native owner는 아직 재결합이 연결되지
않은 window job을 `Failed`로 거부해 suffix가 전체 원문으로 발행되는 것을 막는다.
이는 live trim 완료가 아니라 그 구현을 위한 범위/정렬 계약이다.

Windows·RTX 3080·Whisper base CUDA·기존 7.605초 영어 파일에서 3초/4초
입력 모두 `path`가 2.00→2.00초로 나와 후보를 거부했다. 잘린 전사는 실행하지
않았다. 이번 라운드에서 추가 지연 개선이나 품질 통과는 확인하지 못했다.

재현: `scripts/probe-partial-scheduling.ps1 -Trim -Backend cuda -WavPath <WAV>`.
CPU도 선택할 수 있다. [측정 기록](evidence/decode-window-windows-20261002.md).

다음은 신뢰할 수 있는 단어 시간 정렬의 확보와 동일 입력 검증이다. 그다음
적용된 revision에만 alignment를 보관하고 owner에서 재결합 실패 시 전체 PCM을
재전사하도록 연결한다. 확정 작업은 전체 PCM으로 유지한 채 지연·오역·수정 빈도를
비교한다. 검증 전에는 live trim 옵션을 노출하지 않는다.
