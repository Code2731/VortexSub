# 모델 없는 Windows 캡처 시작 비교 — T02-02d

무음 새 worker의 `InitializeAudioClient` 대기를 실제 ASR/VAD와 분리해 확인한다. `scripts/probe-capture-startup.ps1`은 기본 console render 선택과 활성 render endpoint별 고정 선택을 비교한다. 각 case는 새 프로세스이며 기존 장치 설정·기본 장치·다른 앱을 변경하지 않는다.

## 실행

```powershell
# 기본 선택 + 활성 render endpoint 모두, 각각 새 worker 2회
.\scripts\probe-capture-startup.ps1 -Offline
# 기본 선택만 10회
.\scripts\probe-capture-startup.ps1 -Offline -Rounds 10 -DeviceId default
# 고정 ID는 아래 목록에서 복사한다.
.\target\release\echosub-capture-windows.exe --list
.\scripts\probe-capture-startup.ps1 -Offline -DeviceId '{0.0.0.00000000}.{device-guid}'
```

기본 release worker와 endpoint probe, C# CaptureSmoke를 빌드한다. `-NoBuild`는 Rust 빌드만 생략한다. `-Rounds`는 1~20, endpoint manifest는 최대 32개다. fixture/모델/runtime 다운로드·음원 재생·PCM 저장은 없다. `hello`의 진단 implementation/capability, model=NotInstalled/native ASR disabled, empty history를 확인한다. 실행 중 다른 앱의 오디오는 격리하지 않는다.

## 제어·보고서

Opening을 최대 12초 관측해 worker의 10초 실패 정책을 확인한다. poll 중 Ping 시간을 측정한다. Running 후 0.3초 상태/수신량을 추가 관측한다. Failed에서 join 대기 중이면 재시작 요청의 INVALID_STATE 거부를 확인한다. 요청 직전에 join이 완료되는 race는 accepted일 수 있으며 기록을 그대로 남긴다.

각 worker에 shutdown을 요청하고 응답·프로세스 종료를 합쳐 5초 기다린다. 대기를 넘으면 harness가 직접 생성하고 handle을 보유한 프로세스만 종료한다. 강제 종료 사용 여부·정리 시간·exit code를 보고한다. 이 fallback은 native API가 취소됨을 뜻하지 않는다.

`benchmarks/results/capture-startup-*/`의 endpoint manifest/report는 Git 제외다. 완료한 case마다 report를 갱신하며 뒤 case가 실패해도 앞의 관측을 보존한다. 모든 case가 완료되고 정상 시작/정상 종료돼야 passed=true다. 실패 뒤에도 나머지 장치를 검사하고 마지막에 exit 1을 반환한다. 모든 기간은 초다.

## 해석

새 프로세스는 OS/드라이버/오디오 엔진의 초기 상태를 보장하지 않는다. 장치별로 묶어 순차 측정하므로 앞선 실행의 영향도 가능하다. owned_playback=false와 accepted_audio_s=0은 시스템의 디지털 무음을 입증하지 않는다. 시작·정리 관측을 발화 품질, 실제 장치 전환, 장시간 게임 공존 수용으로 확대하지 않는다. [Windows 실측](evidence/T02-02d-windows-capture-startup.md)을 따른다.
