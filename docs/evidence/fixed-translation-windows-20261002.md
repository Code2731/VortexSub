# P1.3 고정 부분 전사 재생 — Windows, 2026-10-02

## 실행과 범위

Windows RTX 3080 10 GB, llama.cpp b11146을 두 모델에 공통 사용했다.
Qwen3 4B Instruct 2507 Q4_K_M / Hy-MT2 1.8B Q4_K_M을 하나씩 로드했다.
모델별 입력 형식에 동일 greedy 설정(T=0, top_k=1, top_p=1, min_p=0,
repeat_penalty=1, seed=42, max_tokens=256)을 적용했다. 앱 기본값은 바꾸지 않았다.

```powershell
cargo build -p echosub-worker -p echosub-translation --locked --offline
dotnet build tests/EchoSub.NativeAsrSmoke/EchoSub.NativeAsrSmoke.csproj --no-restore
models/tabby/venv/Scripts/python.exe -X utf8 scripts/compare-fixed-translation.py --trace benchmarks/results/streaming-translation-20261001-192918-a74d88/source-trace.json --rounds 3
```

Rust/C# 빌드 성공, C# 경고/오류 0. Python/PowerShell 구문과 diff 검사를 확인했다.
단위·통합 테스트 스위트는 실행하지 않았다. 위 실행은 요청받은 실제 비교 진단이다.

결과는 Git 제외 `benchmarks/results/fixed-translation-20261002-174149-d47223/`.
원문 trace SHA-256은 `1672295c928469afcd3c329d50b848a6ea561d8122505bacceae3ebad0e909f7`.
영어 3개는 이전 CPU Whisper의 독립 file-prefix 결과이며, 일본어 2개는 작성한
MOCK 가설이다. 자연 대화나 새 GPU ASR 측정이 아니다. 모델별 3회 × 5기록 ×
부분 번역 ON/OFF로 **60/60 최종 완료**, history 출력 snapshot 90개를 수집했다.
입력 예정 시각 대비 제출 지연 최대 0.019초 미만이었다.

생산 worker/core의 안정 구간·의미 단위·큐·취소·stale 적용을 사용했다.
실제 HTTP와 C# history IPC를 포함하며 MOCK ASR admission, 화면 렌더링 없음이다.
Rust request와 Python 비교 request의 일치를 24항목씩 확인했다. 첫 Qwen 회차만
동일 export 비교를 실행 후 추가 확인했고, 이후 회차는 서버 시작 전 확인했다.
Hy-MT2 template/tokenization 계약도 확인했다. 준비·warmup은 재생 시계에서 제외했다.

## 시간 — 각 3회 중앙값, 초

첫 출력은 기록 재생 시작부터 history 관찰까지다. 의미가 맞는 첫 자막이나
실제 음성→화면 시간으로 해석하지 않는다.

| 기록 | Qwen 최종만 / 부분 ON | Hy-MT2 최종만 / 부분 ON | 부분 출력 관찰 |
|---|---:|---:|---|
| 영어 짧은 길 안내 | 3.019 / 3.245 | 2.970 / 3.230 | 둘 다 0/3 |
| 영어 until 조건 | 3.928 / 4.018 | 3.961 / 3.966 | 둘 다 0/3 |
| 영어 3문장 연결 | 9.089 / 3.371 | 8.966 / 3.298 | 둘 다 3/3 |
| 일본어 돌아올 때까지 | 3.314 / 1.598 | 3.268 / 1.599 | 둘 다 3/3 |
| 일본어 긍정→부정 수정 | 3.267 / 1.622 | 3.211 / 1.569 | 둘 다 3/3 |

연결 영어의 모델 교체 효과는 첫 출력 약 0.073초이며, 부분 정책 효과는 Qwen
약 5.718초/Hy-MT2 약 5.668초다. 짧은 영어는 안정 구간 확보가 늦고 최종 전사가
다가와 부분 번역이 화면 관찰 가능한 결과로 남지 않았다. 일부 ON 조건이 OFF보다
늦었으며, 현재 자료로 세부 대기 원인이나 GPU 게임 경쟁을 단정하지 않는다.

## 의미와 수정 — 작성자 관찰, 독립 평가 미완료

* 영어 `until I return`: Qwen 최종 `나가기 전까지...` 6/6, Hy-MT2
  `내가 돌아올 때까지...` 6/6. ON/OFF × 3회 합계다.
* 일본어 `戻るまで...`: Qwen 조건 생략 6/6, Hy-MT2 조건 보존 6/6.
  이번 작은 기록의 결과이며 일반 번역 품질 통과를 의미하지 않는다.
* 일본어 긍정→부정 가설: 두 모델 모두 긍정 부분 출력을 보여준 뒤 최종 부정으로
  교정했다. 모델 교체가 원문 가설의 반전을 예방하지 못한다.

부분 ON의 UTF-16 제거 글자 중앙값은 연결 영어 Qwen 37/Hy-MT2 35,
일본어 조건 Qwen 3/Hy-MT2 10, 부정 수정 Qwen 7/Hy-MT2 4였다.
조건을 제대로 추가한 Hy-MT2가 더 많이 수정한 사례 때문에 **수정량이 작다고
품질이 좋은 것은 아니다**. 다음 의미 단위 교체와 final 전체 복원도 포함한 수치다.
같은 번역 단위 내부의 변경이나 실제 overlay churn으로 보고하지 않는다.

## 결론과 남은 작업

고정 trace 비교 경로와 반복 진단을 구현했다. 전체 독립 의미 평가와 paced
PCM→ASR→MT·자연 음성·게임 경쟁·macOS는 미검증이며 기본 모델 채택은 보류한다.
다음 P2.1에서는 같은 번역 단위 내부의 변경과 단위 전환/final 복원을 구분해
표시 유지 정책의 근거를 만든다. 필수 조건·부정 교정을 단순 동결로 막으면 안 된다.
