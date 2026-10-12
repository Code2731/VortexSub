# 자연 음성 세션 시작 시도

2026-10-06. Windows 11 10.0.26200.0 / 기존 CUDA 환경. 로드맵 1단계.

## 변경

실제 앱의 연결·번역 설정·세션 시작·종료를 수행하는 관찰 진입점을 추가했다.
`-NaturalSessionReport <경로> -NaturalSessionSeconds 1800`으로 실행한다.
부분 전사·부분 번역·마침표 갱신 묶기를 켠다. 영어, 폭 760, 불투명도 0.75,
원문 숨김, `hymt2-greedy`를 적용한다. 개인 설정은 읽거나 저장하지 않는다.
관찰 시간은 Running 이후에만 계산한다. 기록 개수와 프로세스 메모리를 5초마다 저장한다.
기록 개수는 보관 중인 기록이다. 전체 입력·누락 개수는 아니다.
재생 제어·준비 발화·정답 품질 평가·물리 렌더 검증은 이 진입점에 포함하지 않는다.

## 실행과 결과

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT='1'
$env:NUGET_PACKAGES=Join-Path $PWD '.nuget/packages'
dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-live.ps1 -NoBuild -TranslationModel hymt2 -AsrBackend cuda -FastPartials -CaptionTiming -NaturalSessionReport benchmarks/results/natural-session-20261006/observation.json -NaturalSessionSeconds 1800
```

새 진입점으로 실행하기 전 빌드: 경고 0 / 오류 0.
번역 카탈로그는 01:08:28에 Ready였다. 실제 캡처는 01:08:29에 Opening이었다.
01:08:39에 `CAPTURE_START_TIMEOUT`으로 Failed가 됐다. 수신 음성은 0초였다.
세션은 Error로 바뀌었다. 영상은 재생하지 않았다. 자연 발화·전사·번역은 관찰하지 못했다.
관찰 결과는 `Incomplete`, 측정 시간 0초, 요청 시간 1,800초다. 실행 종료 코드는 1이다.
01:09:04 worker는 강제 정리됐다. 앱 종료와 런처 소유 서버 정리를 확인했다.
종료 후 해당 앱·worker·서버 이름의 실행 프로세스는 없었다.

Git 제외 근거:

- `benchmarks/results/natural-session-20261006/observation.json`
- `logs/desktop-20261006-010826-38416.log`
- `logs/run-20261006-010826-38416.log`
- `logs/caption-timing-20261006-010826-38416.jsonl`
- `logs/translation-20261006-010824-3804.config.json`

첫 빌드에는 상태 변수 오류가 있었다. 빌드 실패 뒤 이전 바이너리를 잘못 실행했다.
해당 실행은 측정에서 제외했다. 일반 닫기로 끝나지 않아 소유 앱을 강제 종료했다.
그다음 빌드를 통과한 새 바이너리로 위 시작 시도를 수행했다.

시도 후 Error 조기 종료, 캡처 오류·수신 음성·실행 시간 기록을 보강했다.
최종 앱 빌드도 경고 0 / 오류 0이었다. 이 후속 보강의 실제 캡처 재실행은 하지 않았다.
두 런처의 PowerShell 구문 분석은 통과했다. 최종 실행 프로세스 재확인 결과도 0개였다.
전체 회귀 검사·30분 관찰·재생 동기화·장치 선택·준비 세션은 미검증이다.
백신/캡처 시작 원인 조사는 사용자 지시에 따라 보류한다.
이번 변경은 미커밋이며 다른 PC에 전달되지 않았다.
