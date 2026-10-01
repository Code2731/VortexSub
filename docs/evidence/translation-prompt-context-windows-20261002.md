# 번역 프롬프트·문맥 전달 비교

## 조건과 선택

Windows x64, Ryzen 7 5800X, RTX 3080, 기존 Qwen3-4B-Instruct-2507 Q4_K_M의
로컬 llama.cpp CUDA HTTP를 사용했다. 다운로드·캡처·재생 없이 작성된 영/일
12문장을 이전 원문 없음/있음으로 짝지었다. 24개 입력, 후보별 3회,
temperature 0.2/max_tokens 256, 서버 top-k 40/top-p 0.9/min-p 0.1이다.
warmup은 제외하고 후보 순서를 회전하며 문장 순서를 교대했다.

기존 지시는 원문과 이전 문맥을 한 user JSON에 넣는다. `strict`는 지시를
구체화하며 `isolated`는 이전 원문을 별도 user JSON, 현재 원문을 마지막 user
JSON으로 나눈다. 이전 문맥을 assistant 번역이나 system 지시로 넣지 않는다.
언어명 명시·조건 번역 예시·한국어 지시도 비교했지만 채택하지 않았다.

선택한 분리안에는 새로운 의미 오류도 있어 **기본 전환을 보류**했다.
`prepare_with_policy(IsolatedContext)`/HTTP owner/worker/런처에 명시적 실험으로
연결했다. 기본 `prepare`/`Owner::new`는 원래 요청이다. 새 다운로드·추론 엔진 교체,
IPC 필드 추가, 추가 번역 호출은 없다. 입력 예산·identity·남은 deadline을 유지한다.

## 실행 기록

다음 폴더는 모두 `benchmarks/results/` 아래 Git 제외 결과다.

| 출력 폴더 | 조건 | 측정 완료 |
|---|---|---:|
| translation-context-20261002-042533-819a70 | baseline/strict/isolated | 216 |
| translation-context-20261002-042723-7c3602 | isolated/readable/examples | 216 |
| translation-context-20261002-042841-e20ef6 | baseline/strict/korean | 103 후 중단 |
| translation-context-20261002-043124-ed6157 | baseline/strict/isolated | 216 |
| translation-context-20261002-043445-7da6f8 | baseline/실제 분리 prepare | 144 |
| preview-risks-20261002-043603-44812 | native ASR/분리 HTTP, SupportedPreview off/on | 42 |
| translation-context-20261002-044959-314f82 | 최종 off/opt-in 연결 HTTP + 실제 Rust owner | 48 + 48 |

첫 배치의 방향 정정 문맥은 두 문자열이었다. 이후 실제 preview가 보내는
`Take the left path. No.`/일본어 대응 한 문자열로 바꿨다. 배치 간 직접 합산한
품질 점수는 만들지 않는다. 한국어 지시 배치는 완료 103개 뒤 잘린 응답으로
중단됐으며 완전한 3회 비교에 포함하지 않는다. 보고서를 보존했고 이후 harness는
잘린 응답을 오류 행으로 기록한 뒤 끝까지 비교한다. 임의 재시도는 하지 않는다.

완료된 HTTP 후보 비교는 792회다. 원본 requests/results/runtime에 모델/서버/
실행 파일/fixture/profile 해시를 기록했다. 마지막 144회는 Rust prepare가 만든
후보 요청 자체를 사용했다(`production` 집계 이름은 기본 활성화 의미가 아니다).

## 마지막 144회 수동 검토

각 조건 3회이며 개선/악화 모두 보고한다. 문자열 일치나 자동 의미 점수는 아니다.

| 항목 | 기존 | 분리 후보 |
|---|---:|---:|
| 도착 대기문에 이전 “아직 밖에 있다” 혼입 | 3/3 | 0/3 |
| 영어 오른쪽 지시에 이전 “아니요” 혼입 | 3/3 | 0/3 |
| 문맥 있는 엔진/방어막 대비의 명령 반전 | 3/3 | 0/3 |
| 문맥 없는 `only if` 제한 보존 | 3/3 | 3/3 |
| 문맥 있는 `only if` 제한 보존 | 2/3 | 3/3 |
| 일본어 오른쪽 지시에 이전 정정 혼입 | 3/3 | 2/3 |
| `until I return`의 return 오역(문맥 없음/있음) | 6/6 | 6/6 |

기존 엔진 대비는 “엔진을 끄지 말고…” 또는 “쉴드를 끄세요”로 바뀌었다.
후보는 “엔진을 끄세요, 방어막은 끄지 마세요”였다. 반면 `until I return`은
문맥을 빼도 “나가기 전까지…”로 잘못 번역했다. 문맥 전달만의 문제는 아니다.

후보의 문맥 있는 공격 금지문은 **1/3에서 “방어를 해서는 안 됩니다”**로
행동이 바뀌었고 나머지도 guard를 “수비대”로 번역했다. 숫자 13/30은 이번 후보에
보존됐지만 gate가 “문자 옆” 등으로 바뀌고 문맥 없는 숫자문에는 외국 문자 혼입이
있었다. 문맥 없는 엔진 대비도 한 회차에서 shield가 “차단”으로 바뀌었다.
이 새 오류를 근거로 기본 전환을 보류했다.

일본어 돌아오기 조건은 기존 무문맥 3/3 누락에서 후보 3/3 조건 표현 포함으로
바뀌었으나 “문을 열지 마라. 돌아오기 전까지”처럼 부자연스러운 표현이 남았다.
unless 문장은 금지를 유지해도 신호 주체 `I`가 자주 생략됐다. 대명사 문장은
양쪽 모두 “그는 준비…”였으며 문맥 해결 역량의 개선 근거로 삼지 않는다.

HTTP 완료 중앙값은 기존 0.140초 / 후보 0.125초다. 차이가 작고 타이머 해상도,
서버 prefix cache, 프롬프트 위치의 영향이 있어 자막 속도 개선으로 주장하지 않는다.

## 실제 ASR→HTTP 경로

이전 7개 영어 합성 음성을 Whisper base CUDA와 같은 adaptive/짧은 partial
패딩으로 각 정책 3회씩 실행했다. 임시 후보 연결 상태에서 42회 배치를 완료한
후에 기본 off 정책을 연결했다. 배치 도중 요청 정책을 바꾸지 않았다.

현재 오른쪽 preview의 이전 “아니요” 혼입은 기존 기록의 off 3/3·on 3/3에서
후보 off 0/3·on 1/3으로 줄었다. `No.` 단위 자체의 “아니요”는 정상 번역이다.
전체 until 문장은 후보에서도 6/6 “나가기 전까지…”였고, guard는 최종 6/6
“수비대”였다. 엔진/방어막 최종 대비는 6/6 보존했다. 숫자는 13/30을 보존해도
gate 어휘 오류가 남았다. 최종 ASR은 기존 기록과 같은 원문이었다.

첫 번역 시간은 대체로 이전 기록과 비슷하며 부분 전사 보류 규칙과 SupportedPreview의
첫 의미 단위 차이가 더 크다. 실제 화면 표시/자연 음성/게임 격리·macOS는 미검증이다.

## 재현과 후속

최종 정책 연결 뒤 기본/실험 요청 각 24개의 body가 마지막 144회에 쓴
기존/후보 body와 정확히 일치했다. 별도 48회 HTTP 비교는 오류 0이며 실제 Rust
HTTP owner도 각 정책 24/24 완료했다. 소유 서버와 임시 API key를 정리했다(잔존 0).
위 추가 확인은 이전 792회 집계와 별개다.

`cargo fmt --all --check`, `cargo build --workspace --locked --offline`,
CUDA/VAD release build, Python/PowerShell 문법, C# ProtocolSmoke 프로젝트 build를
확인했다. 기존 HTTP fixture의 요청 파서는 두 메시지 배치를 읽도록 조정했으며
일반 Rust/C#/IPC 테스트는 실행하지 않았다. 최종 CLI 옵션으로 실제 UI/캡처를
실행하지 않았고, file probe 42회는 opt-in gate 연결 전 동일 후보 body의 측정이다.

```powershell
./scripts/probe-translation-context.ps1 -Rounds 3
./scripts/probe-translation-context.ps1 -Rounds 3 -Profiles isolated,readable,examples
$env:CMAKE_CUDA_ARCHITECTURES='86' # 이번 RTX 3080
./scripts/probe-preview-risks.ps1 -Rounds 3 -IsolatedTranslationContext
run-live-cuda-fast.bat -IsolatedTranslationContext -CaptionTiming
```

품질 gate false·새 실험 기본 off. Laya는 설치/다운로드하지 않았다.
다음은 같은 독립 원문과 문맥 짝을 유지한 번역 모델 역량 비교다. Laya는 미완성
원문의 요청 판단 후보지만 완전한 원문에서 발생한 return/숫자/행동 오역을
생성 모델 대신 고칠 수 있다고 가정하지 않는다. 새 모델 다운로드는 별도 동의가 필요하다.
