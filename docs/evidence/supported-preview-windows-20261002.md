# 관측된 문장 꼬리 preview: 실제 native/HTTP 비교

## 구현·조건

`Pipeline::set_supported_preview_enabled`와 internal `Unit.promoted`를 추가했다.
영어 첫 문장의 안정된 접두사 뒤에 관측된 1~2단어가 문장을 닫는 좁은 경우에만
preview source를 확장한다. 추가 단어가 안정됐다고 표시하지 않는다.
확장 단위 완료는 delivered 경계를 전진시키지 않고, 실제 stable에 들어오면
중복 요청 없이 경계를 확정한다. 원문 수정/새 revision의 기존 취소·stale 계약을 따른다.
숫자·부정·조건 꼬리와 조건절을 포함한 접두사는 확장하지 않는다.
이 제한은 문법/의미 이해 모델이 아니며 이후 나타날 조건절까지 예측하지 못한다.

Windows x64, Ryzen 7 5800X, RTX 3080/NVIDIA 617.14, CUDA 12.6/CC 8.6,
Whisper base CUDA·threads 8·DTW off, 기존 Qwen3-4B-Instruct-2507 Q4_K_M
로컬 llama.cpp HTTP를 사용했다. 양쪽 모두 짧은 partial 패딩을 켰다.
7.605초 합성 영어 파일, 첫 요청 0.8초·후보 간격 0.256초·같은 적응형
스케줄러/번역 cadence다. 기존 정책 off/on→on/off→off/on 각 3회,
조건별 새로운 native owner, 번역 1회 warmup, 준비 시간 제외다.
실제 파일 시간 공급·생산 owner/scheduler/HTTP이며 캡처/VAD/화면/게임은 없다.

## 결과

조건별 3회 중앙값이며 시간은 모두 초다.

| 항목 | 기존 정책 | 관측 꼬리 확장 |
|---|---:|---:|
| 첫 원문 | 0.859770 | 0.859186 |
| 첫 안정 구간 | 1.103819 | 1.101655 |
| 첫 번역 | 1.749202 | 1.852083 |
| 완전한 첫 원문 문장에 대응하는 번역 | 2.852790 | 1.852083 |
| 최종 번역 | 8.037979 | 8.001004 |
| native decode 누적 | 1.254523 | 1.402385 |
| 부분 원문 글자 삭제/교체 이벤트 | 1 | 2 |
| 번역 요청 횟수 | 5 | 5 |

회차별 on-minus-off 중앙값은 첫 번역 +0.091531초, 완전한 첫 원문 번역
-1.000707초다. 조건별 중앙값 차이와 paired 차이는 서로 다른 집계다.
`first_complete_source_translation_s`는 작성된 `We should take the left path.`와
정확히 일치하는 preview source의 적용 완료 시각이다. 한국어 번역 의미의
자동 품질 점수나 실제 화면에서 읽을 수 있는 시각은 아니다.

## 요청·품질 검토

off는 3회 모두 약 1.64초에 `We should take the left`를 요청해
“왼쪽을 선택해야 합니다”를 먼저 만들고, 약 2.65초에 완전한 문장으로 다시
요청했다. on은 3회 모두 약 1.64~1.66초에 완전한 `We should take the left path.`를
요청했다. 첫 한국어 결과는 “우리는 왼쪽 길을 따라가야 합니다” 2회,
“우리는 왼쪽 길을 따라야 합니다” 1회다. 이번 첫 문장의 뜻은 보존됐다고 검토했지만
자연 음성/다른 문장에서 의미 품질을 일반화하지 않는다.

단순 첫 출력 속도는 조금 늦어졌지만, 완전한 첫 문장에 대응하는 번역까지
기다리는 시간은 약 1초 줄었다. 두 지표를 함께 보고한다.

반면 on은 3회 모두 약 6.02초에 `Do not open the door`를 먼저 요청했고,
약 7.33초에 `until I return`까지 포함한 완전한 문장으로 수정했다. off 이번 3회는
이 조기 요청이 없었다. 새 확장 함수가 뒤 문장을 직접 확장한 것은 아니지만,
앞 단위 완료/후속 선택 및 ASR/HTTP 경합으로 뒤 요청 시점도 달라졌다.
조건절을 기다리는 보장이 없으며 이 차이를 새 정책의 품질 개선으로 보지 않는다.
원문 글자 수정도 1→2회로 늘었다.

최종 ASR 6/6은 동일한 세 문장이다. 최종 한국어는 5개 표현으로 달랐고
중복 표현/문장 순서 이동이 남았다. 의미 평가 corpus·자동 품질 점수는 없고
quality gate는 false다. 기본 활성화를 보류한다.

## 재현·검증·후속

```powershell
$env:CMAKE_CUDA_ARCHITECTURES='86' # 이번 RTX 3080 확인값
./scripts/probe-paced-translation.ps1 -Backend cuda -Rounds 3 -SupportedPreview
models/tabby/venv/Scripts/python.exe -X utf8 scripts/summarize-paced-padding.py benchmarks/results/paced-translation-20261002-033937-7d6ac4/report.json --condition-field supported_preview --first-source 'We should take the left path.' --output benchmarks/results/paced-translation-20261002-033937-7d6ac4/summary.json
```

원본 report/summary/runtime/native-http/server/GPU 로그는 위 Git 제외 폴더다.
기존 자산 해시와 실제 CUDA offload를 기록하고 소유 서버/API key를 정리했다.
native CUDA/VAD release·실제 ignored file probe, Python/PowerShell 문법,
Rust fmt와 오프라인 workspace build를 확인했다. 일반 Rust/C#/IPC 테스트 및
live/UI/자연/일본어/Mac 검증은 미실행이다. 이번에 IPC 필드와 확정 번역 prompt는 변경하지 않았다.

다음은 조건절/부정/숫자/방향 수정과 확장 단위 교정의 통제 음원 비교다.
첫 출력뿐 아니라 완전한 의미 단위의 번역 완료 시각·잘못된 중간 의미·수정 횟수를
판정 기준으로 삼는다. 현재의 어휘 veto를 낮추거나 이 한 파일 결과로 기본 활성화하지 않는다.
