# P2.1 번역 변경 진단 — Windows, 2026-10-02

## 구현

앱과 기존 고정 replay가 `CaptionTargetChanges`를 공유한다. 최신 후보 하나와 두 카드의
각각 마지막 번역만 보존한다. 후보/카드 적용을 별도로 기록하고 session/epoch/segment,
revision/request·부분 단위 위치·첫 관찰/적용 시각·문자 접두부/교체량을 연결한다.
같은 요청 재조회와 역순 결과는 다시 세지 않는다. Clear/Pause/Stop과 새 context에서
진단 상태도 비운다. 번역 요청이나 표시 보호 시간을 조정하지 않는다.

앱의 기존 자막 timing 옵션에서 메타데이터만 기록한다. 원문/번역은 일반 로그에
넣지 않는다. 기존 bounded 큐와 dropped 보고를 사용한다. 단위 정보가 없는 구형
preview는 `UnidentifiedRevision`으로 분리한다. 종류와 시계의 범위는
[진단 문서](../TARGET_CHANGE_DIAGNOSTICS.md)에 명시했다.

## 실행

Windows RTX 3080 10 GB, 기존 Qwen/Hy-MT2 GGUF와 공통 b11146 runtime을 사용했다.
추가 다운로드는 없다. 이전 trace를 그대로 재사용했으며 source SHA-256은
`1672295c928469afcd3c329d50b848a6ea561d8122505bacceae3ebad0e909f7`다.

```powershell
dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore
dotnet build tests/EchoSub.NativeAsrSmoke/EchoSub.NativeAsrSmoke.csproj --no-restore
dotnet build tests/EchoSub.ProtocolSmoke/EchoSub.ProtocolSmoke.csproj --no-restore
models/tabby/venv/Scripts/python.exe -X utf8 scripts/compare-fixed-translation.py --trace benchmarks/results/streaming-translation-20261001-192918-a74d88/source-trace.json --rounds 1
```

세 C# 빌드 성공, 경고/오류 0. ProtocolSmoke 빌드에서 기존 공유 diagnostics의
`StartupDiagnostics` 참조 누락을 발견해 compile link를 보완한 후 성공했다.
단위·통합 테스트를 추가하거나 실행하지 않았다. Python 구문/diff 확인을 수행했다.

최종 binary 재생 결과는 Git 제외
`benchmarks/results/fixed-translation-20261002-180355-eabb30/`에 보존했다.
중간 구현의 20회 자료 `175933-2ddde8`은 최종 binary 결과와 합산하지 않았다.
최종 실행은 5기록 × 두 모델 × 부분 ON/OFF 각 1회, **20/20 최종 완료**다.
CPU Whisper로 이미 만든 영어 prefix와 작성한 일본어 가설을 MOCK admission으로
공급하며 실제 worker/HTTP/history IPC와 실제 두 카드 presenter를 사용했다.
polling 시계로 Update/Tick을 호출했으며 Avalonia·화면 렌더링·캡처는 없다.
모델마다 1회, Qwen 먼저이므로 속도/품질 우열의 새 반복 검증으로 해석하지 않는다.

## 진단 결과

후보 관찰 30건: Initial 20, UnitTransition 2, FinalRestoration 6,
SameUnitRevision 2. 카드 적용도 30건이지만 다음 단위는 새 카드 Initial이므로
종류별 수는 후보와 다르다. 원문 guard 무효화 관찰은 4건이었다.

| 부분 ON 기록 | 모델 | 기존 전체 제거 글자 | 동일 단위 제거 글자 | 나머지 변화 |
|---|---|---:|---:|---|
| 영어 3문장 연결 | Qwen | 37 | 0 | 다음 단위 + 전체 확정 복원 |
| 영어 3문장 연결 | Hy-MT2 | 35 | 0 | 다음 단위 + 전체 확정 복원 |
| 일본어 돌아올 때까지 | Qwen | 3 | 2 | 최종 복원 1글자 |
| 일본어 돌아올 때까지 | Hy-MT2 | 10 | 0 | 최종 복원 10글자 |
| 일본어 긍정→부정 수정 | Qwen | 7 | 0 | 최종 교정 7글자 |
| 일본어 긍정→부정 수정 | Hy-MT2 | 4 | 0 | 최종 교정 4글자 |

글자는 UTF-16 단위다. guard 무효화는 긍정→부정 수정 외에 일본어 조건의 문장
재배열에서도 발생했다. 의미 반박/오역 판정은 `UNASSESSED`와 검토 CSV의 PENDING
필드로 남겼다. 문자열 신호만으로 의미를 판단하지 않는다.

## 판단과 남은 범위

기존 전체 교체 수치가 일반적인 동일 단위 수정량을 과장할 수 있음을 확인했다.
이번 trace에서 일반 동일 단위 제거는 Qwen 2글자/Hy-MT2 0글자다. 큰 속도 개선이나
보편적인 읽기 안정성 개선의 근거가 아니다. 최종 조건/부정 교정을 보호 대상으로
동결하면 품질을 악화시킬 수 있다.

다음은 P2.2의 제한된 opt-in 수정 정책이다. 첫 번역·원문 guard 무효화·최종 전체
교정은 보존하고 일반 동일 단위 변경에 한정한다. 실제 사용자 화면 timing 수집,
독립 의미 평가, segment/session/역순/구형 metadata 분기의 실행 검증과 macOS는
남아 있다. 빌드 성공을 이 분기들의 실행 검증으로 간주하지 않는다.
