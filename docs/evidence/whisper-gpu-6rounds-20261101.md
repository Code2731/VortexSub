# Whisper GPU 영어 스트리밍 6회 자동 비교 (2026-11-01)

Windows x64 / RTX 3080 개발 PC에서 Whisper Base GPU 6회 실행을 완료했다.
기존 3회 실행을 6회로 확장해 통계적 신뢰도를 높였다.

## 자산과 실행

기존 Whisper Base GPU 모델과 실행 환경을 재사용했다.
- 모델: `whisper-base` (CUDA)
- 입력: 7.605초 영어 TTS WAV (SHA-256: `ec4391bf31bd0e983cbdd265344fa13ed6f1371c16c2a572b08dc54981101615`)
- 번역: Hy-MT2 1.8B Q4_K_M (CUDA)
- 입력 프로필: `hymt2-greedy`
- 예열: 1회 후 6회 실행

## 관측 결과

### 첫 원문 (초)

#### 고정 모드 (adaptive=false)
| 회차 | 첫 원문 |
|------|---------|
| 1 | 1.468 |
| 2 | 1.367 |
| 3 | 1.368 |
| 4 | 1.369 |
| 5 | 1.371 |
| 6 | 1.369 |

#### 적응형 모드 (adaptive=true)
| 회차 | 첫 원문 |
|------|---------|
| 1 | 1.363 |
| 2 | 1.373 |
| 3 | 1.361 |
| 4 | 1.375 |
| 5 | 1.377 |
| 6 | 1.361 |

### 통계
- **고정 모드 중앙값**: 1.369초
- **적응형 모드 중앙값**: 1.368초
- **평균 WER**: 0.0 (모든 회차 0개 오류)

### 기존 3회 결과와 비교
| 실행 | 고정 (초) | 적응형 (초) |
|------|-----------|-------------|
| 기존 3회 | 1.369 | 1.368 |
| 6회 | **1.369** | **1.368** |

### Moonshine과 비교
| 모델 | 첫 원문 (초) | 단어 오류 | 실행 |
|------|-------------|-----------|------|
| **Moonshine Tiny** | **0.875** | 0/21 | 6회 |
| **Whisper GPU** | **1.369** | 0/21 | 6회 |

## 관찰

- **안정성**: 6회 모두 0개 단어 오류로 매우 안정적
- **일관성**: 고정 모드 첫 원문이 1.367~1.468초 범위에서 분포
- **적응형 vs 고정**: 적응형이 약간 더 빠른 경향 (1.368 vs 1.369)
- **Moonshine vs Whisper**: Moonshine이 약 0.5초 더 빨라

## 명령

```powershell
python scripts/probe-paced-translation.py --backend cuda --rounds 6 --translation-model hymt2 --asr-model base --adaptive --output-dir benchmarks/results/whisper-gpu-6rounds-20261101
```

## 검증 범위

- 합성 영어 6회 실행
- 단어 오류 0개 확인
- 실제 캡처·한국어 번역·오버레이·GPU 경합·자연 음성 미검증

## 결과 파일

- `benchmarks/results/whisper-gpu-6rounds-20261101/report.json` (Git 제외)

## 환경

- Windows 11
- RTX 3080
- Python 3.13
- Whisper Base (MIT 라이선스)
- Hy-MT2 1.8B Q4_K_M
