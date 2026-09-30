# T02-04a Windows opt-in 부분 전사

2026-10-01, Windows build 26200 / AMD64 16 logical CPU, Whisper base CPU,
기존 Silero v6.0 / ORT 1.22.0. ASR-002의 Windows 연결 일부다.

## 구현

기본 false인 `config.partial_enabled`와 capability를 추가했다. Live VAD의 부분
요청을 동일 제품 구간/revision에 연결하고 확정 요청의 취소를 native token에
전달한다. 실행 예약은 반환 전까지 유지한다. 교체된 요청의 언어 metadata를
정리한다. Pause/Stop/새 epoch는 active partial을 폐기한다.
UI 체크박스와 인식 중/확정 대기 표시, 적용 revision 기준 5초 만료를 연결했다.
확정 원문만 번역/SRT 대상으로 사용하는 기존 계약은 유지한다.
[상세 계약](../WORKER_PARTIAL_ASR.md).

## 실행 결과

- `ECHOSUB_OFFLINE=1`, 로컬 NuGet source로 `scripts/check.ps1`: Rust **98개**,
  포맷/workspace 빌드, Desktop/ProtocolSmoke/TranslationProbe 빌드 및 C# IPC PASS.
  C# 빌드 경고/오류 0. 기본 꺼짐, typed partial revision 갱신/확정,
  Pause/Resume, VAD 부분/확정 ID 일치, 취소 요청과 반환 전 예약을 확인했다.
- 새 fixture 작성 중 검사는 두 번 실패했다. 코어 완료 시각 0이 admission
  시각보다 이전이라 ClockRegression이 발생해 단조 시각을 사용했다. IPC fixture의
  필수 retain/source_language 누락도 수정했다. 제품 계약을 완화하지 않았다.
- `scripts/build-model-probe.ps1 -Backend cpu -Package echosub-worker -Vad -Offline`
  PASS. 모델/런타임 추가 다운로드 없음.
- `scripts/probe-worker-live-asr.ps1 -NoBuild -Offline -Partials`: 첫 실행은
  InitializeAudioClient에서 10.003353초 시작 timeout. PCM 수신 0, 부분 전사에
  도달하지 못했다. 실패 checkpoint를 보존했으며 원인 분석은 보류했다.
- 새 worker로 한 번 재실행 PASS. 기존 영어 TTS `en-10.wav`를 실제 loopback으로
  재생했다. 두 UUID/세 epoch의 확정 원문 **3개**, 적용된 부분 revision **6개**를
  관찰했다. 각각 동일 구간의 revision 1/2 부분 원문이 revision 4 확정으로 갱신됐다.
  대기 언어 metadata 최대 3개 검사, native 연산 중 Pause, Resume의 새 epoch/ID,
  늦은 기록 폐기, Stop→Idle/native 반환, TXT/SRT export 및 기록 삭제 PASS.
  Pause 응답 0.001037초, Stop 응답 0.000206초, Stop부터 Idle 0.508744초.
  이번 Resume는 이미 native 반환 뒤였으며 반환 전 Resume 실측으로 확대하지 않는다.

Git 제외 원본 보고서:
`benchmarks/results/worker-live-asr-cpu-20261001-051209/report.json` (실패),
`benchmarks/results/worker-live-asr-cpu-20261001-051257/report.json` (성공).
전체 원문·WAV·export 파일은 해당 ignored 디렉터리에만 보존한다.

## 남은 수용

실제 체크박스/오버레이 렌더링·5초 만료 조작, 자연 음성/게임/일본어 품질,
overlap 텍스트 정합, 번역, macOS는 미검증이다. 다른 시스템 오디오를 격리한
시험이 아니며 이번 성공으로 시작 안정성/전체 품질 gate를 올리지 않는다.
다음은 기존 계획의 overlap/무음 결과 정합이다.
