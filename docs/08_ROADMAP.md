# 08. Codex 구현 로드맵

달력 기준 완료일은 고정하지 않는다. 각 단계의 실기기 검증 결과를 보고 다음 범위를 확정한다. 작업 하나는 독립적으로 검토·테스트할 수 있는 크기로 유지한다.

## M0. 뼈대와 양 플랫폼 위험 검증

### T00-01 — 저장소·UI·worker·최소 IPC

입력: 01/03/05 문서. 출력: Rust workspace, Avalonia 앱, hello/ping/get_state/shutdown, 구조화 오류, check 스크립트, 고정된 dependency/SDK 기록.

수용: UI가 실제 worker를 시작·종료, protocol mismatch와 EOF 테스트, stdout 오염 없음, 표시되는 mock은 MOCK으로 명시. 현재 OS의 build/test 결과를 기록한다. 다른 OS는 CI 또는 별도 장치에서 후속 확인한다.

### T00-02 — Windows 시스템 오디오 PoC

WASAPI loopback으로 10분 실제 PCM과 레벨을 확인한다. default device/fixed device 차이와 장치 분리, sample format, timestamp를 기록한다. 아직 ASR 없이 캡처만 검증한다.

### T00-03 — macOS 시스템 오디오·번들 PoC

ScreenCaptureKit 브리지, 최소 `.app` 배치, worker 수명·run loop, 권한 deny/allow/retry, 10분 PCM 수신을 검증한다. 별도 가상 오디오 장치를 기본 경로로 쓰지 않는다.

### T00-04 — 모델 및 오버레이 소규모 검증

whisper.cpp small/base의 실제 fixture 전사와 한 개 번역 모델의 한국어 결과를 평가한다. 오버레이 기본 topmost/transparent와 macOS Spaces 동작도 검증한다. 정확한 모델 파일/해시·라이선스를 기록한다. 모델 다운로드는 사용자 동의 후에만 진행한다.

**M0 gate:** 양 OS 실제 캡처가 확인되고, 선택한 배포 구조로 macOS 권한이 동작하며, 기본 모델 경로가 실행 가능함을 확인한다. Windows 제품을 완성한 뒤 macOS 가능 여부를 처음 확인하는 순서를 피한다.

## M1. 공통 코어와 결정론적 테스트

### T01-01 — 오디오 시간축·리샘플링·VAD

16k mono pipeline, VAD state, sample buffer 상한, gap 모델을 구현한다. silence/임의 채널/장치 변경 fixture와 unit test를 추가한다.

### T01-02 — segment·revision·state machine

partial/final, epoch 취소, queue latest-wins, history, protocol schema/DTO를 구현한다. deterministic mock으로 역순 응답과 긴 발화를 검증한다.

**M1 gate:** UT-001~009 중 구현 범위 테스트 통과, async 작업이 무한 적재되지 않음, 실제 OS 모듈 없이 core CI 가능.

## M2. 실제 로컬 전사 세로 경로

### T02-01 — whisper.cpp context·스케줄러

모델 1회 로드, 재사용, partial/final decode, no-speech/반복 검사를 구현한다. 로그에 raw transcript를 남기지 않는다.

### T02-02 — Windows 실시간 전사 연결

실제 loopback→VAD→ASR→UI. 출력 장치 변경과 pause/stop 후 결과 폐기를 확인한다.

### T02-03 — macOS 실시간 전사 연결

동일 공통 코어를 ScreenCaptureKit에 연결한다. Metal/CPU 활성 경로와 권한 복구를 실제 시험한다.

**M2 gate:** 양 OS에서 영어/일본어/한국어 원문 자막 확인. 번역 없이도 사용할 수 있는 상태. 파일 전사만 성공한 상태로 완료 처리하지 않음.

## M3. 로컬 번역과 사용 가능한 개발 MVP

### T03-01 — 외부 번역 어댑터

모델 조회, 연결 테스트, final-only 번역, deadline/재시도/skip, 짧은 context, stale 응답 검증. Windows LM Studio와 Mac oMLX에서 각각 계약 테스트한다.

### T03-02 — 메인 화면·오버레이·기록

원문/번역 카드, 상태/오류, 위치/크기/글꼴, 다중 화면, history/TXT/SRT export를 구현한다. 당장 click-through를 완료하기 어렵다면 미구현 상태로 명시하고 베타 gate로 남긴다.

### T03-03 — 개발 MVP E2E

양 OS 실제 시스템 오디오에서 30분 전사·번역·자막. 서버 중단/장치 분리/무음에서 올바른 동작 확인. 실제 지연 측정 첫 baseline을 기록한다.

**M3 gate:** P0 수용 테스트 통과. 이 단계는 외부 로컬 서버가 필요한 개발 MVP다.

## M4. 독립 실행형 제품 베타 기능

### T04-01 — 모델 카탈로그와 설치

동의/크기 표시/다운로드/취소/해시/라이선스/삭제, 손상 파일 복구. 자동 외부 코드 실행 금지.

### T04-02 — 앱 관리형 번역 런타임

검증한 llama-server 빌드·모델을 패키징하고 private loopback+인증, health, 수명/종료, child cleanup 구현. LM Studio/oMLX가 없는 환경에서 번역까지 동작해야 한다.

### T04-03 — 오버레이 제품화

OS별 click-through, 잠금 해제 복구, 포커스/Spaces/다중 모니터, 선택적 트레이/메뉴바 동작을 구현한다.

**M4 gate:** P0B 기능이 구현됨. 자체 설치/실행 성공과 실제 게임/영상 창 조합별 결과를 남김.

## M5. 성능·패키징·출시 검증

### T05-01 — 기준 장치 벤치마크와 프로필

M3 Pro 36GB/RTX 3080 10GB에서 base/small, partial on/off, 번역 후보를 비교하고 Balanced/Economy를 결정한다. 모델 크기를 임의로 키워 품질 문제를 덮지 않는다.

### T05-02 — 장애·soak·게임 공존

2시간 연속·100회 세션 제어·서버 중단·메모리 부족·모니터 분리. FPS·지연·누락·메모리 추이를 함께 측정한다.

### T05-03 — Windows/macOS 배포

SDK 없는 환경 설치, Windows native 의존성, macOS 서명/공증·권한·번들, 업데이트 시 config 호환, 모델 폴더 보존. 사용 안내와 KNOWN_ISSUES 작성.

**M5 gate:** 07번 문서의 베타 gate 판정 완료. 불충족 항목은 실제 결과와 대응 결정을 공개하고 “통과”로 적지 않음.

## M6. 후속 기능 후보 — 자동 착수하지 않음

앱별 캡처, 용어집/게임 용어팩, 언어 자동 감지, 부분 번역, 더 빠른 ASR/번역 엔진, Core ML/NPU 최적화, Linux/PipeWire, 전역 단축키, 마이크 입력/화자 분리. 각 항목은 별도 요구사항과 테스트를 먼저 만든다.

## 단계별 작업 원칙

각 작업 시작 시 `관련 요구 → 변경 파일 → 테스트 → 완료 조건`을 기록한다. 종료 시 코드와 STATUS를 함께 갱신한다. 설계 변경은 결정 기록에 남기되 사소한 변수명마다 문서를 비대하게 만들지 않는다. 선행 조건이 막히면 독립 작업만 진행하고 미검증 플랫폼을 완료로 꾸미지 않는다.

