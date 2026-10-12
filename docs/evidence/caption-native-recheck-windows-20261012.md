# 보관한 엔진 기록 12회 재비교 — Windows (2026-10-12)

## 범위

Whisper base·small CUDA→Hy-MT2의 보관 기록을 최신 live 배정기로 재생했다.
입력은 2026-10-03 통제 음성의 각 6회 기록이다. 사용자 자연 음성 자료가 아니다.
제품 배정 정책·추론 코드·검사 코드는 이번에 변경하지 않았다.
새 캡처·전사·번역·다운로드는 실행하지 않았다.
이 비교는 저장된 history events→Deck→live 어댑터→실제 Avalonia 저장 화면이다.
420 DIP 폭과 원래 이벤트 시각·0.1초 Tick을 사용한다. 물리 화면의 연속 프레임 검사는 아니다.

입력 SHA-256:

| 입력 | 크기 | SHA-256 |
| --- | --- | --- |
| `paced-repair-v2-base-direction-20261003/report.json` | 279,829바이트 | `08144f824e1ec23d5070045b8bfed5e3814fedcc87178893b528626e23505237` |
| `paced-repair-v2-small-direction-20261003/report.json` | 276,664바이트 | `710fb25b1fbb7791fdb934c9f41f28d713487ff2b0502c51fb929577d8a80736` |

두 파일은 이전 비교와 같은 해시다. 파일 경로는 `benchmarks/results/` 아래다.
product session만 독립 재생 이름으로 바꾸고 나머지 식별자·본문·시각은 유지한다.

## 실행과 결과

저장소 루트에서 실행했다.

```powershell
& scripts/test-captions.ps1 -ReplayInput @('benchmarks/results/paced-repair-v2-base-direction-20261003/report.json', 'benchmarks/results/paced-repair-v2-small-direction-20261003/report.json')
```

종료 코드 0. 결과는 `benchmarks/results/caption-regression-20261011T213312Z-9d89f2d7/`다.
UTC 폴더 시각이며 한국 실행 날짜는 2026-10-12다.
`recorded-0`은 기본 합성 겹침 검사, `recorded-1`은 base, `recorded-2`는 small이다.
각 `comparison.json`과 `prior-reader-comparison.json`에 비교 수치를 보관했다.
Windows 11 Home x64 / Ryzen 7 5800X / RTX 3080 / .NET SDK 10.0.102.
기존 Python/Pillow와 복원한 `.nuget/packages`를 사용했다. GPU 추론은 없다.

| 입력 | 각 표시기 실행 | 각 표시기 검사 | 각 표시기 PNG | 각 표시기 픽셀 검사 |
| --- | --- | --- | --- | --- |
| base | 6/6 PASS | 2,174 PASS | 87 | 472 PASS |
| small | 6/6 PASS | 2,177 PASS | 89 | 481 PASS |
| 합계 | 12/12 PASS | 4,351 PASS | 176 | 953 PASS |

최종 내용 전달·읽기 보호·고정 위치·중복 최종 표시·배출이 통과했다. 폐기는 없다.
기존 계약 84개 / 16,086 검사·상태 189개·고정 입력 16개·요약기 11개·시각 14개도 통과했다.
기존·live 화면은 각각 15개 / PNG 85개 / 픽셀 398개다.
후보·기록 어댑터 화면은 각각 9개 / PNG 47개 / 픽셀 220개다.
기본 합성 비교도 통과했다. 두 빌드는 경고 0 / 오류 0이다.
`adapter_default_ready=true`이며 소스 해시 23개가 현재 소스와 일치했다.

## 대기 판정

첫 발화의 최초 Done 수신→첫 줄 배정 대기 차이는 12회 모두 0초다.
마지막 읽기 줄 종료 시각 차이도 12회 모두 0초다.
base 종료 시각은 6.4~6.5초, small은 6.5~6.6초다. 기록 시작 기준 가상 시각이다.
2026-10-10 이전 비교의 새 표시기와 대조해도 12회 종료 시각이 모두 같다.
이 자료에서 속도 개선 증거는 없다. 최신 교정 기능의 회귀는 검사한 범위에서 발견하지 못했다.

입력의 연속 Done 결과는 모두 서로 다른 원문 revision이다.
`RetainedLowerCorrection` 진단은 두 입력에서 0건이다.
같은 원문 revision의 닫힌 뒷문장 유지 조건이 성립하지 않으므로 적용하지 않은 결과다.
합성 입력에서 측정한 2.7초→2.4초 개선을 이 엔진 기록의 개선으로 확대하지 않는다.

FirstLine은 기존 표시기에서 base 6개·small 9개, 새 표시기에서 각각 12개다.
단위와 버전의 집계 차이이므로 백분위를 직접 속도 비교로 사용하지 않는다.
이전 엔진 입력에는 `timing_targets`가 없다. 교정 문장별 최초 표시 지표는 생략했다.
이를 0초로 집계하지 않는다. 위 첫 발화 배정 대기는 음성→첫 자막 지연이 아니다.

## 화면과 다음 작업

`recorded-1/scheduled/run-00-frame-009.png`와 small 대응 PNG를 직접 열었다.
방향 정정 본문이 고정 두 줄에 보였다. 이 확인은 번역 의미 품질 평가가 아니다.

다음은 P0 요구·수용 기준과 현재 근거를 대조해 남은 작업을 정리하는 것이다.
기본 자동 회귀와 기록 재생의 통과를 자연 음성·장치·배포 gate 통과로 처리하지 않는다.
실제 의미 품질·지연·30분 E2E·macOS·패키징 검증은 남았다.
새 추론·자연 음성·OS 연속 프레임·실제 30분·Rust·IPC·전체 check는 이번에 미검증이다.
사용자 수동 재시험은 요청하지 않았다. 보류한 캡처 원인 조사는 재개하지 않았다.
브랜치 `master`, HEAD `15f1db0`. 이번 근거·인계 문서는 미커밋이며 다른 PC에 전달되지 않았다.
다른 PC에는 .NET 10·복원한 패키지·Python/Pillow가 필요하다.
기본 합성 입력은 소스에 있다. 위 엔진 입력은 Git 제외이므로 별도로 준비해야 한다.
모델·런타임 매니페스트·로컬 설정·음원·로그·PNG·전체 결과가 Git으로 전달된다고 가정하지 않는다.
