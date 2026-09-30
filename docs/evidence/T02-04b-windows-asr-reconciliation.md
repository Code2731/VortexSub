# T02-04b ASR 경계·빈 결과 정합

2026-10-01, Windows build 26200 / AMD64 16 logical CPU / Whisper base CPU,
기존 Silero v6.0·ORT 1.22.0. ASR-003/004 일부 구현이다.

## 구현과 저장소 검사

VAD continuation을 제품 ID로 연결하고 native timestamp/완전한 prefix-suffix
span으로 보수적으로 정합한다. 직전 final context 1개와 최대 3개 pending
metadata를 유지하며 partial은 context를 교체하지 않는다. 세션/epoch가 다르거나
PCM/timestamp/문자열이 불확실하면 원문을 유지한다. 빈 결과와 중복만 남은 결과는
NoSpeech/OverlapOnly 사유로 기록하며 확정 결과의 번역/SRT를 제외한다.
[계약](../ASR_RECONCILIATION.md).

- `ECHOSUB_OFFLINE=1`, 로컬 NuGet source, `scripts/check.ps1`: Rust **105개**,
  포맷/workspace build, Desktop/ProtocolSmoke/TranslationProbe build 및 C# IPC PASS.
- 새 fixture: 시간과 정확한 suffix 모두 필요함, 실제 반복 보존, 일본어/emoji,
  partial context 유지, 다른 epoch/연결 누락/경계 걸침/잘못된 시간 보존,
  과대 native 출력 실패, 빈/중복-only 결과 구분, 제품 continuation ID/metadata
  교체, 확정 skip 시 번역 미예약과 partial의 이전 적용 원문 보존을 확인했다.
- `scripts/build-model-probe.ps1 -Backend cpu -Package echosub-worker -Vad -Offline`
  PASS. 모델/runtime 다운로드 없음.
- CaptureSmoke 빌드 PASS(경고/오류 0). 작성 중 nullable 이벤트 인수 경고를 수정했다.

## 실제 loopback 관측

`scripts/probe-worker-live-asr.ps1 -NoBuild -Offline -Boundaries`의 마지막 실행은
**긴 발화 경계 PASS, 무음 미통과**로 전체 exit 1이다. 기존 en-10 TTS의 edge
padding을 PCM16 절댓값 128 기준으로 잘라 4회 연결하고 실제 시스템 출력으로
재생했다. 실제 8초 chunk 2개와 continuation을 확인했다.

- 구간 2: 4.303125~12.303125초, decode 0.697722초.
- 구간 3: 11.695125~19.695125초, `continued_from=2`, decode 1.957283초.
- 겹침 0.608초. 두 원문은 Final이며 Stop 후 Idle에 도달했다.
- native 제거 span 수는 둘 다 **0**이다. 실제 중복 제거 효과/대사 무누락을
  검증한 것으로 확대하지 않는다. timestamp가 거친 경계에 대한 추가 검증이 필요하다.
- 무음 관측 2.528초에서 이미 record 1개와 VAD model calls 79개가 있었다.
  수신 입력을 디지털 무음으로 격리하지 못했으므로 무음 gate를 올리지 않는다.
  전체 원문과 마지막 상태를 ignored checkpoint에 보존했다.

원본: `benchmarks/results/worker-live-asr-cpu-20261001-053700/report.json`.

## 앞선 실패와 수정

ignored 보고서는 삭제하지 않았다.

| 결과 폴더 suffix | 관측 |
|---|---|
| 053059 | InitializeAudioClient 10.003486초 timeout, 입력 PCM 0. 사용자 백신 승인 관찰과 같은 phase지만 이번 승인 창은 미확인 |
| 053147 | 실제 겹침 final은 생성됐으나 history snapshot acknowledgement가 continuation 이벤트를 소비해 probe가 실패. event 관측을 유지하는 별도 단일-page 조회로 수정 |
| 053359 | probe가 IPC history limit 64를 요청해 INVALID_REQUEST. 기존 상한 4로 수정 |
| 053459 | 무음 단계에서 모델 호출/record 발생. 이후 probe는 무음 미통과를 보존하면서 긴 발화 검증을 별도로 완료하도록 수정 |
| 053645 | 새 미설정 JsonElement checkpoint 직렬화 오류. nullable 초기값으로 수정 |

기본 0.6초 overlap이 VAD에서 19프레임/0.608초로 반올림되는 것을 실측해
정합 상한을 실제 설정에 맞췄다. 제품 history 상한/시작 deadline을 완화하지 않았다.

## 남은 수용

격리된 시스템 무음 10분, 효과음/음악의 모델 환각, 실제 한/일/영 긴 발화의
중복·누락, token timestamp를 활용한 경계 정합, 실제 UI/게임/macOS는 미검증이다.
시작 timeout 분석 보류도 유지한다. M2 전체 수용은 완료되지 않았다.
