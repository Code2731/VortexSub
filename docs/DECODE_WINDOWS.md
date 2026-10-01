# 전사 입력 구간 분리

## 현재 상태: owner 연결, 기본 꺼짐

2026-10-02 실제 부분 전사 owner에 연결했다. 실험 실행은
`run-live-cuda-fast.bat -DecodeWindow -CaptionTiming`이다. UI의 부분 전사/번역도
세션 시작 전에 켠다. 기존 실행은 DTW/window를 끈다. 아직 실시간 속도 개선이
확인되지 않아 이 옵션은 비교용이다.

적용된 같은 세션·epoch·segment의 인접 revision 두 개에서만 정렬을 만든다.
다음 revision의 부분 작업만 이를 사용할 수 있다. Pause/Stop/최종/폐기/epoch
전환에서 mapping을 비운다. 원문/metadata 최대 4096 bytes/tokens와 prefix
1024 bytes를 제한하며 callback에는 정렬/추론 작업을 넣지 않는다.

owner는 전체 snapshot을 보관하고 native 입력 slice만 줄인다. 재결합이 실패하면
같은 job identity와 예약으로 전체 PCM을 다시 전사한다. 새 단일 사용 attempt는
기존 취소 요청·running/abort 상태를 공유한다. 취소된 작업은 재시도하지 않는다.
축소 시도 후 캐시를 비우므로 다음 후보에 새 전체 관측 두 개가 필요하다.
확정 작업은 전체 입력이다(DTW 엔진 설정은 실험 세션 내 유지).

`asr.completed`의 `window_attempted`/`window_fallback`은 텍스트 없는 boolean이다.
state의 `decode_window_enabled`, `window_attempts`, `window_fallbacks`도 확인할 수 있다.
`decode_s`는 fallback 비용까지 포함하며 `input_samples`는 보관한 전체 snapshot
크기다. 로그 요약의 `decode_window_completions`는 수신된 완료 이벤트 횟수다.

실제 paced ASR→Qwen 비교 각 3회: 첫 번역 중앙값 **1.830→1.841초**,
누적 decode **1.296→1.477초**, 확정 번역 **8.033→8.063초**다.
각 window 실행에서 축소 2회/전체 fallback 1회였고 최종 원문은 6회 같았다.
속도 개선을 확인하지 못했으며 VAD/캡처/화면·게임/자연 음성은 미검증이다.
[owner 비교와 강제 fallback 근거](evidence/window-owner-windows-20261002.md).

아래는 기반/파일 정렬을 구현할 당시의 기록이다. 다음 구현 우선순위는
합의한 세 번째 단계인 ASR 후보 비교다. 현재 Whisper/DTW 조합의 이득은
확인되지 않았으므로 기본 속도 개선으로 채택하지 않는다.

## 이번 구현

`Pipeline::submit_asr_window`는 자막 전체 `product_range`와 실제 PCM suffix
`window`를 분리한다. 기존 `submit_asr`는 두 범위를 같게 전달한다.
기록·export의 시간 범위와 revision은 전체 구간을 기준으로 유지하고,
snapshot pool은 window만 복사한다. window는 전체 범위 안에 있어야 하며
끝은 전체 구간의 끝과 같아야 한다. 잘못된 범위는 상태 변경 전에 거부한다.

fixture에서 짧은 입력 snapshot, 전체 기록 범위, 확정 우선, 진행 중 부분
작업의 취소 예약, 오래된 revision 무시를 확인했다.

## 정렬과 재결합

2026-10-02 후속으로 별도 DTW 발화 시점 정렬을 추가했다. 기존 interval을
고쳐 쓰지 않고 `Token.dtw_ms`에 점으로 저장한다. `prefix_dtw`는 연속 byte
coverage, PCM 범위, 발화점 순서와 두 단어 anchor를 검증한다. 경계 기준은
마지막 발화 단어의 점이며 문장부호의 발화점은 제외한다. 두 관측의 일치와
새 전사의 정확한 anchor/시간 일치를 모두 통과해야 재결합한다.

`AsrEngine::load_dtw_base`는 기존 multilingual base 모델 전용 파일 진단이다.
128 MiB 정렬 메모리 설정과 flash attention 비활성화를 명시한다. 일반 load와
라이브 owner는 계속 DTW를 끈다. 시작·끝 길이 0의 기존 interval은 여전히
거부되며 DTW 점을 임의의 단어 시작·끝 시간으로 바꾸지 않는다.

기반 구현 당시 `decode_window`는 **fixture/파일 probe에만 연결**되어 있었다.
native 텍스트의 byte offset과 입력 기준 밀리초를 보존해 절대 sample로 변환한다.
연속 token coverage, UTF-8/단어 경계, 시간 순서, PCM 범위를 검사한다.
단어의 길이 0, 누락된 시간, prefix 변경은 잘라낼 근거로 인정하지 않는다.

같은 안정 prefix를 두 번 관측하고 끝 시간 차이가 0.16초 이내일 때만
0.608초 문맥을 남긴 후보를 만든다. 시작은 512-sample 경계로 내림한다.
남겨 둔 구간에는 정확히 일치하는 최소 두 단어의 anchor가 필요하다.
새 전사의 앞부분과 anchor 텍스트·시간이 모두 일치하면 안정 원문에 suffix를
붙인다. 이후의 실제 반복 단어는 유지하며 fuzzy matching은 사용하지 않는다.

## 적용 상태와 근거

기반 구현에서는 재결합 없는 window job을 거부했다. 현재 owner도 짧은 snapshot은
거부하고 전체 snapshot과 Plan을 사용하므로 fallback 입력이 항상 남는다.

Windows·RTX 3080·Whisper base CUDA·기존 7.605초 영어 파일에서 3초/4초
입력 모두 `path`가 2.00→2.00초로 나와 후보를 거부했다. 잘린 전사는 실행하지
않았다. 이번 라운드에서 추가 지연 개선이나 품질 통과는 확인하지 못했다.

재현: `scripts/probe-partial-scheduling.ps1 -Trim -Backend cuda -WavPath <WAV>`.
CPU도 선택할 수 있다. [측정 기록](evidence/decode-window-windows-20261002.md).

다음은 신뢰할 수 있는 단어 시간 정렬의 확보와 동일 입력 검증이다. 그다음
적용된 revision에만 alignment를 보관하고 owner에서 재결합 실패 시 전체 PCM을
재전사하도록 연결한다. 확정 작업은 전체 PCM으로 유지한 채 지연·오역·수정 빈도를
비교한다는 초기 계획이었다. 현재 연결/비교 결과와 실험 옵션은 위에 기록했다.

## DTW 파일 비교

일반/DTW 조건을 각각 3회, 순서를 교대해 기존 파일로 비교했다. 일반 interval은
6개 prefix 관측 모두 거부됐다. DTW는 `path`가 모두 1.54초였고, 0.928초를
제외한 6.677초 입력을 전사했다. 3회 모두 재결합 원문이 전체 전사와 같았다.
처리 시간 중앙값은 일반 전체 0.182729초, DTW 전체 0.205880초,
DTW 축소 0.174018초다. 일반 전체 대비 약 0.008710초 차이로 작으며
정렬을 만드는 앞선 전사의 추가 비용도 있다. 라이브 지연 개선 근거는 아니다.

재현 시 위 명령에 `-Dtw`를 추가한다. 새 모델/런타임은 필요하지 않다.
다음은 적용된 identity/revision에만 mapping을 저장하는 owner 경로와
재결합 실패 시 전체 입력 fallback이다. DTW 자체 비용과 실제 partial의
prefix 변경·정렬 거부율을 비교한 후 활성화를 결정한다.
[DTW 측정·제약](evidence/dtw-alignment-windows-20261002.md).
