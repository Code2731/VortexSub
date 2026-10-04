# 표시 조립기 고도화 — Windows, 2026-10-03

## 재현과 구현

`문 앞에서 기다려. 왼쪽 길로 가세요.`를
`문 앞에서 기다려. 오른쪽 길로 가세요.`로 수정하면 기존 표시기는 읽기 보호 후
앞 문장까지 다시 표시했다. 생산 fixture에서 이 반복을 재현하고 수정 전 실패를 확인했다.
실패한 workspace 검사 프로세스가 빌드 파일을 잡은 문제는 해당 프로세스를 종료하고
정상 빌드/검사로 복구했다.

`CaptionAssembly.ReusablePrefix`로 표시 접두사의 재사용 판단을 분리했다.
같은 알려진 표시 단위에서 글자까지 동일한 접두사를 문장 끝 구두점까지 재사용한다.
읽는 줄은 먼저 최소 2.5초 유지하고, 교정할 때 동일한 앞 문장을 건너뛰어 변경된
문장부터 표시한다. 단순 suffix 추가는 기존 append 경로를 유지한다.

첫 문장이 달라지거나 final이 짧아져 suffix가 없어지면 전체 교정을 표시한다.
product/session/epoch/segment/unit 변경과 식별자 없음에는 재사용하지 않는다.
비교 상한은 UTF-16 16,384자이며 두 줄·최신 입력 하나를 유지한다. 소비한 target
위치를 보존하므로 동일 final로 생략한 앞 문장이 다시 나오지 않는다.

소수점·말줄임표·알려진 영어 약어를 문장 경계로 쓰지 않고 결합 문자/variation
selector/ZWJ 경계를 나누지 않는다. 제한된 표기 규칙이며 문법·의미 확정기가 아니다.
대소문자/공백/동의어를 같다고 정규화하지 않는다. 표현이 달라진 문장과 부정·조건·
숫자·방향 변경은 공통 몇 글자만 잘라내지 않고 변경된 문장 전체를 표시한다.
이미 표시됐다는 것은 읽기 시간을 제공했다는 뜻이며 시선/읽기 완료 추적은 아니다.
유튜브 내부 구현을 복제하거나 확인한 결과도 아니다.

## 최종 검증

- 줄 상태 80 assertions: 동일 앞 문장 재표시 제거, 읽기 보호, 동일 final 재생 방지,
  방향/부정/조건/숫자 교정, shortening, 소수점/약어/말줄임표, Unicode,
  단위/식별자 격리와 비교 상한을 포함한다.
- 실제 Avalonia 렌더 fixture 573 checks, PNG 36개, 픽셀 검사 97개 통과.
  교정 대기 중 상/하 글자 픽셀·geometry를 비교했다. 읽기 후
  `오른쪽 길로 가세요.`만 표시된 이미지도 직접 확인했다.
- 전회 native ASR→Hy-MT2 방향 정정 12회 기록의 원문/번역 갱신을 생산
  Deck→OverlayWindow에 재생했다. 상태 4351개, PNG 176개, 픽셀 검사 953개 통과.
  기존 정정/읽기 유지/final 완료를 회귀 확인했다. 이 기록 자체가 새 앞 문장
  생략 사례를 모두 포함하지 않으므로 별도 fixture와 함께 검사한다.
- 최종 전체 check 통과: Rust tests/build, C# build/IPC, 표시 상태 41/80/34 assertions.

재생 검사는 이미 표시한 동일 prefix와 변경된 remainder의 조합도 확인한다.
전체 final이 한 이미지에 동시에 있어야 한다고 요구하지 않는다. Windows 가상
시계·저장 렌더 검사이며 live IPC/MainWindow, 실제 음성→물리 화면 지연,
compositor 연속 프레임, 자연 음성/30분/게임과 macOS는 별도 미확인이다.
추론 속도 개선과 표현이 다른 문장의 의미 동등성 판별은 이번 목표에 포함하지 않았다.

## 명령과 증거

```powershell
$env:ECHOSUB_OFFLINE='1'; & scripts/check.ps1
dotnet run --project apps/EchoSub.Desktop --no-build --no-restore -- --caption-line-probe-report J:/MyProject/VortexSub/benchmarks/results/caption-assembly-release-render-20261003/report.json
models/tabby/venv/Scripts/python.exe -X utf8 scripts/check-caption-render.py benchmarks/results/caption-assembly-release-render-20261003/report.json
```

실측 재생 명령은 [연결 증거](caption-repair-replay-windows-20261003.md)와 같으며
출력만 `caption-assembly-release-{base|small}-20261003/`로 바꾼다.
Git 제외 `benchmarks/results/caption-assembly-release-render-20261003/`와 각 release
재생 폴더에 report/PNG/pixel-report, `caption-assembly-final-validation-20261003.log`에
최종 전체 검사 로그를 보존했다. 모델/runtime 다운로드와 기본 ASR/profile 변경은 없다.
