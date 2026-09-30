# T02-01a Windows worker native ASR 검증

실행일: 2026-09-30. Windows NT 10.0 build 26200, AMD64 Family 25 Model 33/논리 16개, Rust 1.90.0 MSVC, .NET 10.0.102. Whisper base CPU/8 threads, whisper-rs 0.14.4·whisper.cpp 1.7.4, `/O2` release. 기존 모델 SHA-256은 `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe`다.

## 재현 명령과 범위

```powershell
$env:PATH = "$env:USERPROFILE/.cargo/bin;$env:PATH"
$env:ECHOSUB_OFFLINE = '1'
$env:ECHOSUB_NUGET_SOURCE = "$env:USERPROFILE/.nuget/packages"
.\scripts\check.ps1
.\scripts\probe-worker-asr.ps1 -Backend cpu -Offline
```

최종 코드에서 위 probe 명령을 다시 실행했다. 원본 summary는 Git 제외 경로 `benchmarks/results/worker-asr-cpu-20260930-214519/report.json`에 있다. 로컬 fixture는 en/ko TTS 각 10개와 exact-zero WAV 1개다. 음원·전사문·full logs를 커밋하지 않았다. 다운로드하지 않았다.

## 결과

| 항목 | 관측값/결과 |
|---|---|
| 모델 hash 검증+context 준비 | 0.273443초 |
| 실제 전사 final/history 전달 | 20/20, 평균 0.577181초·최대 0.733193초 |
| 로딩/추론 중 반복 ping 최대 응답 | 0.015221초 |
| 실제 native running 관측 뒤 epoch 취소·재시작 | 10/10 |
| reset 응답 최대 | 0.000187초 |
| reset 요청부터 이전 native 완료 이벤트까지 최대 | 0.565063초 |
| 추론 중 shutdown→프로세스 exit 0 | 0.516145초 |
| digital silence | suppressed 1, ASR 호출 0 |
| 잘못된 WAV SHA | INPUT_HASH_MISMATCH, ASR 호출 0 |
| 잘못된 모델 SHA | model Failed 후 ping/정상 종료 가능 |
| 기본 저장소 확인 | Rust 74개·fmt·workspace 빌드·C# 빌드/확장 IPC smoke PASS |

취소된 10개는 `asr.completed.applied=false`, `abort_observed=true`, history Discarded다. reset 직후 새 WAV를 제출하여 old native full 반환 뒤 다음 작업이 실행되는 경로를 확인했다. 같은 프로세스의 Ready event는 1회였고 재시작 원문 10개 모두 기준 전사와 일치했다. 해당 프로세스 completed_jobs=40으로 20개 정상+10개 취소+10개 재시작과 일치했다. 별도 shutdown 프로세스도 실제 native_running을 관측했다.

Rust fixture는 WAV 해시 오류, 비지원 rate, 8초 초과, empty/1 MiB 초과 파일과 ASR-only final의 번역 None을 확인한다. 기본 worker의 새 진단 명령 opt-in 거부는 Rust protocol/C# smoke 양쪽에서 확인한다.

## 해석과 미검증

전사 시간은 전체 WAV를 native full에 넣어 반환받은 시간이다. 발화 종료부터 자막 표시까지의 latency가 아니다. reset 응답은 취소 요청 접수이며 native 반환과 다르다. 관측 최대값을 취소의 고정 기한으로 사용하지 않는다. 측정 초기에 기본 check 빌드를 함께 실행했으며 다른 프로세스 부하를 통제한 성능 벤치마크가 아니다.

파일 입력은 VAD 없이 final 후보 1개로 처리한다. 실제 Silero·비영 잡음 억제·WASAPI·부분 전사·번역 HTTP·UI history/오버레이·GPU worker 실행·Mac·자연/일본어 음성·게임 부하는 미검증이다. 따라서 T02-01은 PARTIAL, M2 gate는 미통과다. UI 스크린샷은 해당 변경이 없어 추가하지 않았다.
