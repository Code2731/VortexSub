# HIS-002 원문+번역 SRT — Windows (2026-10-12)

## 변경과 저장 정책

worker의 `export_history`에 `srt_bilingual`을 추가했다. 스키마와 C# 기록 탭도 연결했다.
새 capability `history_export_bilingual`은 UUID 세션 제어를 지원할 때 true다.
기존 worker가 capability를 보내지 않으면 새 버튼을 비활성화한다.
기존 `txt`와 원문 `srt` 형식은 유지했다. 두 SRT 형식은 시각 순으로 cue를 정렬한다.

확정 원문·적용 revision 일치·유효한 시간 구간을 먼저 검사한다.
같은 기록의 Done 번역을 원문 아래에 붙인다. request ID는 양수이고 preview는 false여야 한다.
번역 None/Pending/Failed/Skipped/Bypassed·부분 번역·빈 번역·유효하지 않은 request는 원문만 저장한다.
적용 revision이 원문 revision과 다른 기록은 cue에서 제외한다.
누락 상태 설명·가짜 번역·별도 시각 cue는 만들지 않는다.
Unicode·줄바꿈 정리·session audio 시간·순번·UTF-8와 기존 원자적 파일 게시를 유지한다.

UI는 `.srt` 확장자와 `-bilingual` 기본 이름을 사용한다.
일시정지/종료 후 저장한다. 연결 capability·세션 선택·준비 상태에 따라 활성화한다.
접근성 이름과 키보드 순서, 비활성화 사유를 추가했다.
오버레이 배정·읽기 시간·추론 정책은 변경하지 않았다.

## 자동 검증

저장소 루트의 PowerShell에서 실행했다.

```powershell
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
$env:ECHOSUB_OFFLINE = '1'
& scripts/check.ps1
```

최종 종료 코드 0. 로그: `benchmarks/results/bilingual-check-verified-20261012.log`.
Windows 11 Home x64 / Ryzen 7 5800X / RTX 3080 / .NET SDK 10.0.102.
오프라인 Rust 검사·workspace 빌드·.NET 앱/검사/번역 probe 빌드·C#↔Rust IPC를 실행했다.
Rust 검사 합계 175개 PASS. worker 38개, protocol 24개를 포함한다.
형식 검사와 모든 빌드가 통과했다. 최종 빌드 경고 0 / 오류 0.
C# IPC는 64 요청·Unicode·typed history·shutdown·worker death의 기존 검사와 새 저장 검사가 통과했다.

Rust export 검사에서 Done 번역·Unicode/CRLF·미완료/실패 fallback·preview·request 0·revision 불일치·시각 정렬을 확인했다.
Rust protocol과 C# IPC에서 mock 원문→완료 번역→pause→실제 파일 저장을 실행했다.
번역 본문이 파일에 붙고 충돌 시 기존 바이트가 유지됨을 확인했다.
원문 SRT·명시적 덮어쓰기·임시 파일 정리의 기존 검사도 통과했다.
초기 새 protocol 검사는 shutdown 요청 없이 worker 종료를 기다려 실패했다.
검사에 shutdown을 추가하고 전체 검사를 재실행했다. 초기 로그는 별도로 보존했다.

UI 명령:

```powershell
dotnet apps/EchoSub.Desktop/bin/Debug/net10.0/EchoSub.Desktop.dll --ui-usability-probe-worker target/debug/echosub-worker.exe --ui-usability-probe-report benchmarks/results/bilingual-ui-20261012/report.json
```

실제 Avalonia 컨트롤·Rust mock 세션 IPC의 44개 검사가 PASS다.
새 버튼 활성화·키보드 초점·접근성 이름을 확인했다. 저장 PNG는 4개다.
`04-bilingual-export-ready.png`를 직접 열었다. 저장 버튼 4개와 안내가 잘리지 않고 보였다.
물리 키 입력이나 OS 합성기의 화면 캡처는 아니다.

## 제한과 인계

모델 다운로드·실제 음성·새 추론은 실행하지 않았다. 개인 설정은 변경하지 않았다.
실제 OS 저장 대화상자 조작·취소와 macOS 파일 게시·실제 자막 플레이어는 미검증이다.
옛 worker 실행 파일을 직접 연결한 검사는 하지 않았다. capability 누락 처리는 코드에서 확인했다.
오버레이 저장 화면 회귀와 실제 30분은 이번에 실행하지 않았다. caption 정책은 변경하지 않았다.
HIS-002의 Windows 구현과 자동 파일/IPC/UI 검사를 완료했다. 양 OS 수용 완료는 아니다.
다음 구현은 UI-004 DPI 변경·모니터 분리 시 자동 위치 복구다.
캡처 원인 조사와 Laya 실험은 보류를 유지했다. 사용자 수동 확인은 요청하지 않았다.
브랜치 `master`, HEAD `15f1db0`. 이번 코드·검사·문서는 미커밋이며 다른 PC에 전달되지 않았다.
다른 PC는 Rust/.NET 10·복원한 패키지를 준비한다. 기본 check에는 새 모델이 필요하지 않다.
모델·런타임 manifest와 로컬 설정은 별도다. 로그·PNG·모델·캐시·전체 결과는 Git 제외다.
