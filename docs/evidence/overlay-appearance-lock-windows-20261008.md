# 글꼴 화면과 오버레이 잠금

2026-10-08. Windows x64 / 화면 배율 125%. UI-002와 UI-003의 부분 구현이다.

## 변경

메인 자막 탭에 `자막 창 잠금 · 클릭 통과` 옵션을 추가했다.
기본값은 꺼짐이다. 개인 설정에 저장하지 않는다.
메인 화면에서 옵션을 끄면 이동·크기 조절을 다시 사용할 수 있다.
오버레이를 숨겨도 메인 화면의 잠금 해제 옵션은 사용할 수 있다.
Windows 외 OS에서는 이 옵션을 비활성화하고 지원 상태를 표시한다.
트레이 해제와 macOS 구현은 포함하지 않았다.

Windows는 layered/transparent 확장 속성을 적용한다.
잠금 해제는 변경한 입력 속성만 복원한다. 비활성화·도구 창 속성도 유지한다.
근거는 [Microsoft Window Features](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)의 layered window 입력 처리 설명이다.
Avalonia의 CanResize 변경이 확장 속성을 재설정하므로 크기 조절 상태를 먼저 바꾼다.
그다음 입력·비활성화 속성을 적용한다.

큰 글꼴에서 원문 공간이 부족한 문제도 수정했다.
원문 표시 시 최소 창 높이에 원문 공간을 더한다. 실제 줄의 내용과 읽기 기한은 변경하지 않는다.

## 화면 실행

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT='1'
$env:NUGET_PACKAGES=Join-Path $PWD '.nuget/packages'
dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore
apps/EchoSub.Desktop/bin/Debug/net10.0/EchoSub.Desktop.exe --ui-capture-dir benchmarks/results/ui-appearance-20261008-fixed --ui-capture-worker target/debug/echosub-worker.exe
```

실제 실행은 절대 경로를 전달했다. 위 명령은 저장소 루트 기준 재개 명령이다.
화면 저장에는 기존 debug worker가 필요하다. 별도 모델/런타임은 필요하지 않다.
앱 빌드: 경고 0 / 오류 0. 수정 후 화면 저장 실행 종료 코드: 0.
앱·worker 종료 후 실행 프로세스 0개를 확인했다. 실제 음성·모델 추론은 사용하지 않았다.
개인 설정은 저장하지 않았다. 이번 라운드는 자동 테스트를 추가하거나 실행하지 않았다.

| 화면 | 창 높이 DIP | 확장 속성 | 클릭 통과 속성 | 비활성화 속성 |
|---|---:|---|---|---|
| 18 | 220 | 0x8200088 | 꺼짐 | 켜짐 |
| 26 | 220 | 0x8200088 | 꺼짐 | 켜짐 |
| 48 | 339.2 | 0x8200088 | 꺼짐 | 켜짐 |
| 48 잠금 | 339.2 | 0x82800A8 | 켜짐 | 켜짐 |
| 48 해제 | 339.2 | 0x8200088 | 꺼짐 | 켜짐 |

18·26·48 화면과 메인 옵션 화면을 직접 확인했다.
48에서는 한국어 샘플 번역과 영어/일본어 원문이 표시됐다.
최초 실행에서 원문 공간 부족과 잠금 속성 재설정을 발견했다.
수정 후 별도 폴더에서 위 결과를 얻었다. 최초 결과는 덮어쓰지 않았다.
원본 이미지·capture.json은 `benchmarks/results/ui-appearance-20261008-fixed/`에 있다.
이 경로는 Git 제외다. 이미지는 Avalonia 렌더 결과이며 물리 화면 캡처가 아니다.

## 남은 범위

다른 프로세스의 창으로 실제 마우스 클릭이 전달되는지는 미검증이다.
메인 체크박스의 물리 입력·잠금 상태의 합성기 화면·게임 전체 화면도 미검증이다.
창 속성 전환 성공을 UI-003의 실제 클릭 통과 통과로 해석하지 않는다.
설정 재시작 복원과 실제 키보드·화면 읽기 검증도 남아 있다.
다음은 물리 클릭 통과·메인 해제 확인과 DPI/모니터 복구다.
자연 음성 30분 검증은 미완료다. 캡처 원인 조사는 계속 보류한다.
브랜치 master, 기준 HEAD `15f1db0`. 이번 구현·문서는 미커밋이며 다른 PC에 전달되지 않았다.
