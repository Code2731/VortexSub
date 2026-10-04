# 사용자 설정·번역 연결 복구 실행 검증 (2026-10-04)

## 결과

Windows x64에서 60개 검사 항목이 통과했다. 실패는 0개다.
전체 실행은 19.0296초였다. 앱 빌드는 경고 0개, 오류 0개로 통과했다.
Rust worker는 현재 소스에서 기본 기능으로 offline 빌드했다.

| 확인 영역 | 실행 결과 |
| --- | --- |
| 설정 저장 | 저장 버튼으로 모든 저장 항목의 파일 값을 확인했다. |
| 재시작 복원 | 별도 앱 프로세스에서 같은 설정 파일을 읽고 UI 값을 확인했다. |
| 정상 종료 | 저장 버튼을 누르지 않은 변경도 종료 뒤 파일에 반영됐다. |
| 잘못된 설정 | 손상·16 KiB 초과 파일은 기본값과 오류 안내로 복구됐다. |
| 저장 실패 | 저장 경로를 디렉터리로 만들어 실패를 재현했다. 안내를 표시하고 앱을 유지했다. |
| 출력 장치 | 장치 누락 시 기본 장치를 선택했다. 존재하는 장치는 복원했다. 목록 미수신 시 저장 ID를 보존했다. |
| 주소 검사 | 숫자 loopback·포트·지원 경로를 확인했다. 잘못된 주소는 HTTP 요청을 보내지 않았다. |
| 연결 실패 | 실제 닫힌 로컬 포트로 연결 실패를 재현했다. 기존 Ready 연결을 유지했다. |
| 인증·모델 | HTTP 401과 모델/입력 프로필 불일치 안내를 확인했다. 기존 Ready 연결을 유지했다. |
| 시간 초과 | 모의 서버 응답을 10초 지연해 카탈로그의 8초 제한을 재현했다. 기존 Ready 연결을 유지했다. |
| 재시도 | 연결 실패와 시간 초과 뒤 재설정이 성공했다. 동일 worker 프로세스를 유지했다. |

오류 3개 화면과 복구 화면을 Avalonia 렌더로 저장하고 직접 확인했다.
검증 중 UI의 IPv4 검사에서 선행 0을 허용하던 차이를 수정했다.

## 재실행

아래 명령은 검증 전용 진입점이다. 별도 설정 파일과 로컬 모의 서버를 사용한다.
새 모델이나 런타임을 다운로드하지 않는다.

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $PWD '.nuget/packages'
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
cargo build -p echosub-worker --locked --offline
dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore
dotnet run --project apps/EchoSub.Desktop --no-build --no-restore -- --ui-recovery-probe-worker J:/MyProject/VortexSub/target/debug/echosub-worker.exe --ui-recovery-probe-report J:/MyProject/VortexSub/benchmarks/results/ui-recovery-restart-20261004/report.json
```

JSON 보고서, 재시작 보고서, 설정 파일과 화면 4개는 Git 제외
`benchmarks/results/ui-recovery-restart-20261004/`에 보관한다.
보고서에 각 항목의 결과와 경과 시간을 기록한다.
검증용 창, 자식 앱 프로세스, 소유 worker와 모의 서버는 종료 시 정리한다.

## 확인 범위

실제 Avalonia 컨트롤과 Rust IPC, 로컬 HTTP 카탈로그 경로를 실행했다.
개인 설정은 읽거나 변경하지 않았다. 실제 음성·모델 추론과 WASAPI는 실행하지 않았다.
파일 접근 권한 거절은 별도로 재현하지 않았다. 저장 실패는 경로 충돌로 재현했다.
서버 모델 변경, 30분 실행, 키보드 접근성, 다른 운영체제·배율은 미검증이다.
화면은 운영체제 합성 캡처가 아닌 Avalonia 렌더다.
