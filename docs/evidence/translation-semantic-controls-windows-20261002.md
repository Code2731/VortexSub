# 번역 의미·참조 문맥 통제 비교 (2026-10-02)

## 실행 범위

Windows, RTX 3080, 기존 Qwen3-4B-Instruct-2507 Q4_K_M/llama.cpp를 사용했다.
새 모델 다운로드·ASR·실제 캡처·UI 실행은 하지 않았다.
모델/runtime/요청 해시는 아래 Git 제외 결과 폴더의 runtime.json에 보존했다.

6개 작성 원문 × 문맥 없음/관련/무관 × 각 3회. 기존 baseline과 앱의 실험
문맥 분리 production은 108회, 일반 텍스트 plain 대조군은 54회 완료했다.
HTTP 응답 오류는 0회다. warmup 3회는 162회 집계에서 제외했다.
plain은 JSON뿐 아니라 언어 표기·지시도 바뀌므로 형식만의 인과 비교가 아니다.

```powershell
./scripts/probe-translation-context.ps1 -Rounds 3 -Fixtures benchmarks/translation-semantic-controls.json
./scripts/probe-translation-context.ps1 -Rounds 3 -Fixtures benchmarks/translation-semantic-controls.json -Profiles plain -NoBuild
```

결과: `benchmarks/results/translation-context-20261002-061836-f2f178/`와
`translation-context-20261002-061942-b9f902/`.
첫 sandbox 실행은 외부 WinGet 링크 접근 거부로 HTTP 전에 실패했다.
승인된 외부 런타임 접근으로 재실행했으며 다운로드는 없었다.

## 수동 의미 검토

| 항목 | 관측 | 판단 |
|---|---|---|
| Do not open the door until I return. | 세 방식·세 문맥 총 27/27에서 “나가기 전까지” | 귀환을 출발로 바꾸는 오류. 문맥 제거·plain으로 해결되지 않음 |
| Do not open the door before I return. | 총 27/27에서 “나가기 전/전까지” | before로 바꿔도 귀환 의미가 복구되지 않음 |
| Keep the door closed until I return. | baseline 9/9에서 “나가기”; production 관련 문맥 3/3은 “도어를 돌아올 때까지” | 일부 귀환 어휘 복구가 있으나 주체·문법이 부정확. 부정문에만 한정된 문제도 아님 |
| Attack only if the shield is down. | 조건 유지. 무관 문맥에서는 “방어막”이 “방어”로 바뀌거나 “떨어지면만” | 조건·어휘·문법을 구분해 평가해야 함 |
| Take two potions only if you have fewer than three hearts. | 모두 take를 마시다/먹다로 변경. 미만→이하 오류 baseline 4/9, production 1/9, plain 5/9 | 행동 변형은 남고 숫자 경계도 불안정 |
| 일본어 오른쪽이 아니라 왼쪽 문 지시 | 총 27/27 방향 부정·왼쪽 보존 | 이 단일 작성 문장 통과를 일본어 일반 품질로 확대하지 않음 |

HTTP 중앙값: baseline 0.1405초, production 0.1400초, plain 0.1100초.
원문 입력 이후 단독 서버 응답 시간이며 음성→화면 지연이 아니다.
plain의 짧은 요청과 지시 차이가 있어 엔진 성능 우위로 해석하지 않는다.

## 결정

품질 gate false. 기본 프롬프트 유지, 문맥 분리 실험 기본 off 유지.
이번 오류는 이전 문맥 혼입만으로 설명되지 않으며 JSON 전달만의 문제라는
가설도 지지하지 않는다. 현재 모델/양자화/runtime 조합의 한계 후보로 좁혔지만
세 요인을 분리하지 않았으므로 모델 자체의 확정적 원인이라고 단정하지 않는다.
다음은 같은 fixture에서 별도 번역 모델 후보를 비교한다. 새 모델은 실제 크기·
라이선스·실행 조건을 확인하고 다운로드 동의를 받은 뒤 진행한다.
Laya 도입 여부는 보류하며, 완전한 원문의 오역 해결책으로 간주하지 않는다.
