# Moonshine 영어 스트리밍 6회 자동 비교 (2026-11-01)

Windows x64 / RTX 3080 개발 PC에서 영어 Tiny Streaming 6회 실행을 완료했다.
기존 3회 실행을 6회로 확장해 통계적 신뢰도를 높였다.

## 자산과 실행

기존 Moonshine Tiny Streaming 모델과 실행 환경을 재사용했다.
- 모델: `tiny-streaming-en/quantized_26_08_21`
- 입력: 7.605초 영어 TTS WAV (SHA-256: `ec4391bf31bd0e983cbdd265344fa13ed6f1371c16c2a572b08dc54981101615`)
- 청크: 0.2초
- 업데이트 간격: 0.256초
- 예열: 1회 후 6회 실행

## 관측 결과

### 첫 원문 (초)
| 회차 | 첫 원문 |
|------|---------|
| 1 | 0.906 |
| 2 | 0.875 |
| 3 | 0.875 |
| 4 | 0.875 |
| 5 | 0.875 |
| 6 | 0.875 |

### 통계
- **첫 원문 중앙값**: 0.875초
- **첫 원문 범위**: 0.875 ~ 0.906초
- **평균 WER**: 0.0 (모든 회차 0개 오류)
- **최대 입력 밀림**: 0.020초
- **텍스트 변경 이벤트**: 9개 (모든 회차 동일)

### 기존 3회 결과와 비교
| 실행 | 첫 원문 (초) | 중앙값 |
|------|-------------|--------|
| 기존 3회 | 0.891 / 0.891 / 0.906 | 0.891 |
| 6회 | 0.906 / 0.875 / 0.875 / 0.875 / 0.875 / 0.875 | **0.875** |

## 관찰

- **안정성**: 6회 모두 0개 단어 오류로 매우 안정적
- **수렴**: 첫 원문이 0.875초로 수렴하는 경향
- **일관성**: 6회 중 5회가 0.875초로 동일

## 명령

```powershell
$env:PYTHONPATH = "J:/MyProject/VortexSub/models/moonshine-baseline/runtime"
models/tabby/venv/Scripts/python.exe -X utf8 scripts/probe-moonshine-baseline.py benchmarks/results/streaming-baseline-20261101/en-joined.wav benchmarks/results/streaming-baseline-20261101/report.json benchmarks/results/moonshine-streaming-20261101-6rounds/report.json --rounds 6
```

## 검증 범위

- 합성 영어 6회 실행
- 단어 오류 0개 확인
- 실제 캡처·한국어 번역·오버레이·GPU 경합·자연 음성 미검증

## 결과 파일

- `benchmarks/results/moonshine-streaming-20261101-6rounds/report.json` (Git 제외)

## 환경

- Windows 11
- RTX 3080
- Python 3.13
- Moonshine Tiny Streaming (MIT 라이선스)
