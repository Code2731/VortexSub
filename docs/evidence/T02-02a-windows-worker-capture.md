# T02-02a Windows 실제 worker 캡처 검증

2026-09-30, Windows NT 10.0 build 26200, AMD64 Family 25 Model 33/논리 16개, Rust 1.90.0 MSVC/.NET 10.0.102. release worker, 기존 en-01 TTS WAV를 PlaySound로 기본 출력 장치에 반복 재생했다. 종료 시 자체 재생을 중지했다. 모델 다운로드·PCM 저장은 없다.

## 재현과 결과

```powershell
.\scripts\probe-worker-capture.ps1 -Offline
$env:ECHOSUB_OFFLINE='1'
$env:ECHOSUB_NUGET_SOURCE="$env:USERPROFILE/.nuget/packages"
.\scripts\check.ps1
```

최종 summary는 Git 제외 `benchmarks/results/worker-capture-20260930-224353/report.json`이다.

| 검사 | 관측 결과 |
|---|---|
| 실제 endpoint mix | 48 kHz stereo float32, mask 0x3 |
| 반복 Start→PCM 수신→Stop | 3회 PASS |
| native packets 합계 | 608 |
| worker accepted 16 kHz mono PCM 합계 | 6.048초 |
| Stop 요청 응답 최대 | 0.000219초 |
| Stop 요청→Stopped(join 완료) 최대 | 0.031628초 |
| 처리 중 ping 최대 | 0.002621초 |
| 잘못된 device_id 값 | INVALID_REQUEST |
| 존재하지 않는 endpoint | DEVICE_UNAVAILABLE; fallback 없음 |
| Stop 후 재조회 | accepted_audio_s 증가 없음 |
| 활성 캡처 중 shutdown/부모 stdin EOF | exit 0·소유 스레드 종료 |
| history | empty; live_asr=false |
| 기본 확인 | Rust 84개·fmt/workspace·C# 빌드/IPC smoke PASS |

Windows fixture 5개는 첫 discontinuity anchor·이후 glitch 거부, position gap/backwards QPC/overflow, timestamp error, pool 재활용/고갈 및 첫 오류 보존·Stop flag를 검사한다. 이 시험을 실제 오디오 callback 지연·큐 고갈 stress 통과로 해석하지 않는다. SDK buffer 소유권은 코드에서 같은 스레드 반환·반환 전 큐 연산/추론 없음으로 유지한다.

## 기존 VAD/ASR 회귀

native CPU/VAD worker를 다시 빌드하고 `scripts/probe-worker-vad.ps1 -Offline -NoBuild`를 실행했다. `benchmarks/results/worker-vad-cpu-20260930-224639/report.json`에서 제어/계약 검증은 PASS: final 32개, Failed 1개, 비음성 억제 3개, VAD 2,035회, epoch reset 10회, 두 발화 2개다. 기존 ko-08 추가 조각의 InvalidText 실패가 유지되어 `quality_gate_passed=false`다. VAD 평균 0.008610초·최대 0.016228초, ping 최대 0.002954초이며 파일 진단 결과다.

## 한계

이것은 짧은 PCM 수신/제어 검증이다. exact playback frame과 받은 PCM의 일대일 대조, 10분 통제 음원, 실제 기본/고정 장치 변경·분리, IMMNotificationClient, QPC→session audio clock/gap 매핑, 과부하·stdout stall 중 실제 capture stress, live VAD/ASR·UI·Mac은 미검증이다. Stop 시간은 해당 장치의 관측값이며 고정 deadline이 아니다. M2 제품 gate는 미통과다.
