# 설치된 번역 모델의 MTP 지원 검토

2026-10-09. Windows 로컬 파일과 공식 문서를 대조했다.
이번 작업은 지원 확인이다. 모델 다운로드, 런처 변경, 번역 추론 비교는 하지 않았다.

## 현재 실행 구성

`run-live-hymt2.bat`는 Hy-MT2를 선택한다.
`logs/translation-20261008-030630-27496.config.json`도 같은 선택을 기록한다.
모델은 `hy-mt2-1.8b-q4_k_m`, 입력 프로필은 `hymt2-greedy`다.
버전 기록은 `models/runtime-b11146/llama-server.exe`, build 11146 / commit `7fe450e19`다.
`scripts/run-live.ps1`에는 speculative decoding 실행 인수가 없다.
이번 확인은 해당 런처 구성과 기록을 대상으로 했다. 실행 중인 서버를 측정한 결과는 아니다.

## 로컬 파일 확인

기존 `models/tabby/venv/Scripts/python.exe -X utf8 -`로 읽기 전용 GGUF 헤더 검사를 수행했다.
키·배열 값은 건너뛰고 architecture, block_count와 전체 tensor 이름을 읽었다.
가중치 데이터를 추론에 로드하지 않았다. 명령 종료 코드 0.

| 설치된 파일 | Architecture | Tensor 수 | 기본 block 범위 | MTP 관련 이름 |
| --- | --- | ---: | --- | --- |
| `models/Hy-MT2-1.8B-Q4_K_M.gguf` | hunyuan-dense | 354 | 0~31 / 32개 | 없음 |
| `models/Qwen3-4B-Instruct-2507-Q4_K_M.gguf` | qwen3 | 398 | 0~35 / 36개 | 없음 |
| `models/EXAONE-3.5-2.4B-Instruct-Q4_K_M.gguf` | exaone | 274 | 0~29 / 30개 | 없음 |
| `models/translation-candidates/Qwen3-1.7B-Q8_0.gguf` | qwen3 | 310 | 0~27 / 28개 | 없음 |
| `models/translation-candidates/translategemma-4b-it.Q4_K_M.gguf` | gemma3 | 444 | 0~33 / 34개 | 없음 |

검사 대상 이름은 `mtp`, `nextn`, `next_n`, `enorm`, `hnorm`, `eh_proj`다.
MTP 메타데이터와 기본 범위를 넘어선 추가 block도 없었다.
공식 설정의 기본 architecture와 함께 판단하면 현재 파일에는 내장 MTP 실행 모듈이 없다.
이 결론은 설치된 파일에 대한 것이다. 같은 계열의 다른 버전이나 별도 학습된 draft까지 배제하지 않는다.

기존 서버의 `--help`도 Python subprocess로 실행했다. 제한은 20초였다. 종료 코드 0.
`--spec-type` 목록에 `draft-mtp`가 있다. 별도 draft 모델 옵션도 있다.
엔진의 MTP 기능과 현재 모델의 MTP 가중치 보유를 구분해야 한다.

## 공식 근거

- [Hy-MT2 1.8B 설정](https://huggingface.co/tencent/Hy-MT2-1.8B/blob/main/config.json): HunYuanDenseV1ForCausalLM, 기본 32개 층.
- [Qwen3 4B Instruct 2507 설정](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507/blob/main/config.json): Qwen3ForCausalLM, 기본 36개 층.
- [Qwen3 1.7B 설정](https://huggingface.co/Qwen/Qwen3-1.7B/blob/main/config.json): 기존 Qwen3 구성.
- [EXAONE 3.5 2.4B 설정](https://huggingface.co/LGAI-EXAONE/EXAONE-3.5-2.4B-Instruct/blob/main/config.json): ExaoneForCausalLM 구성.
- [llama.cpp speculative decoding 문서](https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md): `draft-mtp`는 본 모델의 MTP head를 사용한다. 별도 draft 및 n-gram 방식도 설명한다.

## 지연에 대한 판단

기존 030633 로그의 번역 처리 중앙값은 0.124초, P95는 0.328초다.
첫 줄 배정 대기 P95는 4.257초다. [기존 로그 분석](caption-early-clear-windows-20261008.md)을 따른다.
각 지표의 표본은 다르다. P95를 합산하거나 이번 결과로 갱신하지 않는다.
MTP는 번역 토큰 생성 구간을 줄일 수 있다. 원문 안정화와 읽기 슬롯 대기는 직접 줄이지 않는다.
현재 모델에 옵션만 추가해 MTP를 활성화할 수 없다. 전체 지연 해결책으로 확정하지 않는다.

## 다음 작업과 미검증 범위

우선 표시 이력·미표시 수정본의 입력 계약과 과부하 정책을 진행한다.
추론 가속을 별도로 비교하면 기존 고정 번역 입력으로 전체 요청 시간, 첫 토큰 시간,
생성 속도, draft 수락률과 출력 일치를 함께 확인한다. 짧은 번역에서 추가 연산이 이득인지 측정한다.
별도 draft 방식은 MTP와 구분한다. 현재 Hy-MT2와 호환되는 draft는 확인하지 않았다.
실제 가속률, ASR 동시 부하, 의미 품질, 화면·실제 음성·macOS 검증은 이번에 수행하지 않았다.
자막 코드는 변경하지 않았다. 자막 자동 검사도 다시 실행하지 않았다.
브랜치 `master`, HEAD `15f1db0`. 이번 문서와 기존 변경은 미커밋이며 다른 PC에 전달되지 않았다.
