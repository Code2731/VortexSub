# 부분 전사 스케줄러 검증 (2026-10-01)

## 변경과 검사

라이브 ASR 실행 중 새 partial이 현재 source revision을 추월하지 않도록
최신 요청 범위 하나를 보류한다. 임시 HTTP도 실제 반환까지 보류 조건이다.
확정 우선·취소 반환 예약·전체 identity 검증을 유지한다. 덮어쓴 오디오와
종료/epoch 변경의 대기는 폐기한다. snapshot은 실제 admission에서 만든다.

Windows에서 `scripts/check.ps1` PASS: Rust 149개, C# IPC/HTTP와 표시 fixture
21개 assertion, 모든 기본 빌드 PASS. 이전 표시 유지 변경도 이번에 실행했다.
느린 ASR 완료 적용·최신 대기·확정 취소·HTTP deadline 후 예약·오디오 만료를 검사했다.
native CPU/VAD release 빌드와 아래 명시적 native 측정 PASS.

## 실제 native 파일 재생

`scripts/probe-partial-scheduling.ps1 -WavPath
benchmarks/results/streaming-translation-20261001-093831-71c7ed/en-joined-prefix-10.wav`

결과: `benchmarks/results/partial-scheduling-20261001-175011-37848/report.json`.
기존 합성 영어 3문장 연결 7.605초, Whisper base CPU 8 threads.
동일 Windows 머신(기존 evidence: build 26200, AMD64 Family 25 Model 33,
16 logical CPUs)에서 실제 native owner를 사용했다. 모델/음원 SHA는 JSON에 기록했다.
각 조건 1회이며 순서는 기존→개선이다. 반복/순서 교차 실험은 아니다.
PCM을 5ms poll로 실제 속도에 맞춰 추가했다. 첫 요청은 0.8초;
VAD 대신 알려진 파일 끝에서 확정을 요청했다. preload는 시간에서 제외한다.

| 간격 | 방식 | 첫 부분 원문 (초) | 확정 원문 (초) | 표시 가능한 부분 갱신 | 완료 적용/무시 |
|---|---|---:|---:|---:|---:|
| 1.0초 | 기존 | 2.517 | 8.385 | 6 | 8 / 0 |
| 1.0초 | 개선 | 2.519 | 8.371 | 6 | 8 / 0 |
| 0.25초 스트레스 | 기존 | 없음 | 8.871 | 0 | 2 / 10 |
| 0.25초 스트레스 | 개선 | 1.757 | 8.935 | 9 | 11 / 1 |

개선 스트레스에서 요청 28회 중 26회를 보류했고 17회는 최신 대기로 교체했다.
완료 적용에는 NoSpeech가 포함되므로 갱신 수와 다르다. 마지막 확정의 partial
취소로 무시가 남는 것은 정상이다. 기본 1초에서는 경합이 발생하지 않았으며
첫 결과도 빨라지지 않았다. **0.25초를 기본값으로 채택하지 않았다.**

## 한계와 다음 작업

VAD·실제 HTTP·오버레이·게임 자원 경합·CUDA·자연 발화·macOS는 측정하지 않았다.
따라서 사용자가 느끼는 번역 지연의 주원인이나 제품 지연 목표 달성으로 해석하지 않는다.
0.8초 prefix는 앞쪽 pre-roll이 없는 파일이라 Whisper의 1초 최소 입력 경고가
발생했다. 라이브 VAD pre-roll을 포함한 입력과 같다고 볼 수 없다.
다음 순서는 이전 읽기 카드와 현재 draft 분리, 그 뒤 의미 단위 번역이다.
