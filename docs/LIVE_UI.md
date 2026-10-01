# Windows 실제 원문 진단 UI
## 실행

저장소 루트에서 `./run.cmd -Live -Offline` 또는
`./scripts/run.ps1 -Live -Offline`을 실행한다. 일반 실행은 MOCK이다.
Whisper base, Silero v6.0, ONNX Runtime CPU 파일은 기존 manifest 경로에
준비되어 있어야 한다. 런처는 다운로드하지 않으며 worker가 해시를 확인한다.
첫 native CPU 빌드에는 CMake/LLVM/MSVC가 필요하다.
기존 바이너리를 사용하려면 `-NoBuild`를 추가한다. 이때 장치 열거 probe가
없으면 기본 출력 장치만 선택할 수 있다.

CUDA 전사 선택 실행은 `run-live-cuda.bat` 더블클릭 또는
`./run-live.bat -AsrBackend cuda`다. 기본 `run-live.bat`은 CPU 전사다.
CUDA worker는 별도 `target/model-probe-cuda/`에 빌드하며 설치된 NVIDIA
GPU/CUDA Toolkit이 필요하다. 이미 빌드한 경우 `-NoBuild`로 재사용한다.
CUDA 빌드는 `nvidia-smi`의 compute capability로 현재 GPU 대상을 선택한다.
직접 지정한 `CMAKE_CUDA_ARCHITECTURES`는 유지한다(예: RTX 3080은 `86`).
전체 빌드 출력은 `logs/native-build-날짜-프로세스ID.log`에 저장하며,
빌드 실패 메시지에서 해당 로그 경로를 확인할 수 있다.
새 자막이 멈추는 문제를 조사할 때는 앱의 자막 지연 기록 옵션을 켠다.
지연 로그에는 worker 상태(5초 간격)와 기록 조회/화면 적용 identity가 남으며,
desktop 로그에는 세션·캡처 상태 전환과 캡처 오류 코드가 남는다.
선택한 backend 실패는 오류로 표시하며 CPU로 자동 전환하지 않는다.
Whisper와 Qwen이 같은 GPU를 사용한다. ASR 단독 파일 속도 개선을
게임·번역 동시 실행이나 화면 지연 개선으로 확대하지 않는다.
[CPU/CUDA 파일 비교](evidence/partial-backends-windows-20261001.md).

빠른 부분 전사 비교는 **`run-live-cuda-fast.bat`** 더블클릭 또는
`./run-live-cuda.bat -FastPartials`로 실행한다. 세션 시작 전에
**안정된 부분 먼저 번역 · 임시 결과**도 켜야 조기 번역이 나온다.
`-FastPartials`는 적응형 스케줄을 선택한다(2026-10-02 갱신).
첫 부분 요청은 0.8초로 복구했다. VAD는 약 0.256초마다 후보를 보내며,
worker는 전사 결과에 따라 새 오디오 0.256~1.024초를 기다려 실제 요청한다.
빈 결과는 0.512초, 첫 원문 재확인은 0.256초, 안정 prefix가 늘면 0.512초,
동일 원문/안정 prefix 반복은 최대 1.024초다. decode 비용도 간격에 반영한다.
기본 모드는 기존 약 0.992초 간격이다. 두 전사 일치·미완성 조각·확정 우선·
번역/읽기 규칙은 유지한다. [정책·파일 비교](ADAPTIVE_PARTIALS.md).
CUDA 전용이며 기본 꺼짐이다. 미완성 정보의 조기 노출과 GPU 비용이
늘 수 있다. [실제 ASR→Qwen 파일 측정](evidence/paced-translation-windows-20261001.md).

현재 draft 수정은 0.25초 제한/0.1초 표시 timer를 사용하고 이전 읽기
카드 보호는 유지한다. 앱의 **자막 지연 기록** 옵션을 켜면 텍스트 없이
수신→카드 반영 metadata 로그를 `logs/`에 남긴다. 실행 중 전환 가능하며
저장 경로가 표시된다. 기본 꺼짐이다. [규칙·요약 명령](CAPTION_READING.md).

1. 모델 상태가 Ready가 되면 출력 장치와 원문 언어(en/ja/ko)를 선택한다.
2. **세션 시작**을 누르고 선택한 출력 장치에서 음성을 재생한다.
3. 최근 100개 구간과 **원문 오버레이 표시**로 확정 원문을 확인한다.
4. 장치/언어 변경 전 **세션 종료**를 누른다. 실패 시 native phase와
   시작 대기 시간을 확인하고 필요하면 **Worker 종료 → 다시 연결**한다.
5. 일시정지/종료 후 정리가 끝나면 history 아래에서 세션을 선택해
   **TXT 저장** 또는 **원문 SRT 저장**한다. [저장 형식·제약](HISTORY_EXPORT.md).
6. 불필요한 메모리 기록은 **선택 세션 기록 삭제** 후 확인 창에서 삭제한다.
   저장한 파일은 유지되며 선택한 세션의 메모리 기록은 복구할 수 없다.

## 표시·수명 계약

결과 이벤트 도착 시 0.03초 동안 통지를 모아 상태를 조회한다. 별도로
0.5초 상태 조회를 유지하고 history 버전/이벤트 복구 요구가 바뀌면
페이지 snapshot을 읽는다. 페이지 조회 뒤 상태를 다시 읽어 현재 session,
ASR epoch, Final 상태와 적용된 source revision이 일치하는 최신 원문만 표시한다.
현재 라이브 화면은 [두 카드 읽기 정책](CAPTION_READING.md)을 사용한다.
동일 결과를 재조회해도 4~10초 표시 시간을 연장하지 않는다.
정지·실패·통신 오류·연결 해제 시 현재 오버레이 원문을 지운다.
history는 worker의 유한 저장소를 사용하며 UI가 텍스트를 자동 저장하지 않는다.

상태 조회는 버튼을 잠그지 않고 한 번에 하나만 실행한다. 사용자 명령이
들어오면 진행 중인 자동/수동 상태 조회의 로컬 대기를 취소하고, 같은
소유권 gate에서 명령을 실행한다. 조회 tick은 대기열에 쌓지 않는다.
Start/Stop 응답 뒤에는 수락 상태를 표시하고 다음 tick에서 실제 상태를 읽는다.
조회 취소는 이미 worker에 전달한 요청을 되돌리는 동작이 아니다.

정지/worker 종료 클릭 시 원문을 먼저 지우며 취소된 조회 결과는 화면에
적용하지 않는다. 카드 만료는 IPC 완료와 무관하게 0.5초 UI tick에서 확인한다.
따라서 UI 스레드가 정상적으로 진행될 때 만료 확인에 최대 한 tick의 지연이
있을 수 있다. 사용자 명령 자체의 응답 대기와 native 정리 시간은 별도다.
통신 오류 뒤 새 시작은 정상 상태를 다시 확인할 때까지 금지하며 worker 종료는
가능하다. 창 닫기를 반복해도 소유 worker 정리 완료 전에 창 종료를 허용하지 않는다.

캡처/VAD owner 정리가 끝나기 전 시작 버튼은 비활성화된다.
worker 종료가 5초를 넘으면 소유 프로세스를 종료하고 그 사실을 표시한다.
native 반환 시간과 캡처 Stop 응답 시간은 서로 다르다.

T02-03a부터 UUID session 제어와 Pause/Resume 버튼을 제공한다. source/history의 숫자 namespace와 전체 제품 wire의 차이는 [세션 계약](SESSION_CONTROL.md)을 따른다.
부분 전사는 선택 기능으로 연결했다. 로컬 번역 설정/history/오버레이는 [T03-02a 안내](DESKTOP_TRANSLATION.md)에 연결했다. 번역 및 실제 경계 품질 수용은 후속이다. 시간/span 기반 정합은 [T02-04b](ASR_RECONCILIATION.md)에 연결했다. [부분 전사](WORKER_PARTIAL_ASR.md). 간헐적 Initialize 대기와
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

## T02-02f 조작·조회 분리 (2026-10-01)

기존 구현에서 상태/history 조회가 정지 버튼을 잠그고 원문 만료가 IPC 완료에
의존하던 경로를 수정했다. 취소 전 snapshot은 조회와 최종 상태 읽기가 모두
끝난 뒤 적용한다. 의도적인 조회 취소는 통신 실패로 표시하지 않는다.

기존 Windows `scripts/check.ps1`을 `ECHOSUB_OFFLINE=1`,
`ECHOSUB_NUGET_SOURCE=%USERPROFILE%/.nuget/packages`로 실행했다.
Rust 91개·포맷·빌드·C# 빌드(경고/오류 0)·IPC smoke가 통과했다. 이 검사는 UI 지연을
실측하지 않는다. 지연된 조회 중 Stop/종료, 5초 만료, 반복 창 닫기의 실제
마우스 조작은 미검증이며 Stop 0.5초 수용을 통과했다고 간주하지 않는다.

## T02-03a 세션 제어 연결

세션 시작 시 UUID를 발급하고 기존 history를 유지한다. 일시정지는 입력/대기 작업을 취소하며 재개는 capture/VAD join 뒤 허용한다. 세션 종료 뒤 Idle은 native full 반환까지 확인한다. 설정 변경은 Idle에서 새 세션을 시작할 때 적용한다. 버튼·원문 화면의 실제 조작은 여전히 미검증이다. [실행·제어 계약](SESSION_CONTROL.md).

## T02-03b UUID history 표시

원문 표시에서 현재 UUID까지 확인한다. history는 UUID와 세션 시작 기준 초를 표시하며 이전 세션 record의 UUID도 보존한다. 원래 worker 시간 필드는 IPC 호환용으로 남긴다. 이전 worker가 UUID history capability를 제공하지 않으면 재빌드를 안내한다. [제어·필드 계약](SESSION_CONTROL.md)과 [성공/시작 실패 근거](evidence/T02-03b-windows-session-history.md)를 따른다. 화면 조작 수용은 미검증이다.
### 실험적 전사 입력 축소

`run-live-cuda-fast.bat -DecodeWindow -CaptionTiming`은 DTW/window 비교용이다.
세션 시작 전에 부분 전사/번역도 켠다. 파일 비교에서는 전체 전사 대비 속도
개선이 없어 기본으로 켜지 않는다. 재결합 실패 시 전체 입력 fallback을 수행하고
확정 전사는 전체 입력이다. [동작·측정](DECODE_WINDOWS.md).

### 실험적 짧은 부분 입력 패딩

`run-live-cuda-fast.bat -PadShortPartials -CaptionTiming`으로 선택한다.
부분 전사/번역 옵션을 세션 시작 전에 켠다. 기본 off이며 `-DecodeWindow`와
함께 사용할 수 없다. 실제 파일/HTTP 비교에서 원문은 빨랐지만 첫 번역의 큰
개선은 없었다. [입력·수명 계약과 측정](SHORT_PARTIAL_PADDING.md).

`run-live-cuda-fast.bat -PadShortPartials -SupportedPreview -CaptionTiming`은
영어 첫 문장의 관측된 꼬리를 임시 번역에 사용하는 별도 실험이다. 기본 off다.
한 파일에서 완전한 첫 문장 번역은 빨랐지만 뒤 조건절 문제가 남았다.
[범위와 한계](SUPPORTED_PREVIEW.md).
