# 실시간 번역 오픈소스 구현 검토

검토일: 2026-10-01. 공개 저장소 4개의 문서와 고정 커밋의 소스를 읽었다.
실행·성능 측정·설치·모델 다운로드는 하지 않았다. 아래 수치 중 외부 프로젝트의
설정값은 그 프로젝트의 동작 조건이며 VortexSub 성능 보장이 아니다.

## 검토 결과

### Whisper-Streaming / SimulStreaming

[Whisper-Streaming](https://github.com/ufal/whisper_streaming)은 LocalAgreement로
연속 전사의 공통 앞부분을 확정한다. 현재 문서는 후속 SimulStreaming을 안내한다.
Whisper의 기본 음성 번역은 영어 출력이므로 한국어 자막에는 별도 번역 단계가 필요하다.

[SimulStreaming](https://github.com/ufal/SimulStreaming)의 기본 파일 시뮬레이션은
추론 중 도착한 오디오를 다음 처리에 포함한다. 고정 주기로 실행 중 결과를
계속 추월하는 구조와 다르다. ASR의 AlignAtt는 decoder attention을 활용한다.
현재 whisper.cpp 호출에 숫자 설정 하나를 추가해 얻을 수 있는 기능이 아니다.

번역 코드 `SimulLLM`은 새 원문을 누적하고 이전 번역의 확정 부분을 생성 prefix로
사용한다. 연속 번역의 공통 앞부분을 확정하고 나머지는 미확정으로 관리한다.
**ASR 안정성 외에 번역 자체의 안정성도 관리한다는 점**이 주요 참고 사항이다.
우리 Qwen에서 앞부분을 강제 고정하면 초기 오역이 남을 수 있어 그대로 채택하지 않는다.

근거: [ASR 처리 루프](https://github.com/ufal/SimulStreaming/blob/077ea37d5ab4ff98bc567e4507f140dc4e5d5ad6/simulstreaming/whisper/whisper_streaming/whisper_online_main.py),
[번역 buffer·commit](https://github.com/ufal/SimulStreaming/blob/077ea37d5ab4ff98bc567e4507f140dc4e5d5ad6/simulstreaming_translate.py).

### Sublume

Whisper/SenseVoice 계열 ASR와 OpenAI 호환 번역 API를 조합한다.
기본 VAD 분할과 별도로 incremental 경로에서는 1.5초 미만 입력을 건너뛰고,
전사를 문장으로 나눠 마지막 미완성 문장을 제외한 앞 문장을 출력한다.
처리한 오디오를 잘라내 누적 재전사를 줄인다. 확인한 코드의 기본 경로는
문자 비율로 trim 위치를 추정하므로 정확한 음성 경계 보장으로 보지 않는다.

번역 generator는 누적된 실제 API delta를 전달하고, UI는 0.05초 주기로
표시 갱신을 제한한다. 원문·번역을 message ID별 행으로 관리한다.
**음성 입력의 부분 처리와 번역 토큰 스트리밍은 별개의 기능**이다.

근거: [incremental 전사·trim](https://github.com/moonstarsky37/Sublume/blob/b8ef878cd29493142ad2fa3b5f7946140175a81b/sublume/core/pipeline.py),
[번역 generator](https://github.com/moonstarsky37/Sublume/blob/b8ef878cd29493142ad2fa3b5f7946140175a81b/sublume/translation/translator.py),
[자막 행 갱신](https://github.com/moonstarsky37/Sublume/blob/b8ef878cd29493142ad2fa3b5f7946140175a81b/sublume/ui/overlay/chat.py).

### LiveTranslate

부분 전사→번역은 별도 순차 루프에서 약 1.2초 대기 후 실행한다. 확정 작업이
대기/실행 중이면 부분 tick을 건너뛴다. 1초 미만 입력과 변화 없는 오디오도
건너뛴다. 느린 decode 도중 segment가 바뀌었는지 generation으로 확인한다.
문장 끝을 감지하면 충분한 입력 조건 아래 짧은 실제 무음에서 일찍 닫도록 한다.
완성 자막과 임시 자막은 표시 정책이 다르며, 확정 자막 hide 기본값은 9초다.

오버레이의 글자별 효과 중 `streamText`는 **이미 완성된 문자열을 애니메이션으로
보여주는 것**이다. 이를 추론의 첫 토큰 지연 개선으로 평가하면 안 된다.
언어쌍별 MarianMT 경로도 있으나 이 앱의 지원 범위를 일본어→한국어로 확대하지 않는다.

근거: [부분 추론 루프·확정 우선](https://github.com/NBS282/LiveTranslate/blob/3dc57e4378d287537d9e16121d966ced5d620e0a/src-tauri/src/translation/live.rs),
[부분 cascade](https://github.com/NBS282/LiveTranslate/blob/3dc57e4378d287537d9e16121d966ced5d620e0a/src-tauri/src/translation/engine/native_cascade.rs),
[표시·hide·애니메이션](https://github.com/NBS282/LiveTranslate/blob/3dc57e4378d287537d9e16121d966ced5d620e0a/src/overlay.ts).

### LiveCaptions-Translator

자체 ASR 대신 Windows Live Captions의 텍스트를 읽는다. 구두점, 원문의
변경 누적 횟수, 변화 없이 기다린 횟수로 번역을 요청한다. 결과가 준비되면
표시 문자열을 교체하며 완성 문장에는 0.72초 표시 loop 대기를 둔다.
복수 문장을 오버레이에 표시하는 설정도 제공한다.

참고할 점은 **번역 요청 조건과 읽기 시간을 분리하는 것**이다. 이 구현의
task 목록·취소 정책을 우리의 bounded queue/full-key 검증 대신 복사하지 않는다.
Windows 제공 ASR의 언어·배포 의존성 때문에 현재 양 OS 엔진의 즉시 대체는 아니다.

근거: [요청 조건·표시 loop](https://github.com/SakiRinn/LiveCaptions-Translator/blob/a6fee12757b15edbeef7c60f8895e0be694801e1/src/Translator.cs),
[작업 완료·취소](https://github.com/SakiRinn/LiveCaptions-Translator/blob/a6fee12757b15edbeef7c60f8895e0be694801e1/src/models/TranslationTaskQueue.cs),
[오버레이 문장 수 설정](https://github.com/SakiRinn/LiveCaptions-Translator).

## 현재 VortexSub와의 차이

| 항목 | 현재 구현 | 개선 방향 |
|---|---|---|
| 부분 전사 admission | 0.8초 이후 1초 간격, 새 admission에서 source revision 증가 | 실행 중 partial을 매번 추월하지 않게 최신 대기 입력을 따로 보관 |
| 안정성 | 같은 segment의 연속 두 원문 앞부분 비교 | 확정한 오디오/원문 범위와 수정 가능한 tail 분리 |
| 번역 | 안정 원문 prefix 전체를 다시 번역, 마지막에 전체 확정 번역 | 의미 단위별 bounded 요청과 번역 tail 안정성 평가 |
| 오버레이 | 최근 카드 하나, 같은 segment의 임시 결과만 유지 | 읽는 카드와 새 draft를 최대 두 카드로 분리 |
| 실험 | 독립 prefix를 전사한 뒤 MOCK admission으로 재현 | 실제 native owner의 처리시간을 포함한 파일 기반 연속 입력 실험 |

현재 `submit_asr`은 admission마다 record revision을 바꾸고 `complete_asr`은
현재 key가 아닌 결과를 무시한다. 따라서 **추론보다 빠른 partial admission이
계속되면 완료한 결과가 반복 폐기될 수 있다.** 소스로 확인한 조건부 위험이며,
사용자가 관찰한 지연의 실제 원인이라고 확정한 것은 아니다. 기존 오프라인
비교는 ASR을 미리 완료했으므로 이 경합을 검증하지 않았다.

## 다음 구현 순서

1. **스케줄러와 계측:** 실행 중 snapshot과 최신 대기 범위 메타데이터를 구분한다.
   final 우선·취소 반환 대기·전체 key 검증은 유지한다. admission 간격과
   decode 시간, applied/ignored 비율을 기록한 뒤 부분 간격을 조정한다.
   오디오 캡처 없이 기존 파일을 실제 속도로 공급하는 방식부터 비교한다.
   첫 구현/CPU 파일 측정 완료: [결과와 한계](evidence/partial-scheduling-windows-20261001.md).
   기본 1초 간격은 유지하며 실제 VAD→HTTP 지연 측정은 별도다.
2. **자막 읽기 구조:** 이전 읽기 카드 + 현재 수정 중 draft를 최대 두 개로
   유지한다. 교체 최소 간격·글자 수에 따른 표시 시간·대기 상한을 두어
   무작정 오래 남긴 자막이 현재 음성과 멀어지는 일을 막는다.
   두 카드·4~10초 읽기·1.25초 일반 교체 제한 구현. 빌드 확인이며 화면/읽기
   실측은 미실행이다. [표시 계약](CAPTION_READING.md).
3. **문장/짧은 절 경계:** 안정 앞부분에서 번역 가능한 단위를 일찍 분리한다.
   뒤에 오는 부정·조건·숫자 때문에 의미가 바뀌면 draft를 수정한다.
   오디오 trim은 이미 수집하는 token 시간과 연결하며 문자 비율로 추정하지 않는다.
   텍스트 단위·중복 생략·후속 요청·수정/카드 이동 구현. [단위 계약](TRANSLATION_UNITS.md).
   실제 Qwen 파일 비교는 요청 감소와 오역을 확인했다. 첫 출력 가속은 없었다.
   token trim은 길이 0인 단어 시간으로 거부했으며 라이브 적용은 남아 있다.
   [실측](evidence/translation-units-trim-windows-20261001.md).
4. **모델 비교:** 위 경로에서 Whisper CPU/CUDA·SenseVoice, 4B/1.7B를
   독립 비교한다. 첫 자막·확정 자막 지연, 조건/부정 오역, 수정 횟수,
   읽기 전 교체 비율, backlog, 게임과의 자원 경합을 각각 기록한다.
   Whisper CPU/CUDA ASR 단독 paced 파일 비교 완료. CUDA 선택 실행을
   제공하되 Qwen 동시 경쟁·화면/품질과 새 후보 모델 비교는 남아 있다.
   [실측](evidence/partial-backends-windows-20261001.md).

LLM SSE 스트리밍은 이후 선택 사항이다. 현재 짧은 번역의 HTTP 평균이
약 0.12~0.13초인 관측 범위에서는, 전체 지연을 크게 줄일 첫 작업으로
판단하지 않는다. 작은 모델만 채택하거나 공개 sub-second 문구를 근거로
목표를 보장하지 않는다. 최신 후보 모델 조건은 [MODEL_CANDIDATES](MODEL_CANDIDATES.md),
기존 임시 번역의 품질 한계는 [STREAMING_TRANSLATION](STREAMING_TRANSLATION.md)을 따른다.
