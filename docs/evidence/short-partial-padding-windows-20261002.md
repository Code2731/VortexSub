# 짧은 부분 입력 패딩: owner와 실제 번역 비교

## 구현과 조건

`ModelConfig.pad_short_partials`와 실험 CLI/런처 옵션을 연결했다.
native owner에서 비어 있지 않은 1초 미만 partial만 16,320 samples까지
zero-pad한다. 실제 PCM snapshot과 product range/identity는 변경하지 않는다.
무음은 실제 PCM으로 먼저 제외한다. final과 1초 이상 입력은 원본을 유지한다.
취소 토큰·단일 native 예약·완료/stale 적용 계약을 사용한다.
정렬 메타데이터를 쓰는 DecodeWindow와 동시 선택을 거부한다. 기본 off다.

Windows x64, Ryzen 7 5800X, RTX 3080, NVIDIA 617.14, CUDA 12.6/CC 8.6,
Whisper base CUDA·threads 8·DTW off, 기존 Qwen3-4B-Instruct-2507 Q4_K_M의
로컬 llama.cpp HTTP를 사용했다. 새 다운로드는 없다.

세 합성 영어 음원을 연결한 7.605초 파일에서 off/on 각 3회 실행했다.
첫 ASR 요청 0.8초, 후보 간격 0.256초, 같은 적응형 스케줄러와 안정 구간/
번역 단위 정책을 적용했다. 순서는 off/on→on/off→off/on이다.
같은 서버를 소유해 1회 번역 warmup 후 비교했고, 각 조건 native owner는 새로 생성했다.
준비 시간은 제외했다. 실제 시간에 따라 파일 PCM을 공급한 측정으로 캡처·VAD·
화면 표시·게임 부하와 장치 조건은 포함하지 않는다.

## 측정 결과

각 조건 3회 중앙값. 시간은 모두 초다.

| 항목 | 패딩 off | 패딩 on |
|---|---:|---:|
| 첫 원문 | 1.377687 | 0.854264 |
| 첫 안정 구간 | 1.637460 | 1.111212 |
| 첫 번역 | 1.812143 | 1.793010 |
| 최종 번역 | 8.120544 | 8.116880 |
| native decode 누적 | 1.420328 | 1.279314 |
| partial 원문 이벤트 | 12 | 13 |
| 부분 원문의 글자 삭제/교체 이벤트 | 2 | 1 |
| 번역 요청 | 5 | 5 |

paired on-minus-off 중앙값은 첫 원문 -0.521610초, 안정 구간 -0.513730초,
첫 번역 -0.033439초다. 조건별 중앙값 차이와 paired 차이는 다른 집계다.
첫 번역 범위 off 1.762359~1.851235초, on 1.778705~1.804703초로 겹친다.
3회/한 파일에서 유의미한 번역 가속을 확인했다고 보지 않는다.
누적 decode 변화에는 적응형 스케줄러의 작업 선택 차이도 들어가므로 개별
추론의 속도 개선으로 해석하지 않는다.

## 실제 보류 이유와 텍스트 검토

패딩 on 첫 실행의 적용 이벤트:

| 경과 (초) | 적용 원문 | 안정 원문 | 번역 단위 판정 |
|---|---|---|---|
| 0.852 | We should take the | 없음 | NoStablePrefix |
| 1.108 | We should take the left | We should take the | DanglingWord |
| 1.651 | We should take the left path. | We should take the left | Eligible |
| 2.137 | 같은 완전한 문장 | 같은 완전한 문장 | Cadence |
| 2.648 | 같은 완전한 문장 | 같은 완전한 문장 | Eligible |

즉 추론 결과가 일찍 도착해도 `the`로 끝난 안정 원문은 의미 단위가 덜 완성되어
번역을 보류한다. 그 다음 결과를 기다리는 시간이 첫 번역 가속을 제한했다.
이 veto를 단순히 제거하면 불완전한 목적어를 번역하는 위험이 있어 유지했다.

off의 첫 요청은 3회 모두 완전한 `We should take the left path.`이며
번역은 “우리는 왼쪽 길을 따라가야 합니다.”였다. on은 3회 모두
`We should take the left`로 요청했고 “왼쪽을 선택해야 합니다”라는 결과가 나온 뒤
완전한 문장으로 수정됐다. 부분 원문의 문자 수정 감소가 번역 의미의 완결성
개선을 뜻하지는 않는다.

최종 ASR 원문은 6/6 같은 작성된 세 문장이다. 최종 번역은 5개 표현으로 달랐다.
“문 앞/문 근처”와 인칭 표현 차이, 어색한 중복 표현과 문장 순서 이동이 양쪽에
남았다. 자동 의미 품질 점수/자연 발화 수용은 없으며 quality gate는 false다.

## 검증·재현·후속

```powershell
$env:CMAKE_CUDA_ARCHITECTURES='86' # 이번 RTX 3080의 확인된 설정
./scripts/probe-paced-translation.ps1 -Backend cuda -Rounds 3 -PadShortPartials
models/tabby/venv/Scripts/python.exe -X utf8 scripts/summarize-paced-padding.py benchmarks/results/paced-translation-20261002-032408-6dfd4d/report.json --output benchmarks/results/paced-translation-20261002-032408-6dfd4d/summary.json
```

원본 report/summary/native-http.log/server.log/runtime.json/GPU samples는 위 Git 제외
결과 폴더에 남겼다. 모델·worker/server·음원의 해시와 실제 GPU 사용 로그를 보존했다.
프로브가 소유한 서버를 종료하고 API key 파일을 삭제했다.
native CUDA/VAD release·오프라인 workspace build, 실제 ignored file probe, Python/PowerShell 문법·Rust fmt를
확인했다. 일반 Rust/C#/IPC 테스트 및 live/UI/게임·자연/일본어·Mac 검증은 미실행이다.
이번에는 IPC 필드를 추가하지 않았다.

기본 활성화를 보류하고 `run-live-cuda-fast.bat -PadShortPartials -CaptionTiming`에만
실험을 남긴다. 다음은 **번역 단위의 미완성 꼬리 처리**다. 잘린 목적어를 그대로
번역하도록 제한을 낮추기보다, 관측된 뒤 단어의 지지 조건과 필요한 문맥을 붙이는
preview 정책을 좁은 조건에서 비교한다. 완성 원문과 임시 번역 수정의 대응을 계측해
표시 시점·오역·수정 빈도 기준으로 평가한다. 패딩만으로 번역 지연이 해결됐다고
보고하지 않는다.
