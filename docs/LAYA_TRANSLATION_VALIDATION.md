# Laya 번역 출력 검증 실험

## 목적과 범위

사용자 동의(2026-10-02)에 따라 Laya 다국어 322M을 별도 진단 환경에 설치한다.
원문과 한국어 번역문을 비교해 의미·행동/부정·대상/방향·수량·조건·사족을
판별한다. 원문/번역은 작성된 fixture만 사용하며 실제 사용자 자막을 수집하지 않는다.
앱의 번역 모델, IPC, 자막 표시 경로에는 연결하지 않는다.

공식 [모델 카드](https://huggingface.co/convaiinnovations/laya-multilingual)와
[한계 설명](https://github.com/NandhaKishorM/laya#honest-limits)은 과신·부정 판단
오류를 보고한다. 공개 T4 속도는 RTX 3080 실측이나 번역 오류 검출 성능을 보장하지 않는다.

## 설치와 실행

`benchmarks/laya-model.json`에 모델 revision, 파일 크기, LFS SHA-256 또는
Git blob 해시와 SDK wheel SHA-256을 고정했다. 다운로드는 모델/토크나이저
678,201,614 bytes와 SDK 286,096 bytes다. 기존 `models/tabby/venv/`의
PyTorch CUDA·Transformers를 재사용하며 SDK는 `models/laya/sdk/`에
`--no-deps`로 설치한다. 원본을 검증한 후 SDK의 토크나이저 호환 수정을 위한
별도 로컬 복사본을 만든다. 결과와 모델은 Git 제외다.

```powershell
# 다운로드 동의가 있을 때만 실행
./models/tabby/venv/Scripts/python.exe -X utf8 scripts/setup-laya.py --download-approved
./scripts/probe-laya-translation.ps1 -Rounds 3 -Device cuda
```

추론은 로컬 파일과 offline 모드만 사용한다. CUDA가 없으면 실패하며 조용히
CPU로 대체하지 않는다. 실패 traceback과 부분 결과도 결과 폴더에 보존한다.

## 평가 계약

* `benchmarks/laya-translation-fixtures.json`: 12개 원문 × 정답/오류 후보 = 24개.
  기존 관찰 오류와 합성 최소 변형을 구분한다. 정답 label은 추론 전에 고정한다.
  모호한 `take`→섭취 여부는 평가에서 제외하고 수량/조건을 별도로 통제한다.
* 동일 후보를 영어/한국어 질문, A/B 및 B/A 순서로 평가한다. A는 보존, B는
  변경/누락/추가다. `true`/`false` 선택지와 보정되지 않은 확률 임계값은 사용하지 않는다.
* 각 호출은 여섯 판단을 함께 요청한다. 3회 반복은 독립 표본을 늘리지 않는다.
  고유 사례 수, 오류 검출 TP/FN, 정상 오차단 FP/TN, 순서 민감도를 함께 기록한다.
* CUDA 동기화를 포함한 호출 전체 시간을 초 단위 중앙값/p95로 기록한다.
  최초 로딩·프로필별 warmup은 제외한다. 입력 잘림과 불완전 응답은 무효다.

이 작은 진단 자료로 학습·확률 보정·임계값 최적화를 하지 않는다. 좋은 결과가
나와도 대표성 있는 별도 자료와 ASR/번역 동시 GPU 사용, 큐/취소 검증을 거쳐야
제품 연결을 판단할 수 있다. 오류를 검출해도 올바른 대체 번역이 자동 생성되지는 않는다.
