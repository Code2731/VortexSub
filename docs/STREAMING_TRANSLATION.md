# 안정된 부분 전사와 임시 번역

## 사용

세션 시작 전에 **안정된 부분 먼저 번역 · 임시 결과 / 기본 끔**을 선택한다.
부분 전사도 함께 켜진다. 번역 결과를 받으려면 서버가 Ready여야 한다. 부분 전사를 끄면
임시 번역도 꺼진다. 설정은 이번 앱 실행에만 유지되며 기본값은 false다.
오버레이 원문 표시 설정과는 독립적이다. 번역에는 **[임시 번역]** 표시가 붙는다.

## 처리 계약

- 같은 session/epoch/segment의 연속 두 전사가 공유하는 앞부분을 계산한다.
  영어는 단어 경계, 일본어·한국어는 문자/구두점 휴리스틱을 사용한다.
  최소 6문자, 영어 최소 3단어, UTF-8 최대 1,024바이트다.
  `stable_source`는 확정이나 confidence가 아니며 다음 전사에서 줄어들 수 있다.
- 임시 번역 대기는 최신 1개, 확정 번역 대기는 기존 FIFO 2개다.
  HTTP 실행은 하나만 예약한다. 임시 요청 간격은 최소 0.5초,
  임시 deadline은 1.5초, 확정 deadline은 기존 8초다.
- [문장/짧은 절 단위](TRANSLATION_UNITS.md)로 임시 번역 입력을 최대
  384 UTF-8 바이트로 제한한다. 성공한 닫힌 단위 뒤의 원문만 새로 보내며
  앞부분은 문맥이다. 수정 가능한 tail은 provisional이며 앞부분 반박 시 되돌린다.
- 새 전사 admission은 이전 임시 번역을 취소한다. 라이브 스케줄러는 ASR이나
  임시 HTTP가 처리 중이면 다음 partial admission을 최신 하나로 보류한다.
  확정 전사는 보류를 우회한다. 실제 HTTP 반환 전까지
  실행 예약을 유지한다. 전체 source key와 request ID가 다른 응답은 적용하지 않는다.
  확정 요청을 우선하며 최종 원문 전체를 새로 번역한다. 번역 조각을 이어 붙이지 않는다.
- worker는 새 revision에서 이전 임시 결과를 무효화한다. UI는 이미 표시한
  임시 자막을 같은 segment의 다음 전사/번역 대기 동안 유지한다. 기존 안정
  prefix와 수정된 원문이 일치하지 않으면 즉시 지운다. 새 유효 번역으로 교체하며
  라이브 화면은 [두 카드 읽기 정책](CAPTION_READING.md)에 따라 새 번역의
  실제 표시부터 4~10초를 제공한다. 같은 결과의 반복 조회는 만료를 늘리지 않는다.
  segment 변경은 이전 읽기 카드로 이동하며 Pause/Stop/epoch 변경은 모두 지운다.
- IPC의 `stable_source`, `translation_is_preview`와 session config의
  `partial_translation_enabled`는 추가 필드다. 이전 기록은 빈 문자열/false로 읽는다.
  `translation_source`, `translation_prefix`는 현재 임시 단위와 원문 guard이며
  구형/확정 기록은 빈 문자열이다. 최종 번역은 전체 원문을 계속 사용한다.

WhisperStreaming의 LocalAgreement-2에서 착안한 간단한 문자열 정책이다.
논문의 전체 스트리밍 스케줄러, wait-k 번역 모델이나 SimulWhisper 구현은 아니다.
ASR 모델에 종속된 token 형식을 사용하지 않지만 실제 연결은 Whisper만 검증했다.

## 재현과 판정

```powershell
./scripts/probe-streaming-translation.ps1
# 기존 빌드 또는 별도 Python 3.12 경로:
./scripts/probe-streaming-translation.ps1 -NoBuild -PythonPath C:/path/python.exe
```

기존 동의받은 Whisper/Qwen과 합성 WAV만 사용한다. 다운로드·캡처·재생은 없다.
CPU Whisper로 길어지는 WAV를 독립 전사한 뒤 전사 결과를 MOCK admission으로
재현하고 실제 로컬 HTTP 번역을 비교한다. 일본어는 작성한 전사 변화이며 실제
일본어 ASR이 아니다. availability는 음원 끝 + 파일 전사 측정값으로 추정한다.
실제 오디오 스케줄러, 게임 동시 실행, 화면 표시 지연을 측정하지 않는다.

[Windows 실험 결과](evidence/streaming-translation-windows-20261001.md).
조건 누락과 의미 반전이 남아 기본 활성화/품질 채택은 보류한다.
후보 모델 비교는 [ASR·번역 후보](MODEL_CANDIDATES.md)를 따른다.

참고: [WhisperStreaming 논문](https://arxiv.org/abs/2307.14743).
