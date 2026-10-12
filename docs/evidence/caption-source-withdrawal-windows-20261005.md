# 원문 철회 후 자막 처리

날짜: 2026-10-05. 로드맵 1단계의 수정 대기 정책 후속이다.

## 변경

`CaptionLines`의 구간 무효화에서 보이는 줄 제거를 구분했다.
`CaptionDeck`이 원문 guard 불일치·실패·폐기를 명시적으로 전달하면
해당 구간의 표시 줄, 미표시 입력과 대기를 취소한다.
해당 구간의 소비 위치도 초기화한다. 새 번역이 도착하면 처음부터 표시한다.
다른 구간의 줄 위치와 읽기 기한은 유지한다.
표시 줄 제거는 `InvalidatedVisible / SourceInvalidated`로 진단한다.

일반 번역 대기 카드의 빈 본문은 보이는 줄을 지우지 않는다.
부분 원문 확장과 번역 대기를 명시적 원문 철회와 구분한다.
원문 의미를 새 모델로 분류하지 않는다. 기존 source guard 일치 판정을 사용한다.
따라서 어휘가 바뀐 전사 수정도 guard 불일치이면 철회로 처리될 수 있다.
일반 번역 표현 수정의 읽기 보호와 줄당 2.5초 정책은 유지한다.

## 실행

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $PWD '.nuget/packages'
dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/capture-model-ui.ps1 -NoBuild -Revisions -OutputDirectory benchmarks/results/ui-model-withdrawal-final-20261005
```

Windows x64, .NET 10, 125% 배율, 420 DIP 오버레이를 사용한다.
실제 Hy-MT2 번역과 모의 원문 revision을 사용한다. 입력 일정은
[이전 수정 관찰](ui-model-revisions-windows-20261005.md)과 같다.
앱 빌드는 경고 0개, 오류 0개였다.
중간 구현에서 빈 번역 대기 카드도 같은 취소 경로를 쓰는 점을 확인했다.
최종 구현은 명시적 철회만 보이는 줄을 지우도록 분리했다.
중간 실행 `ui-model-withdrawal-20261005`는 최종 정책의 근거로 사용하지 않는다.

## 관찰 결과

최종 실행 종료 코드 0. 관찰 36.059초, 표본 574개, 화면 13개였다.
긴 번역은 6개 줄로 꼬리까지 표시됐다. 줄 위치는 36.8·84로 유지됐다.
부분 확장은 15.22초의 위 줄을 유지하고 16.77초에 아래 줄을 추가했다.
최종 원문 revision 5는 17.27초에 관측됐고 그 표본에서 기존 두 줄이 지워졌다.
최종 번역은 17.39초에 관측됐고 같은 표본에서 두 줄에 표시됐다.
이전 실행의 번역 완료 후 1.79초 읽기 대기가 이번 관찰에서는 발생하지 않았다.
같은 표본에서 표시됐다는 뜻이며 물리 화면 지연 0초를 의미하지 않는다.
표시가 비어 있던 관측 구간은 약 0.12초였다. 추론 시간의 개선으로 해석하지 않는다.
`revision-07`~`10`을 직접 확인했다. 서버와 임시 인증키가 남지 않은 것도 확인했다.
단일 입력·단일 실행 결과다. 자연 음성이나 모든 수정 유형에 일반화하지 않는다.

## 한계

원본은 Git 제외 `benchmarks/results/ui-model-withdrawal-final-20261005/`다.
원문 철회부터 수정 번역 도착까지 자막이 비는 시간이 생길 수 있다.
원문과 맞지 않는 이전 자막을 읽기 기한까지 유지하지 않는 정책이다.
자연 음성의 전사 변동·철회 빈도, 다른 구간 혼합, 전체 회귀와 30분 사용은 남아 있다.
실제 음성·VAD·ASR과 운영체제 화면 합성은 이 실행에 포함하지 않는다.
자동 테스트는 이번 라운드에서 추가·실행하지 않았다.
이번 소스와 문서는 미커밋이다. 다른 PC에는 아직 전달되지 않았다.
