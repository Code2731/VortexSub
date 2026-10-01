# ASR 후보 비교: SenseVoiceSmall INT8

## 목적과 현재 상태

Whisper base CUDA를 유지하며 SenseVoiceSmall INT8 CPU의 파일 전사와 반복 부분
전사를 비교한다. 2026-10-02 사용자 동의 후 고정 자산 다운로드·설치를 완료했다.
Fun-ASR 및 Qwen 1.7B 비교는 이 결과 뒤 진행한다.

[첫 측정 결과와 다음 구현](evidence/asr-candidates-windows-20261002.md):
이번 SenseVoice CPU 추론은 Whisper CUDA보다 느렸으며 제품 기본값은 유지한다.
짧은 Whisper 입력 패딩으로 첫 파일 원문이 앞당겨져 paced 번역 비교를 우선한다.

## 고정 자산과 설치

`benchmarks/asr-candidates.json`에 모델 revision, 파일 크기·SHA256과
sherpa-onnx/core 1.13.8 Windows x64 CPython 3.12 wheel을 고정했다.
합계 258,741,172 bytes(약 259 MB), 설치 후 공간은 추가로 필요하다.
모델은 [FunASR 별도 모델 라이선스](https://github.com/modelscope/FunASR/blob/main/MODEL_LICENSE),
런타임은 Apache-2.0이다. 모델을 제품에 배포하기 전 별도 라이선스 검토가 필요하다.

동의를 받은 뒤에만 다음 설치 명령을 실행한다.

```powershell
models/tabby/venv/Scripts/python.exe -X utf8 scripts/probe-sensevoice.py --download-approved
```

Git 제외 `models/asr-candidates/`에 해시 검증 후 내려받고, wheel을
`--no-index --no-deps --target`으로 별도 폴더에 설치한다. 기존 Tabby 패키지는
수정하지 않는다. 기존 Python의 NumPy를 사용하며 자동 다운로드는 없다.
기존 runtime 폴더가 있으면 설치를 거부하므로 실패한 설치는 먼저 점검한다.

## 비교 실행

```powershell
./scripts/probe-asr-candidates.ps1 -Rounds 3 -Threads 8
```

이번 RTX 3080 환경에서 `native` 자동 설정의 probe는 CC 5.2 커널 오류로
실패했다. 이 환경의 재현에는 `-CudaArchitecture 86`을 추가한다.
다른 GPU에 86을 그대로 적용하지 않는다. 이미 비교용 바이너리가 빌드되었다면
`-NoBuild`로 재사용한다. 제품 worker 실행 파일에는 이 변경을 적용하지 않았다.

같은 `benchmarks/fixtures/local-tts/manifest.json`의 영어·한국어 합성 PCM을 사용한다.
전체 전사와 0.8초부터 0.256초 간격의 누적 입력 전사를 실행하고 회차별 full/prefix
순서를 교대한다. 후보 간 순서는 SenseVoice 전체 실행 후 Whisper 전체 실행이다.
모델 로드·각 decode는 초로 기록한다. 전체 원문은 공백/구두점을 제거한
문자 오류율, 무음·비발화는 예상 밖 텍스트, 부분 결과는 기존 글자 삭제와 수정
횟수를 기록한다. SenseVoice ITN은 켜져 있다. 숫자 `10`과 `ten`/`십`은 문자
오류율에서 다르게 계산하므로 이 점을 결과 해석에 반영한다. 집계기는 두 후보의
fixture hash·회차·입력 경계를 확인하고 같은 NFKC 문자 정규화를 적용한다.
상세 결과는 Git 제외 `benchmarks/results/`에 저장한다. 입력별 양쪽 마지막 원문을
함께 점검하며 문자 오류율만으로 번역 품질을 판단하지 않는다.

## 해석과 다음 단계

이 모델/API는 [비스트리밍 전사](https://k2-fsa.github.io/sherpa/onnx/sense-voice/index.html)다.
부분 입력 반복을 진짜 스트리밍이라고 부르지 않는다. 순차 처리 완료 시각은
파일 재생 시뮬레이션이며 worker의 최신 후보 교체/VAD/번역/화면 지연을 포함하지 않는다.
공통 접두사 길이도 제품의 안정 구간 판정과 다르다. CPU 대 CUDA 비교는 실행 구성
비교이며 모델 자체의 우열이 아니다. INT8 품질과 자연 발화·일본어는 별도 확인한다.

첫 판단은 작은 합성 corpus의 속도/정확도/수정 빈도다. 유망하면 단일 owner와
취소·stale identity를 지키는 worker adapter를 연결해 같은 paced 번역 조건으로
재측정한다. 최종 채택은 번역 지연·품질과 게임 동시 부하까지 확인한 뒤 결정한다.
