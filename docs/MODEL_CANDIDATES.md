# ASR·번역 후보 검토 (2026-10-01, 후속 2026-10-02)

후속: 동의받은 SenseVoiceSmall INT8 CPU와 Whisper CUDA 파일 비교를 완료했다.
이번 구성에서 추론 가속은 없었다. Whisper 짧은 입력 패딩의 첫 원문 가속은
파일 실험에 한정되며 제품 번역 지연 개선은 미확인이다.
[실측 근거](evidence/asr-candidates-windows-20261002.md).
[설치·비교 절차](ASR_CANDIDATE_COMPARISON.md)를 따른다.
현재 빠른 실행 경로의 비교 기준은 Whisper base CUDA다.

최초 조사 당시 기본 구성은 CPU Whisper base + GPU llama.cpp의
Qwen3-4B-Instruct-2507 Q4_K_M이다. 아래 내용은 최초 조사 당시의 기록이다.
SenseVoice 외 후보는 다운로드·실측하지 않았다. 모델/runtime 확보 전에 용량·라이선스·고정 revision을
확인하고 저장소의 다운로드 동의 절차를 따른다.

## ASR

| 후보 | 확인한 특성 | 실험에서 확인할 점 |
|---|---|---|
| SenseVoiceSmall 234M | 비자동회귀, 중국어·광둥어·영어·일본어·한국어; ONNX export 경로 | Windows adapter, 작은 오디오 prefix의 정확도, CPU 처리시간, 취소/재시작 |
| Fun-ASR-Nano-2512 800M | 중국어·영어·일본어 | 한국어 요구에는 MLT checkpoint와 구분; autoregressive 생성 비용 |
| Fun-ASR-MLT-Nano-2512 800M | 한국어 포함 31개 언어 | Windows 실제 runtime, GPU 게임 경합, 부분 결과의 수정 빈도 |

FunASR toolkit과 Fun-ASR 모델 이름은 구분한다. 공개 streaming SDK가 있다는
사실만으로 현재 Windows Rust adapter에서 연속 오디오를 처리한다고 보지 않는다.
SenseVoice의 커뮤니티 pseudo-streaming도 정확도 손실이 명시돼 있다.
공개 Whisper small/large 비교 수치를 현재 base나 전체 자막 지연에 대입하지 않는다.
우선 SenseVoiceSmall을 속도 후보로, Fun-ASR MLT를 추가 품질 후보로 보는 것은
현재 요구와 실행 경로를 고려한 판단이며 실측 결론이 아니다.

출처: [SenseVoice 공식 저장소](https://github.com/QwenAudio/SenseVoice),
[Nano 모델 카드](https://huggingface.co/FunAudioLLM/Fun-ASR-Nano-2512),
[MLT 모델 카드](https://huggingface.co/FunAudioLLM/Fun-ASR-MLT-Nano-2512),
[Fun-ASR 공식 저장소](https://github.com/QwenAudio/Fun-ASR).

## 작은 번역 모델과 thinking

Qwen3 1.7B는 llama.cpp 비교 후보다. 파라미터 감소는 메모리·연산 감소를
기대하게 하지만 현재 장치의 속도/번역 품질은 아직 측정하지 않았다.
현재 4B-Instruct-2507과 일반 1.7B는 학습 버전도 다르다.
공식 1.7B GGUF 카드는 Q8_0을 제공한다. Q4_K_M 비교 파일을 정하면
배포자·revision·SHA-256을 별도로 고정한다.

실시간 번역 실험은 **thinking OFF**로 한다. 현재 4B-Instruct-2507은
비사고 전용 모델이라 별도의 끄기 변경이 필요 없다. 일반 Qwen3 1.7B는
기본 thinking이 켜지므로 지원 runtime의 chat template에서
`enable_thinking=false`를 명시하고 실제 출력/토큰 사용을 확인한다.
`/no_think`는 soft switch이며 hard switch와 같다고 가정하지 않는다.
사용 중인 llama build의 지원 여부도 실제 확인한다.

비교는 같은 서버·프롬프트·양자화 등급·문맥·출력 한도에서 시작한다.
sampling 튜닝은 별도 조건으로 기록한다. 영어/일본어→한국어에서
부정문·조건문·숫자·고유명사를 확인하고 HTTP 시간, 첫 자막 시간,
확정 자막 시간, 오역/누락, 메모리 사용을 각각 기록한다.
모델 축소와 임시 번역 활성화를 한꺼번에 바꾸지 않는다.

출처: [Qwen3 1.7B 공식 카드](https://huggingface.co/Qwen/Qwen3-1.7B),
[공식 GGUF](https://huggingface.co/Qwen/Qwen3-1.7B-GGUF),
[현재 4B-Instruct-2507](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507).
