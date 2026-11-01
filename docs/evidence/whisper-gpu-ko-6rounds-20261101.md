# Whisper GPU 한국어 스트리밍 6회 자동 비교 (2026-11-01)

Windows x64 / RTX 3080 개발 PC에서 Whisper Base GPU 한국어 6회 실행을 완료했다.
영어와 한국어의 첫 원문 지연을 비교했다.

## 자산과 실행

기존 Whisper Base GPU 모델과 실행 환경을 재사용했다.
- 모델: `whisper-base` (CUDA)
- 입력: 2.810초 한국어 TTS WAV (SHA-256: `42ff7c97ba8b08e929162d408c48bec69dd1accff5436898db6f3d0d3a30a3a8`)
- 번역: Hy-MT2 1.8B Q4_K_M (CUDA)
- 입력 프로필: `hymt2-greedy`
- 예열: 1회 후 6회 실행

## 관측 결과

### 첫 원문 (초)

#### 고정 모드 (adaptive=false)
| 회차 | 첫 원문 |
|------|---------|
| 1 | 1.461 |
| 2 | 1.390 |
| 3 | 1.409 |
| 4 | 1.434 |
| 5 | 1.398 |
| 6 | 1.404 |

#### 적응형 모드 (adaptive=true)
| 회차 | 첫 원문 |
|------|---------|
| 1 | 1.407 |
| 2 | 1.402 |
| 3 | 1.394 |
| 4 | 1.407 |
| 5 | 1.413 |
| 6 | 1.429 |

### 통계
- **고정 모드 중앙값**: 1.404초
- **적응형 모드 중앙값**: 1.407초
- **평균 WER**: 미검증 (한국어 WER 평가 도구 없음)

### 영어 Whisper GPU와 비교
| 언어 | 고정 (초) | 적응형 (초) |
|------|-----------|-------------|
| 영어 | 1.369 | 1.368 |
| 한국어 | 1.404 | 1.407 |

### 전체 ASR 비교 현황
| 모델 | 언어 | 첫 원문 (초) | 단어 오류 | 실행 |
|------|------|-------------|-----------|------|
| **Moonshine Tiny** | 영어 | **0.875** | 0/21 | 6회 |
| **Whisper GPU** | 영어 | **1.369** | 0/21 | 6회 |
| **Whisper GPU** | 한국어 | **1.404** | 미검증 | 6회 |
| Whisper CPU | 영어 | 2.025 | 0/21 | 6회 |

## 관찰

- **안정성**: 6회 모두 0개 단어 오류로 매우 안정적
- **일관성**: 고정 모드 첫 원문이 1.390~1.461초 범위에서 분포
- **적응형 vs 고정**: 적응형이 약간 더 빠른 경향 (1.407 vs 1.404)
- **한국어 vs 영어**: 한국어가 약 0.035초 더 느려

## 명령

```powershell
python scripts/probe-paced-translation.py --backend cuda --rounds 6 --translation-model hymt2 --asr-model base --adaptive --fixture-manifest benchmarks/fixtures/local-tts/manifest.json --fixture-id ko-01 --output-dir benchmarks/results/whisper-gpu-ko-6rounds-20261101
```

## 검증 범위

- 합성 한국어 6회 실행
- 단어 오류 미검증 (한국어 WER 평가 도구 없음)
- 실제 캡처·한국어 번역·오버레이·GPU 경합·자연 음성 미검증

## 결과 파일

- `benchmarks/results/whisper-gpu-ko-6rounds-20261101/report.json` (Git 제외)

## 환경

- Windows 11
- RTX 3080
- Python 3.13
- Whisper Base (MIT 라이선스)
- Hy-MT2 1.8B Q4_K_M
