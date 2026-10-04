# 첫 ASR admission 이전 계측 — P0.3

2026-10-02 Windows 구현. 기존 **자막 지연 기록** 옵션으로 새 metadata를 저장한다.
VAD·ASR 요청 기준과 번역/읽기 정책을 변경하는 성능 튜닝은 아니다.
[개선 계획](TRANSLATION_IMPROVEMENT_PLAN.md)의 RI-03/06에 대응한다.

## 기록 시점과 시간축

| 필드/이벤트 | 의미 |
|---|---|
| `capture.voice_observed` / `voice_observed_worker_s` | VAD가 첫 voiced frame에서 Started를 만든 batch의 처리 완료 시각 |
| `capture.asr_eligible` / `first_eligible_worker_s` | 첫 VAD partial 또는 final 후보 batch의 처리 완료 시각 |
| `first_admitted_worker_s` | 해당 VAD segment의 첫 `queued=true` ASR 요청 submit 시각 |
| `capture.partial_deferred` | ASR 작업, 번역 preview, 적응형 추가 오디오 대기에 따른 보류 관측 |
| `first_voiced_frame_audio_s` | Started를 만든 현재 frame의 sample 위치. 지연 계산용 clock이 아님 |
| `processing_started_worker_s` | 해당 batch의 dequeue 후 처리 시작 시각. 캡처 callback 시각이 아님 |
| `processing_kind` / `vad_processing_s` | push 또는 timeout poll, 해당 batch의 처리 시작→완료 기간 |
| `voice_vad_started_worker_s` | 첫 voiced frame batch의 처리 시작 시각. 후속 admission에도 연결 |

`eligible`은 **VAD 후보가 나왔음**을 뜻한다. whisper의 입력 최소 길이 충족이나
최종 scheduler/model 승인까지 보장하는 이름은 아니다. partial off이면 처음
관측되는 후보는 final이므로 발화 종료 대기도 이 구간에 포함한다.

VAD 처리 스레드의 `Instant`를 worker origin으로 변환한다. 별도 VAD scheduling
clock이나 C# Stopwatch와 직접 빼지 않는다. `worker_at_s - observed_worker_s`는
VAD batch 처리 후 worker 수신/발행까지의 대기이며 native packet callback 시간은 아니다.
Started는 VAD-positive 관측이다. 실제 음성이 귀에 들린 시점이나 minimum speech
수용 완료 시각이 아니며 짧은 false positive는 discard로 남을 수 있다.

오디오 sample 시각과 관측 시각의 정확한 offset/오차는 이번에 검증하지 않았다.
물리적 음성 시작→캡처 입력 큐→VAD 처리 이전 비용은 이 계측으로 확정할 수 없다.
2026-10-03 처리 시작 시각을 추가했다. 기존 관측/eligible 시각은 처리 완료를
유지한다. 처리 기간에는 해당 owner batch의 작업이 포함된다. 순수 모델 추론
시간과 동일하지 않다. timeout poll에는 새 frame sample 위치를 만들지 않는다.
frame sample 위치를 worker 시각에서 빼서 임의의 전체 지연을 만들지 않는다.
긴 발화 continuation의 관측은 새 chunk 기준이며 전체 발화 시작과 다를 수 있다.

## 보관과 초기화

worker가 현재 VAD identity의 timing 하나만 보관한다. 첫 시각은 후속 partial
요청의 metadata에도 반복해 coalescing으로 첫 요청 이벤트가 사라지는 경우를 보완한다.
모든 timing은 session/epoch/VAD segment가 일치할 때만 연결한다.
Final 발행 후, discard, Pause/Stop/epoch 변경에서 초기화한다.
native callback에 JSON/추론/파일 기록을 추가하지 않았다.

보류 이유는 동시에 여러 개일 수 있다. reason 관측 수와 cumulative 보류 횟수는
초 단위 duration이 아니며 합산해 병목 시간을 계산하지 않는다.
latest partial의 기존 deferred wait는 마지막 보관 snapshot의 대기다.

## 요약과 사용

평소 쓰는 launcher로 실행하고 세션 시작 전에 **자막 지연 기록**을 켠다.
기존 빠른 CUDA launcher에서 실행 인자로 켜려면:

```powershell
run-live-cuda-fast.bat -CaptionTiming
./models/tabby/venv/Scripts/python.exe -X utf8 scripts/summarize-caption-timing.py logs/caption-timing-<실행>.jsonl
```

요약의 `pipeline.durations_s`에 다음 구간과 n/median/p95/min/max를 표시한다.

* `voice_observation_to_first_asr_eligible_s`
* `first_asr_eligible_to_first_admission_s`
* `voice_observation_to_first_admission_s`
* `vad_observation_to_worker_receipt_s`
* `voice_observation_to_first_translation_update_s`
* `vad_push_processing_s` / `vad_poll_processing_s`
* `voice_vad_start_to_first_admission_s`

`pipeline.pre_asr`에는 event/reason 관측과 미등록/discard/missing timing을 표시한다.
마지막 512 VAD identity의 누적 보류 횟수는 전체 세션 합계가 아니다.
`bounded_map_evictions`, 로그 drop, 누락 pair가 있으면 관측 범위가 불완전하다.
긴 로그의 identity eviction 뒤에는 중복 집계 가능성도 명시한다.
새 필드가 없는 과거 로그의 앞단 시간을 0으로 채우지 않는다.

## 이번 검증 범위

Windows에서 `cargo check -p echosub-worker --locked --offline`,
`scripts/build-model-probe.ps1 -Backend cuda -Package echosub-worker -Vad -Offline`,
Desktop `dotnet build --no-restore`를 통과했다. Python 문법과 diff 공백도 확인했다.
CUDA/VAD worker는 기존 release 경로에 빌드했고 C# 빌드는 경고/오류 0이다.
단위/통합 테스트, 실제 캡처, 새 timing log 및 지연 개선 비교는 미실행이다.
macOS도 미검증이다. 이 변경만으로 지연 원인을 확정하거나 속도가 개선됐다고 판단하지 않는다.
