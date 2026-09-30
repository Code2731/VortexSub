# T02-02c Windows 캡처 시작 대기·발화 검증 보완

2026-10-01. Windows NT 10.0 build 26200, AMD64 Family 25 Model 33/논리 16개, Rust 1.90.0 MSVC/.NET 10.0.102. 기본 render endpoint는 GSX 1000 Main Audio, mix 48 kHz stereo float32/mask 0x3다. 기존 Whisper base/Silero v6.0/ORT 1.22.0 CPU 자산을 사용했으며 새 다운로드는 없다.

## 변경과 재현

```powershell
.\scripts\probe-worker-capture.ps1 -Offline -Rounds 20
.\scripts\probe-worker-live-asr.ps1 -Offline
$env:ECHOSUB_OFFLINE='1'
$env:ECHOSUB_NUGET_SOURCE="$env:USERPROFILE/.nuget/packages"
.\scripts\check.ps1
```

- Ready 없는 Opening은 10초 이후 worker poll에서 Failed/`CAPTURE_START_TIMEOUT`으로 전환한다. native 반환을 기다리지 않고 Stop을 요청하며 실제 owner join 전에는 재시작을 거부한다.
- `opening_elapsed_s`, `startup_deadline_s`, `failure_native_phase`, 최대 16개 단계 관측을 추가했다. 첫 실패는 후속 오류/Stop/owner 종료로 덮어쓰지 않는다. 관측 시간은 poll 기준이며 개별 native API의 정밀 실행 시간이 아니다.
- capture owner/probe COM을 STA로 변경했다. [Initialize 공식 문서](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize)의 STA 안내는 Windows 8에 명시돼 있다. 현재 Windows의 지연 원인을 COM apartment로 확정하지 않는다.
- 같은 문서가 요구하는 shared/event-driven 초기화의 버퍼 시간·주기 0을 적용했다. 이전 버퍼 시간은 0.1초였다. 계약 위반을 수정했으나 간헐적 지연과의 인과는 별도 검증 대상이다.
- live 두 final 검증은 Running 뒤 whole fixture를 시작하고 앞 0.5초/뒤 1초 실제 무음을 포함한다. 다른 시스템 오디오는 격리하지 않았다. duration·8초 chunk cap 여부를 기록하며 ASR 문구 정확도를 수용하지 않는다.
- failed report 및 부모 EOF 이전 checkpoint를 보존한다. PCM/전사문·전체 report는 Git 제외다.

## 실패와 중간 관측

| 보고서 (`benchmarks/results/` 아래) | 결과 |
|---|---|
| `worker-capture-20261001-004010/report.json` | MTA/버퍼 0.1초: Start/Stop 20회 PASS. 첫 Opening 2.116177초, 이후 약 0.014~0.016초. Start 직후 Stop·활성 shutdown/EOF 통과 |
| `worker-capture-20261001-004304/report.json` | 같은 설정 20회 PASS. 첫 Opening 3.384889초, Initialize 단계 0.014953초부터 Capturing 3.384885초까지 관측. 정밀 API 시간은 아님 |
| `worker-live-asr-cpu-20261001-004630/report.json` | MTA/버퍼 0.1초: 부모 EOF용 새 worker에서 Initialize 대기, 10.006040초에 `CAPTURE_START_TIMEOUT`. PCM 0, join 미완료를 보고 |
| `worker-live-asr-cpu-20261001-004852/report.json` | STA/버퍼 0.1초: live 회귀 PASS. final 2회, 취소/재시작 3회, 활성 shutdown/EOF 통과 |
| `worker-live-asr-cpu-20261001-004957/report.json` | STA/버퍼 0.1초: EOF용 새 worker Initialize 대기 재발, 10.014578초에 실패. checkpoint final 2회·취소/재시작 3회·활성 shutdown은 통과. STA만으로 해결되지 않음 |
| `worker-live-asr-cpu-20261001-005234/report.json` | STA/버퍼 0: live 회귀 PASS. final 범위 1.792/1.824초, cap 도달 0. Stop 응답 최대 0.000652초, capture/VAD join 확인 0.048601초, 이후 full 반환 확인 0.396611초 |
| `worker-live-asr-cpu-20261001-005356/report.json` | STA/버퍼 0: 다음 실행의 EOF용 새 worker Initialize 대기 재발, 10.010085초에 실패. 초기화 인수 수정 뒤에도 무음 새 프로세스 시작 안정성 미통과 |
| `worker-capture-20261001-005657/report.json` | 최종 STA/버퍼 0: 재생 중 Start/Stop 20회·즉시 Stop·활성 shutdown/EOF PASS. PCM 40.320초/4,060 packets. 첫 Opening 3.160569초, 이후 0.014138~0.030090초. Stop 응답 최대 0.000247초·join 확인 0.032272초 |

실패한 raw EOF 프로세스는 harness가 보유한 process handle로 종료/회수한다. 시간 초과 정책은 native API의 취소 또는 10초 이내 thread 종료를 보장하지 않는다. 실제 timeout 발생 시 PCM은 0이며 Failed와 pending join을 구분해 보고했다. 이 장치에서 무음 새 프로세스 시작의 Initialize 대기는 여전히 재현된다. 드라이버/오디오 엔진/COM 중 근본 원인은 미확정이며 시작 안정성 gate를 PASS로 올리지 않는다.

한 장치·합성 영어 검증이며 자연/일본어/한국어 live 품질, 게임/장기 부하, 실제 장치 전환·분리, CUDA/Mac은 미검증이다. 제품 session/Pause·원문 UI는 후속이며 품질 gate는 false다. 다음은 모델 추론과 분리한 무음 cold-start probe·다른 render 장치 비교와 제품 UI의 오류/소유 worker 복구 계약이다.

## 저장소 회귀

최종 `scripts/check.ps1` PASS: Rust fmt/91개 시험/workspace build, C# Desktop·ProtocolSmoke·TranslationProbe 빌드(경고·오류 0), C#↔Rust Unicode/동시 요청·history/종료 smoke 통과. native CPU/VAD release build도 통과했다. timeout 경계/Ready 우선·terminal 메타데이터/첫 실패 보존은 unit test로, 신규 wire 필드는 Rust protocol과 C# smoke로 확인했다. 실제 API timeout은 위 failed live report로 확인하며 mock 시험을 장치 안정성 근거로 확대하지 않는다.
