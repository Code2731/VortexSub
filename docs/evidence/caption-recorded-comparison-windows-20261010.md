# 보관한 엔진 기록의 표시기 비교 — Windows (2026-10-10)

## 범위와 변경

기록 재생 기본 경로를 MainWindow live와 같은 `SetLiveCards`로 연결했다.
`--caption-replay-legacy`는 같은 입력을 기존 표시기로 실행한다.
`compare-caption-replay.py`는 두 경로의 입력 해시·상태·PNG·픽셀·대기 사건을 비교한다.
통합 검사에 `-ReplayInput`을 추가했다. 입력 기록 없이 실행하는 기본 검사는 유지한다.
재생 보고서와 비교 결과 폴더는 덮어쓰지 않는다.

입력은 `paced-repair-v2-base-direction-20261003/report.json`과 small 대응 파일이다.
각 6회, 총 12회의 통제 음성 Whisper CUDA→Hy-MT2 실제 엔진 기록이다.
사용자 자연 음성의 본문 기록은 아니다. 새 전사·번역 추론은 실행하지 않았다.
원래 시각·session/epoch/segment·revision/request·preview/source/prefix를 유지한다.
product session 이름만 독립 재생용으로 바꾼다. 두 표시기는 같은 파일과 420 DIP 폭을 사용한다.
0.1초 Tick과 원래 이벤트 시각을 합친 가상 시계다. 실제 화면 연속 프레임은 아니다.

## 실행과 결과

저장소 루트에서 실행했다.

```powershell
& scripts/test-captions.ps1 -ReplayInput @('benchmarks/results/paced-repair-v2-base-direction-20261003/report.json', 'benchmarks/results/paced-repair-v2-small-direction-20261003/report.json')
```

최종 종료 코드 0. 결과는 `benchmarks/results/caption-regression-20261009T183742Z-c2ba4a5a/`다.
폴더 시각은 UTC이며 한국 실행 날짜는 2026-10-10이다.
`recorded-0/comparison.json`, `recorded-1/comparison.json`에 두 경로의 입력 해시와 결과를 보관한다.
Windows 11 Home x64 / Ryzen 7 5800X / RTX 3080 / .NET SDK 10.0.102.
.NET 10·복원한 `.nuget/packages`·기존 Python/Pillow를 사용했다. 다운로드는 없다.

| 입력 | 각 표시기 재생 | 각 표시기 상태 검사 | 각 표시기 PNG | 각 표시기 픽셀 검사 |
| --- | --- | --- | --- | --- |
| base | 6/6 PASS | 2,174 PASS | 87 | 472 PASS |
| small | 6/6 PASS | 2,177 PASS | 89 | 481 PASS |
| 합계 | 12/12 PASS | 4,351 PASS | 176 | 953 PASS |

최종 내용 전달·읽기 보호·고정 위치·중복 최종 표시·읽기 배출을 확인했다.
각 실행의 마지막 줄 종료 시각 차이는 모두 0초다. 이 자료에서 속도 개선 증거는 없다.
base의 종료 시각은 6.4~6.5초, small은 6.5~6.6초다. 음성 시작 기준 가상 시각이다.
FirstLine은 기존 표시기에서 단위 기준, 새 표시기에서 버전 기준이다.
base는 6개 대 12개, small은 9개 대 12개다. 백분위를 직접 속도 개선 수치로 비교하지 않는다.
최종 읽기 종료 시각은 성공한 같은 실행끼리 비교했다. 첫 자막 지연을 대신하지 않는다.

기존 계약 77개 상황 / 11,661 검사·상태 189개·고정 입력 16개·요약기 11개도 통과했다.
기존·live 화면은 각각 15개 상황 / PNG 85개 / 픽셀 398개다.
직접 후보·기록 어댑터 화면은 각각 6개 상황 / PNG 31개 / 픽셀 146개다.
모든 저장 화면 검사가 통과했다. 두 빌드는 경고 0 / 오류 0이다.

## 판정기 수정과 방어 검사

초기 base 비교의 새 표시기는 run 1의 최종 전달 판정에 실패했다.
표시된 앞문장과 뒤 문장 사이의 선행 공백은 SeparatorConsumed 이력으로 소비됐다.
기존 판정기는 이 공백의 글자 표시도 요구했다. 문장 내용 유실은 아니었다.
닫힌 동일 앞문장의 경계에서만 `TrimStart`를 적용했다. 교정 문장 내부 공백과 문자열은 그대로 비교한다.
수정 후 두 경로 12회와 전체 회귀를 재실행했다. 기존 실패 결과는 보존했다.

0개 run·25개 run·빈 event 입력은 종료 코드 1과 빈 화면으로 실패했다.
`benchmarks/results/caption-replay-input-guards-20261010/guards.json`에 3개 판정을 보관했다.
같은 재생 보고서 재실행은 종료 코드 2로 거부하고 해시를 유지했다.
최종 통합 보고서의 소스 해시는 현재 검증 소스와 일치했다.
보고서 설명의 이전 기본 경로 문구도 현재 live 경로에 맞게 정정했다.
`recorded-0/scheduled/run-00-frame-009.png`를 직접 열어 두 줄의 방향 정정 화면을 확인했다.

## 다음 작업과 제외 범위

이번 기록은 단일 발화의 짧은 방향 정정이다. 여러 발화의 대기·과부하 개선 판정에는 부족하다.
다음은 여러 발화·긴 문장·원문 정정·과부하가 겹치는 반복 기록 비교다.
새 추론·사용자 자연 음성·일반 의미 품질·OS 연속 프레임·실제 30분·macOS는 미검증이다.
Rust·IPC·전체 check 스크립트는 이번에 실행하지 않았다. 수동 재시험은 요청하지 않았다.
브랜치 `master`, HEAD `15f1db0`. 소스·검사·문서는 미커밋이다. 커밋·푸시·다른 PC 전달은 하지 않았다.
모델·음원·로그·PNG·전체 결과와 입력 기록은 Git 제외다. 다른 PC에 이 기록이 있다고 가정하지 않는다.
입력이 없으면 기본 자막 검사를 실행하고 기록 비교를 미실행으로 표시한다.
