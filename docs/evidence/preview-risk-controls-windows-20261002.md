# 조건·부정·정정 preview 통제 비교

## 범위와 재현

Windows x64, Ryzen 7 5800X, RTX 3080, CUDA 12.6/CC 8.6에서 기존
Whisper base CUDA와 Qwen3-4B-Instruct-2507 Q4_K_M llama.cpp HTTP를 사용했다.
새 다운로드 없이 Microsoft Zira Desktop의 영어 합성 PCM 7개(2.270~4.030초)를
작성했다. 원문은 `benchmarks/preview-risk-texts.json`, WAV와 SHA manifest는 Git 제외다.
같은 adaptive 스케줄러와 짧은 partial 패딩을 사용해 SupportedPreview off/on을
각 3회 교대 실행했다. 보류 수정 전 42회, 수정 후 42회, 합계 84회다.
실제 native/HTTP owner와 파일 시간 공급이며 캡처·VAD·화면·게임 음성은 없다.

```powershell
$env:CMAKE_CUDA_ARCHITECTURES='86' # 이번 RTX 3080
./scripts/probe-preview-risks.ps1 -Rounds 3
```

출력은 `benchmarks/results/preview-risks-20261002-040502-31252/`(수정 전)과
`preview-risks-20261002-041003-24632/`(수정 후)다. 각 runtime에 모델/worker/음원
해시를 기록했다. 각 배치의 worker 해시는 하나이며 배치 종료 뒤 코드를 수정했다.
소유 HTTP 서버와 임시 API key를 정리했다(잔존 key 파일 0개).

## 수정과 시간

`units::english_fragment`에서 문장 부호/길이 판단보다 먼저 다음을 보류한다.

- 세 단어 이상이 `not`/`never`로 끝나면 `DanglingWord`.
- 조건 단어가 있고 `is` 등 be/have 술어에서 끝나면 `IncompleteCondition`.

두 preview 정책에 공통 적용하며 final 전체 번역·IPC 필드는 변경하지 않았다.
미래에 나타날 조건절을 예측하거나 번역 모델의 의미 오류를 판별하는 규칙은 아니다.

첫 번역 완료 시각 중앙값, 단위 초. 각 칸은 기존 off / 실험 on이다.

| 작성된 통제 문장 | 수정 전 | 수정 후 |
|---|---:|---:|
| We should take the left path. | 1.755 / 1.821 | 1.774 / 1.812 |
| Do not open the door until I return. | 1.174 / 1.191 | 1.170 / 1.168 |
| Attack the enemy only if the shield is down. | 2.600 / 2.612 | 3.142 / 3.176 |
| You must not attack the guard. | 1.185 / 1.190 | 1.805 / 1.833 |
| There are thirteen enemies near the gate, not thirty. | 1.798 / 1.797 | 1.796 / 1.802 |
| Take the left path, no, take the right path. | 1.167 / 1.187 | 1.166 / 1.184 |
| Turn off the engine, not the shield. | 1.778 / 1.197 | 1.778 / 1.185 |

수정 전 `only if the shield is`는 양쪽 3/3씩 “방어막이 있는 경우에만…”으로
조건을 바꿨다. `You must not`도 양쪽 3/3씩 행동 없는 번역이 적용됐다.
수정 후 각각 6/6에서 해당 중간 원문 요청/적용이 사라졌다. 조건 문장은 `down`까지
받고 번역하고, 기존 부정 preview는 `You must not attack`까지 기다렸다.
첫 번역은 약 0.54~0.64초 늦어졌다. 두 배치는 순차 실행이며 작은 시간 차이를
통계적 개선으로 일반화하지 않는다.

## 수동 의미 검토와 남은 문제

- **완전한 목적어:** 수정 후 on 첫 원문은 `left path`를 포함했다.
  정확한 전체 원문 대응 번역 off 2.532초 / on 1.812초다.
- **until 조건:** 양쪽 모두 조건을 듣기 전 “문을 열지 마십시오”를 먼저 표시했다.
  전체 ASR은 정확하지만 final 6/6이 “나가기 전까지…”로 `I return`을 오역했다.
  전체 입력을 받은 번역 모델/문맥 경로의 문제로 남는다.
- **if 조건:** 반대 조건의 중간 결과는 없어졌다. 수정 후 preview/final 일부는
  “방어막이 떨어지면”으로 `only`의 제한을 약화해 품질 통과로 판정하지 않는다.
- **부정:** on은 전체 목적어까지 번역하고 off는 “공격해서는 안 됩니다”부터 표시한다.
  최종 6/6은 경비원 공격 금지를 보존했다.
- **숫자:** 최종 13/30 대비는 6/6 보존했다. `gate`를 “문턱” 등으로 번역하는
  어휘 오류가 남았다. ASR 숫자 표기로 정확한 원문 문자열 일치 지표는 N/A다.
- **방향 정정:** 최종은 좌→우 정정을 보존하지만 현재 오른쪽 단위 번역에 이전
  “아니요”가 양쪽 3/3씩 섞였다. 이전 문맥의 현재 출력 혼입 문제다.
- **대상 대비:** 최종은 엔진만 끄는 대비를 보존했으나 shield 용어가 흔들린다.
  빠른 첫 출력은 뒤 대비까지 완전한 의미를 받았다는 뜻이 아니다.

집계는 적용 preview/final을 보존하며 자동 의미 채점하지 않는다. 숫자 표기/문장 부호가
다르면 정확한 원문 완료 지표가 없을 수 있다. 수동 검토는 위 범위에 한정한다.
**quality gate false, SupportedPreview 기본 off 유지.** 다음은 동일 전체 원문에서
이전 문맥 없음/있음과 프롬프트를 비교해 조건 오역·현재 원문 외 출력 혼입을 분리한다.

## 확인 범위

실제 CUDA/VAD release build와 기존 파일 probe 84회 완료. Python/PowerShell 문법,
`cargo fmt --all --check`, `cargo build --workspace --locked --offline` 확인.
일반 Rust/C#/IPC 테스트, 실제 overlay 표시, 자연/한국어/일본어 음성, macOS는 미실행이다.
