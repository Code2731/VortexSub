# T02-02d Windows 모델 없는 캡처 시작 비교

2026-10-01. Windows NT 10.0 build 26200, AMD64 Family 25 Model 33/논리 16개, Rust 1.90.0 MSVC/.NET 10.0.102. T02-02c의 STA·shared/event-driven 기간 0·10초 실패 정책을 사용한다. default release worker이며 ASR/VAD 모델을 로드하거나 음원을 재생하지 않았다. 외부 시스템 오디오는 격리하지 않았다.

## 실행

```powershell
.\scripts\probe-capture-startup.ps1 -Offline -Rounds 2
.\scripts\probe-capture-startup.ps1 -Offline -NoBuild -Rounds 10 -DeviceId 'default','{0.0.0.00000000}.{690293db-6104-45e0-854b-5022cdf0e0c4}','{0.0.0.00000000}.{d7b53170-8d49-41b7-92de-756e86b26279}'
.\scripts\probe-capture-startup.ps1 -Offline -NoBuild -Rounds 2
```

새 worker마다 hello와 제어·empty history, 시작 관측, 정상 shutdown을 확인했다. 최종 harness는 implementation/capability와 model=NotInstalled/native ASR disabled도 확인한다. 새 다운로드·기본 장치 변경·드라이버 재시작·PCM 저장은 없다. endpoint manifest와 전체 report는 ignored `benchmarks/results/`에 보존한다.

## 결과

| report (`benchmarks/results/` 아래) | 결과 |
|---|---|
| `capture-startup-20261001-010635/report.json` | 기본 선택 + 활성 7개 고정 endpoint ×2 =16회. 정상 시작 15, Initialize timeout 1. case 종료는 16회 모두 exit 0, 강제 종료 0. matrix exit 1로 실패 보존 |
| `capture-startup-20261001-010755/report.json` | default/Steam Streaming Speakers/GSX Main 고정 ×10 =30회 모두 정상 시작·종료. default 시작 0.023035~0.033851초, Steam 0.027787~0.058775초, GSX 고정 0.027939~0.034102초 |
| `capture-startup-20261001-011012/report.json` | 최종 전체 16회 정상 시작·종료. 시작 0.029529~0.062841초, Ping 최대 0.000601초, 정리 최대 0.041821초. accepted PCM 합계 0, 강제 종료 0 |
| `capture-startup-20261001-011558/report.json` | Windows PowerShell 5.1에서 `-NoBuild -Rounds 1 -DeviceId default` 실행 PASS, 강제 종료 0 |

첫 matrix의 실패는 Steam Streaming Speakers 첫 실행이다. `opening_elapsed_s=10.017561`, `failure_native_phase=InitializeAudioClient`, accepted PCM 0, `awaiting_capture_join=true`였다. Failed 뒤 Ping까지 응답했으며 해당 case Ping 최대 0.000229초다. join 대기 중 재시작은 INVALID_STATE로 거부됐고 shutdown/프로세스 종료는 0.924161초에 완료됐다. 새 worker로 같은 장치의 다음 실행은 정상 시작했다. ASR/VAD가 없어도 같은 API 위치의 timeout이 발생하므로 모델 추론은 이 실패의 필수 조건이 아니다.

첫 matrix에서 Realtek의 첫 시작은 약 7.88초, 기본 GSX 선택은 약 2.00/3.06초로 이후 반복보다 느렸다. 이 값만으로 특정 드라이버 또는 오디오 엔진 상태가 원인이라고 단정하지 않는다. endpoint 순서는 고정·장치별로 묶여 있고 worker 간 대기는 0.5초다. 새 프로세스가 OS/오디오 엔진까지 초기화한 것은 아니다. 이후 46회 정상 시작을 근본 원인 해결이나 오류 확률의 추정으로 확대하지 않는다. 시작 안정성 gate는 미통과다.

## 검증 범위와 후속

실제 Failed→제어 응답 유지→join 전 재시작 거부→정상 소유 프로세스 정리를 확인했다. matrix 62회와 Windows PowerShell 5.1 추가 1회, 총 63회에서 정상 시작 62/timeout 1이며 모두 정상 종료했다. 5초 정리 대기 후 owned process 강제 종료 fallback은 구현했으나 이번에는 사용되지 않아 해당 branch의 실측 수용은 미검증이다. 실패 뒤에도 matrix 전체를 진행하고 case별 결과를 보존한다.

최종 `scripts/check.ps1` PASS: Rust fmt/91개 시험/workspace build, C# Desktop·ProtocolSmoke·TranslationProbe 빌드(경고·오류 0), C#↔Rust smoke. CaptureSmoke의 신규 startup 모드 빌드·실행도 통과했다. 종료 후 프로세스 조회에서 worker/CaptureSmoke 잔여는 없었다.

일반 UI는 MOCK이며 제품 session/Pause·실제 자막 UI·장치 전환/분리·게임/장기 부하·CUDA/Mac은 미검증이다. 다음 진단 UI 연결에는 Opening 진행, Failed 원인, pending join 동안 Start 금지, 소유 worker 종료/재연결을 명시적으로 반영한다. 자연 음성/게임 품질 gate는 별도다.
