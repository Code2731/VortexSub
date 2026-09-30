# Token timestamp 경계 정합

## Native 연결

worker는 고정 Whisper 1.7.4 / whisper-rs 0.14.4에서 token timestamp를 켠다.
기존 model-probe의 기본 decode는 기존 설정을 유지한다. native 결과의 token ID가
EOT보다 작은 경우 원본 bytes와 상대 시간을 수집한다. bytes를 합친 결과가
segment 원문과 정확히 일치할 때만 offset metadata를 사용한다. 개별 token의
잘못된 UTF-8을 대체 문자로 변환하지 않는다. decode당 metadata 최대 4,096 token,
원문 최대 4,096 bytes와 이전 확정 context 1개를 유지한다.

`native_running`은 native full 실행 중에만 true다. timestamp 추출은 full 반환
뒤 같은 owner에서 수행하며, 완료 응답 전까지 단일 실행 예약/PCM 소유권은 유지한다.
`source_token_alignment` capability는 native owner를 구성한 경우 제공한다.

## 판정 규칙

기존 [완전한 span 정합](ASR_RECONCILIATION.md)을 먼저 적용하고 token 판정이 더
긴 prefix를 안전하게 제거할 수 있을 때 사용한다. 동일 session/epoch의 명시적
continuation과 실제 PCM 겹침(최대 0.608초)이 필요하다.

현재 prefix와 이전 suffix의 모든 token이 겹침 안에 시간 순서대로 들어와야 한다.
원문 문자열도 정확히 일치해야 한다. token offset은 UTF-8 문자 경계이며 ASCII
공백으로 구분된 완전한 단어 경계에서만 내부 절단한다. 공백이 있는 한글도 이 규칙을
따르지만 공백 없는 일본어/한글과 결합 문자·ZWJ emoji 내부는 보존한다.
시간 길이가 0인 ASCII 문장부호/공백은 유효하고 순서가 맞는 시간 점으로만 허용한다.
시간을 모르는 단어, 경계를 넘는 token, 불연속 byte coverage는 원문을 유지한다.
문자열 유사도나 신뢰도 임계값을 새로 적용하지 않았다.

## 입력과 결과 진단

`asr.completed`에 아래 계측을 추가했다. 원문/token bytes를 이벤트에 넣지 않는다.

- `input_samples`, `nonzero_samples`: 실제 immutable decode PCM의 크기와 0이 아닌 sample 수.
- `timed_token_count`: PCM 범위 안의 양수 길이 token 시간 개수. 정확도 점수가 아니다.
- `overlap_tokens_removed`: 제거한 prefix token 수. 기존 `overlap_segments_removed`와 구분한다.

## 검증 범위

`scripts/check.ps1`은 token offset/시간/동일 제품 ID와 실제 반복 보존을 검사한다.
`scripts/probe-worker-vad.ps1 -NoBuild -Offline`은 정확한 0 PCM 8초 WAV를 75회
처리한다. **600초 PCM 파일 분량**이며, 600초 동안 실시간 loopback을 관측한
결과가 아니다. 외부 재생이 파일 PCM에 섞일 수 없으므로 디지털 무음 처리의 근거로
사용하고 loopback 무음 gate는 별도로 유지한다.
`-Boundaries`는 실제 loopback 시간/token/PCM 계측을 확인한다. 승인 대기나 다른
음원 혼입으로 실패하면 그대로 보존한다. 자연 음성·실제 dedup 효과·UI·macOS
수용은 [이번 실행 근거](evidence/T02-04c-windows-token-alignment.md)를 따른다.
