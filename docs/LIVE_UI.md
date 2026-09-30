# Windows 실제 원문 진단 UI

## 실행

저장소 루트에서 `./run.cmd -Live -Offline` 또는
`./scripts/run.ps1 -Live -Offline`을 실행한다. 일반 실행은 MOCK이다.
Whisper base, Silero v6.0, ONNX Runtime CPU 파일은 기존 manifest 경로에
준비되어 있어야 한다. 런처는 다운로드하지 않으며 worker가 해시를 확인한다.
첫 native CPU 빌드에는 CMake/LLVM/MSVC가 필요하다.
기존 바이너리를 사용하려면 `-NoBuild`를 추가한다. 이때 장치 열거 probe가
없으면 기본 출력 장치만 선택할 수 있다.

1. 모델 상태가 Ready가 되면 출력 장치와 원문 언어(en/ja/ko)를 선택한다.
2. **캡처 시작**을 누르고 선택한 출력 장치에서 음성을 재생한다.
3. 최근 100개 구간과 **원문 오버레이 표시**로 확정 원문을 확인한다.
4. 장치/언어 변경 전 **캡처 정지**를 누른다. 실패 시 native phase와
   시작 대기 시간을 확인하고 필요하면 **Worker 종료 → 다시 연결**한다.

## 표시·수명 계약

0.5초마다 상태를 조회하고 history 버전/이벤트 복구 요구가 바뀌면
페이지 snapshot을 읽는다. 페이지 조회 뒤 상태를 다시 읽어 현재 session,
ASR epoch, Final 상태와 적용된 source revision이 일치하는 최신 원문만 표시한다.
동일 결과를 재조회해도 5초 표시 시간을 연장하지 않는다.
정지·실패·통신 오류·연결 해제 시 현재 오버레이 원문을 지운다.
history는 worker의 유한 저장소를 사용하며 UI가 텍스트를 자동 저장하지 않는다.

캡처/VAD owner 정리가 끝나기 전 시작 버튼은 비활성화된다.
worker 종료가 5초를 넘으면 소유 프로세스를 종료하고 그 사실을 표시한다.
native 반환 시간과 캡처 Stop 응답 시간은 서로 다르다.

진단의 숫자 session/epoch namespace를 유지한다. 제품 session/Pause,
partial, 번역, overlap 텍스트 병합은 후속이다. 간헐적 Initialize 대기와
자연 음성 품질·게임 포커스·macOS 수용은 해결/검증되지 않았다.

## 이번 확인 범위 (T02-02e, 2026-10-01)

Windows 저장소 검사(Rust 91개, 포맷·빌드, C# 빌드·IPC smoke) 통과.
Windows PowerShell 5.1 `-Live -NoBuild -Offline -NoPause` 실행에서
live 메인 창 생성과 worker 연결 로그를 확인했다. 확인용 앱은 프로세스
종료로 정리했으며 정상 창 닫기 검증으로 간주하지 않는다.

UI 자동화가 다른 실행 세션을 보고 있어 실제 버튼 클릭·원문 렌더링·5초 만료·
실패 버튼·게임 위 표시를 확인하지 못했다. 이전 native pipeline probe의
성공을 UI 성공으로 확대하지 않는다. 다음 수동 확인은 위 실행 순서로
Start/Stop/재시작, 언어/장치 선택, 새 원문/만료, Worker 종료/재연결을 관찰한다.
