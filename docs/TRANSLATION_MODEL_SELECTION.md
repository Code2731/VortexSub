# 실험 번역 모델 선택 (P3.1)

## 실행

기존 앱을 종료한 후 저장소 루트의 `run-live-hymt2.bat`을 실행한다.
설치된 Hy-MT2 1.8B Q4_K_M와 별도 llama.cpp b11146을 사용하고,
Whisper CUDA 빠른 부분 전사로 시작한다. 새 다운로드는 하지 않는다.
앱에서 **서버 연결 / 모델 조회**를 누르고 번역 **Ready**를 확인한다.
부분 번역을 비교하려면 **안정된 부분 먼저 번역 · 임시 결과**를 켜고 세션을 시작한다.
입력 선택은 **Hy-MT2 · 전용 번역 입력 / 실험**으로 준비된다.
원문 표시 기본 끔, 읽기 보호와 캡션 timing 옵션은 기존 설정을 따른다.

CPU 전사를 사용하려면 다음과 같이 직접 실행한다.

```powershell
scripts/run-live.ps1 -TranslationModel hymt2 -AsrBackend cpu
```

Qwen 비교는 기존 `run-live-cuda-fast.bat`을 사용한다. 가중치는 실행 중 바뀌지
않으므로 앱을 닫고 해당 모델의 런처를 다시 실행한다. 두 런처를 동시에 실행하지 않는다.
포트 1234가 사용 중이면 런처는 실패하며 다른 서버를 종료하거나 재사용하지 않는다.
앱 종료 시 자신이 시작한 서버와 임시 토큰 파일만 정리한다.

## 입력 방식과 적용 상태

| 입력 profile | 허용 모델 ID | 요청 방식 |
| --- | --- | --- |
| `standard` | Hy-MT2 외 기존 모델 | 기존 자막 입력; 문맥 분리 옵션 가능 |
| `qwen-greedy` | `qwen3-4b-instruct-2507-q4_k_m` | 기존 엄격 입력 + 고정 greedy 설정 |
| `hymt2-greedy` | `hy-mt2-1.8b-q4_k_m` | 전용 user 입력 + 문맥 구분 + 고정 greedy 설정 |

세션 종료 후 입력을 선택하고 **선택 모델 적용**을 누른다. 앱은 선택 대기와 실제
적용 상태를 구분한다. 후보 입력과 문맥 분리 옵션은 함께 사용할 수 없다.
모델 목록 조회 및 profile 검사가 성공한 뒤 연결을 교체한다. 실패하면 이전 연결,
모델과 입력을 유지하고 오류를 표시한다. 조회 중에는 세션/진단 입력을 시작할 수 없다.

IPC `configure_translation.input_profile`은 선택 사항이며 생략 시 현재 입력을
유지한다. `translator.input_profile`과 `pending_input_profile`은 적용/준비 상태다.
`hello.capabilities.translation_input_profiles`가 없는 worker는 후보 입력을 사용할 수 없다.

런처는 모델 SHA-256과 Hy-MT2 runtime build를 확인하고 `logs/translation-*.config.json`에
모델/profile/runtime 해시를 기록한다. 토큰은 기록하지 않는다. 외부 서버 연결은
모델 ID만 확인할 수 있으며 실제 가중치 파일까지 인증하지 않는다.

## 현재 범위

사용자는 실행 후 체감 속도 개선을 보고했다. 설정 유지·읽기 편의·품질과 장시간
동작은 [세션 기록과 확인 절차](SESSION_TRANSLATION_REVIEW.md)로 구분해 확인한다.

Hy-MT2는 선택형 실험이며 기본 모델은 Qwen이다. 설정 UI의 실제 조작/화면,
실패 복원 분기, 장시간 세션과 게임 동시 실행은 별도 확인이 필요하다.
빌드와 고정 입력 비교 범위는 [이번 실행 기록](evidence/model-selection-windows-20261002.md)을 참고한다.
