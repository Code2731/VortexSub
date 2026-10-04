# P2.2 제한 후보 / P2.3 같은 입력 비교 — 2026-10-02

## 구현 범위

기본 off의 `마침표 갱신 묶기 · 실험` 체크박스를 라이브 설정에 추가했다.
세션 시작 전 변경하고 실행/일시정지 중에는 비활성화한다. 같은 원문/보호 prefix와
부분 번역 단위에서 번역 본문은 같고 단일 끝 마침표만 바뀌는 새 요청에 한정한다.
현재 draft의 기존 0.25초 교체 간격 대신 표시 시각 기준 0.75초까지 최신 하나를
묶으며, 이전 카드의 기존 1.25초 제한도 유지한다. 반복 후보가 기한을 연장하지 않는다.

단어·공백·말투·숫자·질문/감탄·말줄임표 변경은 이 추가 대기에서 제외한다.
첫 번역·원문 prefix 무효화·최종 전체 번역은 기존 우선 경로를 따른다.
두 번째 추론이나 target prefix 강제는 없다. 임의의 본문 수정이 같은 의미인지
판정할 근거가 없어 광범위한 보호 본문 동결은 구현하지 않았다.

[정책과 사용법](../COSMETIC_REVISION_POLICY.md).

## 실행

Windows RTX 3080 10 GB, 기존 Qwen/Hy-MT2 GGUF와 공통 b11146 runtime.
새 다운로드는 없다. 이전 고정 trace SHA-256:
`1672295c928469afcd3c329d50b848a6ea561d8122505bacceae3ebad0e909f7`.

```powershell
dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore
dotnet build tests/EchoSub.NativeAsrSmoke/EchoSub.NativeAsrSmoke.csproj --no-restore
dotnet build tests/EchoSub.ProtocolSmoke/EchoSub.ProtocolSmoke.csproj --no-restore
models/tabby/venv/Scripts/python.exe -X utf8 scripts/compare-fixed-translation.py --trace benchmarks/results/streaming-translation-20261001-192918-a74d88/source-trace.json --rounds 1
```

세 빌드 경고/오류 0, Python 구문과 diff 확인 성공. 단위·통합 테스트는 추가/실행하지
않았다. 결과는 Git 제외 `benchmarks/results/fixed-translation-20261002-184148-370f78/`.

5기록 × 두 모델 × 부분 ON/OFF 각 1회, 실제 worker/HTTP 20/20 최종 완료.
각 실행에서 동일 history와 동일 시각을 기존/후보 두 deck에 공급했다. 추론을
두 번 실행하지 않았다. 영어는 이전 CPU Whisper prefix, 일본어는 작성한 가설이며
MOCK ASR admission, 진단 polling 시계, 실제 Avalonia/화면 렌더링은 없다.

## 결과

* 20개 paired 실행의 첫 deck 적용 차이는 모두 **0초**.
* 기존/후보의 번역 카드 변화 timeline(시각·내용)은 20/20 동일.
* 기록된 번역 카드 상태 변화는 기존 35건/후보 35건. 첫 출력·단위 이동·제거도
  포함한 수치로 동일 문장의 수정 횟수가 아니다.
* `CosmeticDeferred`는 **0건**. 대상 마침표-only 수정이 이 trace에 없었다.
* 일본어 긍정→부정의 최종 교정은 두 deck에 같은 시각/내용으로 반영됐다.

따라서 기존 흐름이 이 자료에서 유지되는 것은 관찰했지만 **활성화된 대기 분기를
검증하거나 안정화 효과를 확인한 것은 아니다**. 새 의미 오류 감소·표시 안정성 향상·
속도 개선을 주장하지 않는다. 독립 의미 검토는 PENDING이다.

## 판단과 다음 작업

일반 수정 감소의 채택 기준을 통과하지 못했으므로 기본값은 유지한다. 체크박스는
제한 후보의 명시적인 실험 진입점이다. 더 넓은 본문 동결이나 강제 번역 prefix로
확대하지 않는다. 대기/반복 후보/중간 설정 변경 분기, 실제 UI 조작/화면/스크린샷,
macOS는 실행 미검증이다. 빌드 성공으로 대체하지 않는다.

P2.3의 paired 집계 경로는 마련했지만 후보 효과와 독립 의미 평가를 완료하지 않았다.
효과 없는 수정 정책 튜닝을 반복하기보다 다음 P3.1에서 번역 후보 모델·입력 profile을
명시적인 실험 선택 경로로 연결해 실제 사용에서 비교할 수 있게 준비한다.
기본 Qwen과 검증되지 않은 언어/모델의 구분은 유지한다.
