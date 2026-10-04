# Hy-MT2 비교 준비

2026-10-02, P1.1 / RI-01/06. 사용자 승인 후 모델/runtime 설치와 실제 비교를
완료했다. [입력·GPU·EOS·540응답 결과](evidence/hymt2-translation-windows-20261002.md).
후속 [공식 sampling 비교](evidence/hymt2-sampling-windows-20261002.md)도 완료했다.
앱의 기본 구성을 바꾸지 않는다. 아래 준비 단계의 미실행 표시는 최초 조사 당시 상태다.

## 고정 자산

공식 `tencent/Hy-MT2-1.8B-GGUF`의 Q4_K_M을 사용한다.
revision `a0c709d9fac510f2c807aa3af52872340dc37a4a`, 크기 1,133,080,448 bytes,
SHA-256 `dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699`.
[고정 공식 파일](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/blob/a0c709d9fac510f2c807aa3af52872340dc37a4a/Hy-MT2-1.8B-Q4_K_M.gguf)과
HF API 파일 metadata를 대조했다. 라이선스는 Apache-2.0이다.
`translation-research-models.json`에 크기/해시/path를 기록했지만 상태는 pending_consent다.

## runtime 호환성

공식 base config는 `HunYuanDenseV1ForCausalLM` / `hunyuan_v1_dense`다.
설치된 [llama.cpp 6047 소스](https://github.com/ggml-org/llama.cpp/blob/952a47f4/src/llama-arch.cpp)는
Hunyuan MoE만 등록하며 dense는 없다. 구조가 비슷하다는 이유로 다른 architecture로
강제 변환하지 않는다. 모델 로딩 성공은 아직 확인하지 않았다.

공식 안정 배포 `v0.5.0`의 `nightly-tag.txt`는 b11146을 가리킨다.
[b11146 소스](https://github.com/ggml-org/llama.cpp/blob/b11146/src/llama-arch.cpp)에는
`hunyuan-dense` 등록이 있다. [공식 CUDA 12.4 Windows x64 자산](https://github.com/ggml-org/llama.cpp/releases/tag/b11146)
두 ZIP의 정확한 크기/해시는 `translation-research-runtimes.json`에 기록했다.
합계 645,313,426 bytes이며 NVIDIA CUDA 구성요소에는 별도 재배포 조건이 있다.
모델과 합계 1,778,393,874 bytes다. 압축 해제 후 공간은 더 필요하다.

동의 후 `models/runtime-b11146/`에 별도로 준비하고 모델 입력/template/EOS와
CUDA 실제 로딩을 확인한다. 등록된 architecture만으로 실행 호환성을 확정하지 않는다.
Qwen과 Hy-MT2를 모두 이 runtime에서 실행해 모델과 runtime 효과를 섞지 않는다.

## 입력과 비교 경로

공식 [고정 model card](https://huggingface.co/tencent/Hy-MT2-1.8B/blob/9a341cd1b679d3efd23b46e847b01745a71ed792/README.md)의
기본 번역 지시와 background/source 형식을 사용한다. 단일 user 메시지이며 system은
추가하지 않는다. 대상 언어는 `Korean` 전체 이름을 쓴다. 공식 template에 없는
thinking-off 필드나 EXAONE 입력 보정은 적용하지 않는다.

`compare-exaone.py --candidate hymt2 --server <고정 llama-server.exe 경로>`로
동일 runtime의 Qwen baseline과 Hy-MT2를 하나씩 로드해 교대 비교할 수 있게 했다.
기존 EXAONE 명령은 기본값으로 유지한다. context-ablation은 EXAONE 전용이다.
공통 greedy를 먼저 쓰고 권장 sampling은 별도 축이다. 공식 권장은 temperature 0.7,
top_p 0.6, top_k 20, repetition_penalty 1.05, max_tokens 4096이며 실시간 비교의
256 출력 한도와 같다고 주장하지 않는다. 권장 구성 비교는 아직 구현/실행하지 않았다.

모델 없는 PrepareOnly로 90개 요청 export를 확인했다. 기존 regression/calibration
66항목과 EXAONE 때 만든 heldout 24항목을 재사용한다. Hy-MT2 결과를 보고 이 자료에
맞춰 수정하면 calibration으로 전환하고 새 heldout을 준비한다.
reference/판정 label은 후보 입력에 전달하지 않는다. 실제 template/BOS/EOS는 미검증이다.

동의·설치 후 사용할 명령(미실행):

```powershell
./models/tabby/venv/Scripts/python.exe -X utf8 scripts/compare-exaone.py --candidate hymt2 --server <models/runtime-b11146 내부 llama-server.exe의 실제 경로> --sampling greedy --fixtures benchmarks/translation-exaone-ablation-fixtures.json
```

실제 exe의 압축 내 위치는 설치 후 확인한다. 새 runtime의 API 변경·출력/종료 계약·
template fallback을 확인하지 않은 응답은 품질 비교에 포함하지 않는다.
전체 독립 수동 품질 판정·partial trace·앱·macOS는 남아 있다.
