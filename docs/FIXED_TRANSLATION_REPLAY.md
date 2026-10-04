# 고정 부분 전사 번역 비교

P1.3은 모델마다 ASR을 다시 실행하지 않고 동일한 원문 수정 기록을 재생한다.
실제 worker의 안정 구간 판별, 의미 단위, 번역 큐, deadline/취소/identity 적용과
실제 로컬 HTTP 번역을 사용한다. ASR admission은 MOCK이며 캡처·화면 렌더링은 없다.

## 실행

이미 승인받아 설치한 Qwen/Hy-MT2와 `models/runtime-b11146/llama-server.exe`,
기존 Python 환경이 필요하다. 다운로드는 수행하지 않는다.

```powershell
scripts/compare-fixed-translation.ps1 -Trace <source-trace.json> -Rounds 3
```

worker·request exporter·기존 C# 진단 클라이언트만 빌드한다. 테스트 스위트를 실행하지
않는다. 각 모델 서버를 하나씩 소유해 시작하고 종료하며 모델 순서를 회차별로 반전한다.
두 모델은 동일 greedy 설정과 각 모델에 맞는 입력 형식을 사용한다. 앱 기본값은 유지한다.
CLI `--diagnostic-translation-profile qwen-greedy|hymt2-greedy`는 진단 전용이다.
Rust가 만든 요청과 기존 Python 비교 요청의 일치를 확인하고 Hy-MT2 입력 계약도 확인한다.

## 입력

```json
{"cases":[{"id":"example","language":"en","source_kind":"authored hypotheses",
"steps":[{"available_s":0.75,"source":"Do not open the door","final":false},
{"available_s":1.5,"source":"Do not open the door.","final":false},
{"available_s":2.25,"source":"Do not open the door until I return.","final":true}]}]}
```

파일은 1 MiB 이하, case/revision은 각각 1~100개, 시각은 0~60초에서 엄격히 증가하고
마지막 항목만 final이어야 한다. 실제 Whisper 기록과 작성한 가설을 `source_kind`로
구분한다. 기존 file-prefix 시각은 당시 ASR 소요시간을 포함한 추정치다.

## 결과 해석

Git 제외 `benchmarks/results/fixed-translation-*/`에 원본 복사·해시·runtime·요청·
회차별 기록·요약·모델을 숨긴 의미 검토 CSV를 보존한다. 첫 출력은 history에서 관찰한
시각이며 읽을 수 있는 의미 단위나 화면 출력 완료를 보증하지 않는다.
원문 도착 예정/실제 시각도 기록해 관찰 오차를 확인할 수 있다.

수정량은 이전 번역과 공통 접두부 이후 교체된 UTF-16 글자 수다. 첫 출력의 추가 글자는
포함하고 제거 글자는 0으로 계산한다. 다음 의미 단위로 넘어가는 교체와 final의 전체
문장 복원도 포함하므로 같은 번역 단위 내부의 교정량과 구분해야 한다.
이 수치는 오역률이나 overlay의 깜빡임이 아니다.
의미 오류·누락·미완성 문장 보완 여부는 CSV에서 별도 판정해야 한다.
P2.1부터 `target_observations`에 후보/카드 적용을 나눠 기록하며 동일 단위 교정과
다음 단위 전환/확정 복원을 분리한다. [분류와 제한](TARGET_CHANGE_DIAGNOSTICS.md).
P2.2 후보는 같은 history·시각으로 두 deck을 구동해 `cosmetic_policy`의 첫 적용 차이,
추가 대기와 표시 카드 snapshot을 기록한다. [정책 범위](COSMETIC_REVISION_POLICY.md).
기본 모델 전환, PCM→ASR→MT, GPU 경쟁, macOS 검증은 별도 단계다.
