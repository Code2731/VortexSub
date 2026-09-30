# T02-02b Windows CPU live 전사 검증

2026-09-30~2026-10-01. Windows NT 10.0 build 26200, AMD64 Family 25 Model 33/논리 16개, Rust 1.90.0 MSVC/.NET 10.0.102. native-vad release worker, 기존 Whisper base/Silero v6.0/ORT 1.22.0 CPU 자산을 사용했다. 새 다운로드는 없다. en-01 TTS에 실제 무음 1초를 붙여 PlaySound로 기본 render 장치에 반복 재생하고 종료 시 자체 재생을 정리했다. PCM fixture와 전체 report는 Git 제외다.

## 재현과 결과

```powershell
.\scripts\probe-worker-live-asr.ps1 -Offline
.\scripts\probe-worker-vad.ps1 -Offline -NoBuild
.\scripts\probe-worker-capture.ps1 -Offline
$env:ECHOSUB_OFFLINE='1'
$env:ECHOSUB_NUGET_SOURCE="$env:USERPROFILE/.nuget/packages"
.\scripts\check.ps1
```

최종 live report: `benchmarks/results/worker-live-asr-cpu-20261001-001048/report.json`.

| 검사 | 관측 결과 |
|---|---|
| 실제 loopback format | 48 kHz stereo float32, mask 0x3 |
| 독립 Start에서 source final/history 확인 | 2회; 원문 비어 있지 않음·번역 None |
| 확인한 final PCM 구간 | 1.184초, 1.888초; 최대 128,000 samples |
| 재시작 gap 최소 | 0.364750초; 첫 sample이 이전 수신 끝보다 앞서지 않음 |
| native 실행 중 Stop/재시작 | 3회; 이전 epoch history Discarded·늦은 final 반영 없음 |
| 이전 full 예약 유지 중 새 캡처 시작 | 1회; decoding=true 확인 후 restart 접수 |
| Stop 응답 최대 | 0.000797초 |
| Stop→capture/VAD join 확인 최대 | 0.043802초 |
| join 확인 이후 기존 full 반환 확인 최대 | 0.409312초; Stop 총시간과 다름 |
| 처리 중 ping 최대 | 0.001573초 |
| Stop 이후 PCM 증가 | 없음 |
| VAD 모델 잘못된 해시 | LIVE_VAD_MODEL_FAILED·history empty·제어 유지 |
| 파일 입력/live 병용·language 누락 | UNSUPPORTED_CAPABILITY·INVALID_REQUEST |
| 활성 shutdown/부모 stdin EOF | exit 0·소유 owner 정리 |
| 기본 검증 | Rust 89개·fmt/workspace·C# 빌드/IPC smoke PASS |

기존 파일 VAD 회귀도 `worker-vad-cpu-20260930-235136/report.json`에서 PASS다. final 32개·Failed 1개·비음성 억제 3개, VAD 2,035회·epoch reset 10회·두 발화 2개·모델/DLL 해시 거부를 유지했다. ko-08 추가 조각 InvalidText 때문에 품질 gate는 false다.

## 경계 품질과 실패 관측

중간 live run의 final 하나가 8초 chunk 상한에 도달했다. 최종 run의 두 구간은 1.184초·1.888초였지만 경계 수용 검증은 아니다. 이 결과는 자연스러운 발화 분리나 원문 정확도 수용 근거가 아니다. 시작 시 looping 음원의 중간을 받을 수 있고 다른 시스템 출력도 격리하지 않았다. 자체 재생을 멈춘 뒤 0.6초 동안 추가 ASR 완료는 없었다. 최종 run은 모델 호출 0회였지만 중간 run은 nonzero PCM의 모델 호출 19회가 관측됐다. 실제 digital-silence 입력이나 비음성 억제 통과로 해석하지 않는다. 품질 gate는 false다.

중간 harness에서 float 초 차이의 8초 경계 비교가 한 번 실패했다. 정밀 범위 검사를 sample 개수로 변경하고 실제 범위를 report에 추가했다. 최종 검증은 통과했으며 최초 실패의 원인을 단정하지 않는다. 파일 회귀/빌드를 동시에 수행한 추가 run은 Running 대기 timeout으로 끝났다. 이후 단독 live run은 통과했다. 동시 부하 수용을 주장하지 않으며 해당 초기 run의 세부 capture 오류 코드는 수집하지 못했다. Running 대기에도 오류 상태를 즉시 표시하도록 harness를 보완했다.

## 미검증 범위

자연/일본어/한국어 live 음성·음악/게임 효과음·발화 경계 정확도·overlap 텍스트 정합, 장기 QPC drift/정밀 지연, 제품 session/Pause/UI·번역/partial, 실제 장치 전환/분리·notification, 실제 큐 고갈/출력 stall stress·10분 soak·CUDA worker·Mac은 미검증이다. 측정 시간은 해당 장치의 관측값이며 고정 종료 deadline이 아니다. M2 제품 gate는 미통과다. 다음은 제품 session과 UI 원문 표시를 연결하고 경계/부하 검증을 계속한다.

## 추가 PCM 회귀와 진단 보완

최종 기본 worker PCM 회귀는 `worker-capture-20261001-000839/report.json`에서 PASS: Start/Stop 3회, 609 packets·6.048초 PCM, Stop 응답 최대 0.000214초·join 확인 0.031204초, 활성 shutdown/EOF. 앞선 재시도에서는 Opening/native_phase=OpeningClient가 5초 이상 지속되는 timeout을 관측했다. 세부 native API phase를 추가해 ActivateAudioClient/InitializeAudioClient/StartAudioClient 등의 대기 위치를 구분한다. 재시작 안정성 문제의 원인은 미확정이며 부하/장기 수용을 보류한다.

또한 빠른 종료 후 PID로 연결한 C# Process의 ExitCode 조회가 실패하는 harness 오류를 확인했다. shutdown 전 Handle을 확보하고 최상위 예외를 exit 1로 처리해 unhandled crash report에 검증 프로세스가 남지 않도록 수정했다. 이전 실패에서 남은 소유 CaptureSmoke 프로세스 1개는 PID·명령행을 확인해 정리했다. 최종 PCM/live 회귀는 이 보완 후 통과했다.
