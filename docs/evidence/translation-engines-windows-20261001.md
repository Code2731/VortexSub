# Windows 번역 엔진 비교 — 2026-10-01

## 범위와 결정

Windows x64, RTX 3080 10240 MiB, 드라이버 617.14에서 실제 로컬 HTTP
번역을 비교했다. 현재 앱의 샘플러 설정에서는 기존 llama.cpp 구성이 더
빨랐으므로 `run-live.bat`을 기본으로 유지한다. `run-tabby.bat`은 선택형
TabbyAPI/ExLlamaV3 실행 경로다. 이 측정에는 VAD·ASR·UI 시간이 포함되지 않아
오디오→화면의 약 2초 지연이 해결됐다는 결론은 내리지 않는다.

## 모델·런타임과 재현

- llama-server build 6047 (`952a47f4`), 기존 WinGet TabbyML.Tabby CUDA124 배포.
  이는 이번에 설치한 TabbyAPI 서버와 다른 프로그램이다. GPU 37/37층 offload.
- 같은 Qwen3-4B-Instruct-2507 계열: GGUF Q4_K_M 대 EXL3 4.0bpw_H6.
  양자화·템플릿·샘플러 구현이 다르므로 배포 구성 비교이며 엔진만의 비교가 아니다.
- TabbyAPI revision `be74bf0a00bcb3a518e6feb7606f150c189be637`, Python 3.12.14,
  PyTorch 2.10.0+cu128, ExLlamaV3 1.5.3+cu128.torch2.10.0.
  revision·라이선스·wheel SHA는 `benchmarks/tabby-model.json`에 기록했다.
- 사용자 동의 뒤 Git 제외 `models/tabby/`에 다운로드·격리 설치했다.
  설치 목록은 `requirements-installed.txt`, 모델별 SHA는 `model-downloads.json`.
  초기 ExLlamaV3 1.5.2 native import가 Win32 오류로 실패했다. 1.5.3 및 대응
  Torch로 교체 후 native import·CUDA 사용·실제 추론이 성공했다. 최초 DLL 오류의
  내부 원인을 확정한 것은 아니다.

```powershell
./scripts/probe-translation-engines.ps1 -Rounds 3 -Warmup 1 -Worker -SamplingProfile current
./scripts/probe-translation-engines.ps1 -Rounds 3 -Warmup 1 -Order tabby-first -SamplingProfile current
./scripts/probe-translation-engines.ps1 -Rounds 3 -Warmup 1 -SamplingProfile untruncated
```

EN/JA 작성 문장 각 10개에 문맥 없음/이전 같은 언어 원문 최대 2개를 적용한
40개 corpus다. 엔진마다 40회 warmup을 제외하고 3라운드 120회씩 측정했다.
context 4096, 동시 요청 1, speculative draft 없음. production Rust HTTP owner를
사용하며 corpus·순서·model ID를 정규화한 요청 JSON SHA의 일치를 확인했다.
토큰화된 prompt까지 같음을 뜻하지는 않는다. 모든 측정 시간은 초다.

## 성능

현재 앱 설정: temperature 0.2, max_tokens 256, top-k 40, top-p 0.9,
min-p 0.1, repetition penalty 1. 설치된 llama-server 도움말의 기본값을 확인하고
Tabby에 같은 fallback을 지정했다. seed는 무작위이며 캐시 재사용을 허용한다.

| 조건 / 실행 순서 | llama 평균 / P95 | Tabby 평균 / P95 | Tabby 평균 절감 |
|---|---:|---:|---:|
| 현재 / llama 먼저 | 0.130975 / 0.174254 | 0.157779 / 0.198573 | -0.026804 |
| 현재 / Tabby 먼저 | 0.118728 / 0.148441 | 0.160002 / 0.206537 | -0.041273 |
| 후보 제한 해제 / Tabby 먼저 | 0.367370 / 0.491993 | 0.157398 / 0.208496 | +0.209972 |
| 후보 제한 해제 / llama 먼저 | 0.356889 / 0.475312 | 0.155368 / 0.196013 | +0.201521 |

후보 제한 해제는 top-k 0/top-p 1/min-p 0이다. 초기 약 2.3배 Tabby 우위는
이 조건에만 해당한다. 두 순서 모두 현재 설정에서 llama가 빨랐다. 짧은 고정
문장을 반복한 결과로 긴 발화·게임 부하에 일반화하지 않는다. 1초 GPU 샘플은
장치 전체 메모리이며 프로세스 peak가 아니다. 원본 경로·SHA·설정·전체 집계·
ready 시간·GPU 관측은 [요약 JSON](translation-engines-windows-20261001.json)에 보관했다.

## 품질과 통합 확인

현재 설정 첫 측정 라운드의 40쌍/80개 출력을 의미 중심으로 검토했다.
독립적인 이중 언어 수용 검사가 아니며 나머지 80쌍은 PENDING으로 유지했다.
[검토 결과](translation-engines-quality-20261001.json).

- 두 엔진: `Do not open the door until I return.`을 “나가기 전까지 …”로
  옮겨 귀환 조건을 출발 조건으로 바꿨다. 일본어 대응 사례에서는 조건이 빠졌다.
- Tabby: gate 사례에 `문门前`이 섞였고, `in ten seconds`를 “10초 안에”로 바꿨다.
- 두 엔진: shield를 일반 방어력으로 옮기거나 원문/문맥에 없는 차를 추가했다.
  응답 완결성이 품질 통과를 뜻하지 않으며 `quality_gate_passed=false`다.

두 엔진 각각 실제 CPU Whisper 파일 ASR→HTTP→history 검사를 통과했다:
영어 3개 Done, 한국어 1개 Bypassed. 현재 설정에서 Ping 최대 llama 0.000878초,
Tabby 0.001101초. 파일 전체 제공 뒤 terminal까지의 시간이며 live 자막 지연이 아니다.
`tool_calls:null`을 정상 빈 필드로 수용하고 비-null tool 응답은 계속 거부하도록
Rust/C# 회귀 fixture를 보완했다.

`scripts/check.ps1`: Rust 138개·포맷·양 언어 빌드·C# HTTP/IPC·표시 assertion
14개 PASS. native CPU/VAD worker release 빌드 PASS. llama 준비 확인에 `/health`를
추가해 모델 목록만 먼저 노출되는 로딩 상태를 Ready로 오인하지 않게 했다.
Tabby API/admin 로그 키 redaction 및 정상 종료 뒤 임시 인증 파일 제거를 확인했다.

새 런처의 실제 UI 조작/렌더링, 게임 공존·프레임 시간·프로세스 VRAM peak,
자연 음성·full audio→caption, macOS는 미검증이다. 다음 성능 작업은 단계별
live 지연 측정과 CPU ASR/CUDA 후보 비교다. 현재 live 런처의 Whisper는 CPU다.
