# T02-01b Windows CPU 실제 VAD 파일 검증

실행일: 2026-09-30. Windows NT 10.0 build 26200, AMD64 Family 25 Model 33/논리 16개, Rust 1.90.0, .NET 10.0.102. Silero v6.0 ONNX·ONNX Runtime 1.22.0 CPU, intra/inter 각 1 thread. Whisper base CPU 8 threads `/O2` release를 사용했다.

## 자산과 재현

사용자가 새 모델 2.33 MB와 CPU runtime ZIP 72.37 MB 다운로드에 동의했다. 공식 pinned revision/release에서 받은 뒤 모델 Git blob SHA-1을 GitHub metadata와 대조하고, 모델·ZIP·DLL SHA-256을 [카탈로그](../../benchmarks/vad-assets.json)에 고정했다. runtime release는 upstream digest가 없어 최초 공식 다운로드의 측정 해시를 사용했다. 이후 실행은 모델/DLL 해시가 일치해야 가능하다.

```powershell
.\scripts\download-vad-assets.ps1
.\scripts\probe-worker-vad.ps1 -Offline
$env:ECHOSUB_OFFLINE='1'
$env:ECHOSUB_NUGET_SOURCE="$env:USERPROFILE/.nuget/packages"
.\scripts\check.ps1
```

최종 VAD summary: Git 제외 `benchmarks/results/worker-vad-cpu-20260930-221133/report.json`. 실제 모델과 음원·생성 WAV·전사문·full 결과는 커밋하지 않는다. 초기 check 빌드를 함께 실행했으며 다른 프로세스 부하를 통제한 성능 벤치마크는 아니다.

## 관측 결과

| 검사 | 결과 |
|---|---|
| 합성 en/ko 각 10개 | 모든 파일에서 원문 final을 하나 이상 전달 |
| 정상 final 총합 | 32: 초기 20+epoch 재시작 10+두 발화 파일 2 |
| 빈 ASR 실패 | 1: ko-08의 두 후보 중 하나가 Failed/InvalidText; 빈 Final 없음 |
| digital silence | final 0, 실제 VAD 호출 0, ASR 호출 0 |
| 1 kHz tone·고정 seed 저레벨 잡음 각 2초 | 실제 VAD 호출 있음, 발화 범위·ASR 호출 0 |
| epoch reset 10회 | segment 수·상대 끝 경계 재현, 새 identity의 source final 전달 |
| 두 발화+0.7초 zero gap 파일 | 분리된 segment 2개·각 final/history 전달 |
| VAD model/DLL SHA 오류 | 각각 VAD_MODEL_FAILED, ASR 0; ping 가능·정상 종료 |
| 실제 VAD frame 호출 합계 | 2,035 |
| 파일별 VAD 처리 평균/최대 | 0.008699초 / 0.014931초 |
| 처리 중 ping 최대 | 0.041636초 |
| 기본 검증 | Rust 79개·fmt/workspace·C# 빌드와 확장 IPC smoke PASS |

VAD 처리는 파일 전체의 model call+segmentation 시간이다. DLL/모델 준비·디스크 읽기·Whisper decode·자막 UI latency는 포함하지 않는다. 모델 없는 fixture 5개는 recurrence/context·epoch/stale 거부·600초 exact-zero 모델 호출 0·true tail·잘못된 입력/출력과 비음성의 빈 range를 확인한다.

## 품질 판정과 남은 범위

진단 구현 검사는 PASS이며 품질 gate는 **false**다. ko-08의 추가 후보는 모델/발화 경계 검토 항목으로 남긴다. 실패를 제거하거나 주변 발화와 합쳐 성공으로 계산하지 않았다. 한두 종류 생성 음원으로 음악/효과음 오검출이나 자연 발화 정확도를 일반화하지 않는다.

live WASAPI PCM·장치/clock 변화·gap/Pause/Stop 중 VAD state 수명·partial/overlap 텍스트 정합·화면 자막·번역·일본어/자연 음성·게임 공존·Mac·장시간 실행은 미검증이다. 기본 UI는 MOCK 오버레이다. M2 제품 gate는 여전히 미통과다.

기존 ASR-only 경로 회귀: 같은 native-vad binary를 `scripts/probe-worker-asr.ps1 -Backend cpu -Offline -NoBuild`로 실행해 final 20개·무음 억제·취소/재시작 10회·추론 중 정상 종료를 다시 통과했다. `benchmarks/results/worker-asr-cpu-20260930-221610/report.json`의 결과이며 VAD opt-in 없이 기존 경로가 유지된다.
