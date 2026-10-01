# 짧은 부분 전사 입력 패딩 실험

실제 owner/HTTP 각 3회에서 첫 원문/안정 구간은 약 0.52초 빨랐으나 첫 번역의
큰 개선은 없었다. [실측과 후속 판단](evidence/short-partial-padding-windows-20261002.md).

## 실행

```powershell
run-live-cuda-fast.bat -PadShortPartials -CaptionTiming
# 파일 기반 실제 ASR→안정 구간→번역 비교
./scripts/probe-paced-translation.ps1 -Backend cuda -Rounds 3 -PadShortPartials
```

기본 off이며 `-FastPartials`가 켜진 live CUDA 경로에서만 선택한다.
`-DecodeWindow`와 함께 사용할 수 없다. launcher는 worker에
`--experimental-pad-short-partials`를 전달한다. 부분 전사와 부분 번역 UI 옵션도
세션 시작 전에 켜야 한다. 이 문서의 파일 probe는 캡처를 시작하지 않는다.

## 입력과 수명 계약

Whisper의 최소 입력 검사를 통과하도록 1초 미만의 비어 있지 않은 부분 PCM을
native owner 안에서 1.02초까지 zero-pad한다. 최대 임시 입력은 16,320 float
samples(65,280 bytes)다. 무음 제외 검사는 실제 PCM을 기준으로 먼저 수행한다.
오디오 callback에 할당이나 추론을 추가하지 않는다.

실제 PCM snapshot, audio_start/end, session/epoch/segment/source_revision,
history 범위와 자막 수명은 원래 값을 유지한다. 합성 무음을 다음 오디오나
문맥으로 누적하지 않는다. 확정 전사와 1초 이상 입력은 패딩하지 않는다.
기존 단일 native 예약과 같은 취소 토큰을 사용하고 완료/stale 적용 검사를 따른다.
패딩 적용 여부는 stderr에 실제/native samples를 기록하며 IPC 계약은 추가하지 않았다.

## 비교와 해석

파일 probe는 같은 0.8초 첫 요청·0.256초 VAD 후보 간격·적응형 스케줄러에서
패딩 off/on 순서를 회차별로 바꾼다. 같은 Qwen 모델·HTTP owner를 사용한다.
`scripts/summarize-paced-padding.py <report.json> --output <summary.json>`는
첫 원문/안정 구간/번역 시간, native 누적 시간, 부분 원문의 글자 수정,
번역 요청 횟수와 회차별 paired 차이를 집계한다.

시작 준비 시간은 제외하며 파일 끝을 알고 있다. VAD·캡처·화면·게임 부하의
실시간 결과가 아니다. 빠른 첫 부분 원문에는 잘못 예측한 단어가 포함될 수 있으므로
요청 원문과 번역을 함께 검토하고 기본 활성화 여부를 판단한다.
