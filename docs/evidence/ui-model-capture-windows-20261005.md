# 실제 번역 모델 연결 화면 확인

날짜: 2026-10-05. 환경: Windows x64, .NET 10, 화면 배율 125%.

## 변경

`UiCapture`에 실제 HTTP 번역 연결 모드를 추가했다.
음성 입력은 `mock_segment` 문장 입력으로 대체한다. 번역 결과는 주입하지 않는다.
실제 Rust 번역 owner, 모델 서버, IPC 기록, 자막 전달과 오버레이를 사용한다.
`scripts/capture-model-ui.ps1`은 설치된 Hy-MT2 모델을 확인하고 서버를 직접 실행한다.
서버는 숫자 loopback 주소와 임시 포트를 사용한다. 임시 인증키를 사용한다.
종료 시 자신이 실행한 서버와 인증키 파일을 정리한다. 다른 서버는 종료하지 않는다.
모델 다운로드와 개인 설정 저장은 수행하지 않는다.

## 실행 결과

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $PWD '.nuget/packages'
dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/capture-model-ui.ps1 -NoBuild -OutputDirectory benchmarks/results/ui-model-20261005
```

- 앱 빌드: 경고 0개, 오류 0개.
- 실제 모델 화면 실행: 종료 코드 0.
- 모델: `hy-mt2-1.8b-q4_k_m`, 입력 프로필: `hymt2-greedy`.
- 모델 SHA-256: 매니페스트와 일치. 런타임: 설치된 b11146.
- 모델 서버 준비: 14.325초. 추론 지연과 별도다.
- 문장 입력부터 앱 기록 수신: 0.945초. 폴링·IPC 시간이 포함된다.
- 서버 측 입력 처리: 0.535초. 출력 생성: 0.304초. 합계: 0.839초.
- 입력: `Please keep the subtitles on screen long enough to read them.`
- 출력: `자막이 읽을 수 있을 정도로 화면에 오래 표시되도록 하세요.`
- 번역 연결 설정, 실행 중 메인 화면, 오버레이, 기록 화면을 직접 확인했다.
  한국어 번역이 표시됐고 원문 표시 옵션은 꺼져 있었다.
- 실행 후 서버 프로세스와 임시 인증키 파일이 남지 않았다.

원본은 Git 제외 `benchmarks/results/ui-model-20261005/`에 있다.
`capture.json`은 입력·출력·전달 시간을 기록한다. `model.json`은 파일 해시를 기록한다.
화면은 `10-model-ready.png`, `11-model-overlay.png`, `12-model-main.png`, `04-history.png`다.

## 범위와 남은 작업

한 문장의 실제 번역과 화면 전달만 확인했다. 속도 분포나 번역 품질 전체를 평가하지 않았다.
음성 입력·VAD·ASR·사용자가 들은 시점부터의 지연은 측정하지 않았다.
모델을 불러온 뒤 첫 번역이다. 이번 수치를 이전 음성 지연 수치와 직접 비교하지 않는다.
화면 원본은 Avalonia 렌더다. 운영체제의 화면 합성 결과는 아니다.
두 줄 연속 전환, 30분 사용, 실제 키 입력, 화면 읽기 프로그램과 macOS는 남아 있다.
이번 라운드에서는 자동 테스트를 추가하거나 실행하지 않았다.

다른 PC는 .NET 캐시, 기본 worker와 매니페스트의 모델·b11146 런타임을 먼저 준비한다.
새 다운로드에는 동의 규칙을 적용한다. Git만으로 모델과 화면 원본이 전달되지 않는다.
이번 코드와 문서는 미커밋이다. 다른 PC에는 아직 전달되지 않았다.
