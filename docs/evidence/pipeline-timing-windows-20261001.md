# 단계별 자막 지연 계측 (2026-10-01)

## 변경

기존 UI 자막 지연 옵션과 128개 background log queue를 사용한다.
`capture.partial_requested`/`capture.segmented`, `asr.started`/`asr.completed`,
`source.partial`/`source.final`, `translation.started`/`translation.completed`/
`translation.updated`에서 숫자·boolean metadata만 기록한다. 번역 시작 이벤트와
worker-local 초 단위 시점을 추가했고 기존 protocol version/텍스트 계약은 유지한다.
캡처 callback에서는 계측·디스크 작업을 추가하지 않았다.

요약은 worker PID+session/epoch/segment/revision/request로 연결한다.
worker/UI 시점의 직접 차이는 금지하며 없는 쌍은 0초로 채우지 않는다.
512개 상한과 coalescing/로그 고갈로 완전한 관측이 아닐 수 있다.
[통계 항목과 해석 제한](../CAPTION_READING.md)을 따른다.

## 확인

Windows에서 다음을 실행했다.

- `scripts/check.ps1`: Rust 153개, C# 표시 39개 assertion·HTTP/IPC·기본 빌드 PASS.
- `models/tabby/venv/Scripts/python.exe -X utf8 tests/test_caption_timing_summary.py`: 3개 PASS.
- `scripts/build-model-probe.ps1 -Backend cuda -Package echosub-worker -Vad -Offline`: native CUDA/VAD release 빌드 PASS.
- `cargo fmt --all -- --check`, `git diff --check`: PASS.

Rust IPC fixture는 source 시점/안정 길이, HTTP preview/final 요청의 시작·완료
identity/시점 순서를 확인한다. C#은 실제 fixture HTTP 이벤트를 UI logger에
전달해 시작/완료 시점과 원문/번역 텍스트 제외를 확인한다.
Python은 별도 원점의 worker 시계 계산, 누락 시작/다른 worker 분리와 구형
UI 로그 호환을 검사한다. 이는 MOCK source/local HTTP이며 실모델 품질 측정이 아니다.

첫 check에서 지난 영어 보류 변경과 충돌한 tail fixture가 실패했다. 두 번의
동일 열린 전사에서도 마지막 단어를 제외하므로 기존 입력이 `Do not open the`로
안정화됐다. 정상 tail 번역용 입력에 `now`를 붙여 `door`까지 안정화하고,
관사로 끝난 수정 tail은 계속 보류한다. 이후 전체 check PASS다.

Git 제외 출력: `benchmarks/results/pipeline-timing-check-20261001.log`.
기존 사용자 로그를 새 요약기로 읽어 UI 32건·0.033161초를 재현했고,
새 단계가 없는 로그는 `pipeline.events=0`으로 남긴다.

## 다음과 미실행

실제 캡처/ASR/HTTP 단계 로그와 물리 렌더, UI 클릭·게임·macOS는 미실행이다.
새 세션 시작 전에 자막 지연 기록을 켜고 수집한 로그로 단계를 비교한다.
기존 미완성 조건/숫자 trace의 실제 HTTP 품질/첫 출력 지연 비교도 남아 있다.
새 자산은 다운로드하지 않았다. 이번 변경으로 속도 개선을 주장하지 않는다.
