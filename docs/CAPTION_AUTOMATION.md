# 자막 자동 검증

자막을 수정한 뒤 에이전트가 먼저 실행한다. 사용자에게 매번 재생과 관찰을 맡기지 않는다.
고정 입력을 같은 시각에 재생하고 상태·저장 화면·픽셀을 판정한다.
실패하면 원인을 수정하고 해당 검증을 다시 실행한다.

## 사용자 수동 확인 기준

2026-10-13 화면 복구 기하 189개를 계약 검사에 추가했다. 합계 85개 상황 / 16,275개 판정이다.
후보·어댑터는 각각 10개 상황 / PNG 52개 / 픽셀 244개다.
창 크기 알림의 화면 밖 복구 뒤 Run·읽기 기한을 확인한다. 실제 모니터 분리/DPI 시험은 별도다.
`source_hashes`는 위치 계산·기하 검사 파일을 포함한 25개다.
[최신 실행](evidence/overlay-placement-windows-20261013.md)을 읽는다. 아래 수치는 이전 단계다.

2026-10-12 기본 계약 검사는 84개 상황 / 16,086개 판정이다.
직접 배정기와 기록 어댑터에서 각각 300개 발화의 가상 1,800초 혼합 검사를 포함한다.
반복 교정·철회·중지 후 재개·context 전환과 이전 입력 거부를 확인한다.
직접 후보·어댑터 렌더는 각각 9개 상황 / PNG 47개 / 픽셀 220개다.
상태 반복과 저장 화면은 별도다. 실제 음성 30분 검증으로 집계하지 않는다.
[최신 실행 근거](evidence/caption-mixed-lifecycle-windows-20261012.md)를 읽는다.

사용자는 하루에 수십 번 테스트하지 않는다. 여러 변경을 묶어 요청 횟수를 최소화한다.
자동 검증을 통과한 의미 있는 변경에서 사람이 판단해야 할 문제가 남았을 때만 요청한다.
요청에는 자동으로 확인할 수 없는 이유, 자동 검증 결과, 구체적인 관찰 항목과 예상 시간을 적는다.
자동 검사 통과만으로 사용자 확인을 요청하지 않는다. 같은 조건의 중복 테스트를 요구하지 않는다.
증거가 부족한 결과는 미검증으로 기록한다. 확인되지 않은 체감 개선을 약속하지 않는다.

## 실행

저장소 루트에서 다음 명령을 실행한다.

```powershell
& scripts/test-captions.ps1
```

Windows 기본 실행은 다음을 포함한다.

1. .NET 빌드와 기존 줄·pipeline·전달 검사.
2. 동일 입력 16개 상황의 생산 `CaptionDeck`·`CaptionLines` 재생.
3. 예전 조기 삭제를 의도적으로 적용한 실패 검출 검사.
4. 로그 요약기 회귀 검사.
5. 실제 Avalonia 오버레이의 같은 입력 재생과 PNG 저장·픽셀 비교.

화면 실행 중 모의 오버레이가 잠시 나타난다. 개인 설정과 모델은 사용하지 않는다.
실행은 단계마다 제한 시간을 둔다. 시간 초과 시 해당 실행의 프로세스를 종료한다.
새 모델·패키지·런타임을 자동으로 내려받지 않는다.

## 확인할 문제

고정 입력은 0.064초 뒤의 원문 수정, 반복 수정, 수정 원문의 번역 확장,
단위 rollback, 다른 문장의 대기열 보존, 실패·폐기·건너뜀, 중지와 epoch 변경을 포함한다.
긴 번역의 꼬리, 분리자만 추가, 글자 추가, Unicode와 명시적 과부하도 검사한다.
600문장을 넣는 가상 30분 세션은 빠르게 재생한다. 실제 30분 음성 검증은 아니다.

각 상황은 읽기 기한, 줄 순서, 최종 교정, 누락·중복, 대기열 상한과 대기 시간을 판정한다.
기한 검사는 중지·세션 변경·해당 구간의 최종 실패 삭제를 별도로 허용한다.
예상하지 않은 누락이나 과부하 사유는 실패다. 과부하 상황에는 예상한 폐기를 명시한다.
문자열·픽셀 검사는 번역 의미가 맞다는 판정이 아니다.

## 보고서

실행마다 새 `benchmarks/results/caption-regression-*/` 폴더를 만든다.
`report.md`는 결과 요약이다. `report.json`은 단계·소스 해시·환경·실패 조건을 담는다.
`state.json`, `negative-control.json`, `render/report.json`, PNG와 픽셀 결과를 보관한다.
실패 시에도 시각·조건을 기록한다. 보고서가 없으면 통과로 처리하지 않는다.
기존 출력 폴더는 덮어쓰지 않는다. 출력 폴더명과 JSON 시각은 UTC다.

```powershell
# 같은 입력의 이전 결과와 비교한다.
& scripts/test-captions.ps1 -BaselineReport benchmarks/results/<이전 실행>/report.json

# 화면을 사용할 수 없을 때 상태만 검사한다.
& scripts/test-captions.ps1 -SkipRender
```

같은 suite 해시·운영체제만 비교한다. 가상 첫 줄 최대 대기가 0.1초보다 많이 증가하면 실패다.
서로 다른 입력의 보고서는 비교하지 않는다. `-SkipRender`는 화면 검사 제외를 보고서에 표시한다.
완료 판정은 선택한 검증의 범위에 한정한다. 실패 시 종료 코드는 1이다.

## 도구와 다른 PC

.NET 10 SDK, Python 3.10 이상, 렌더 픽셀 검사에는 Pillow가 필요하다.
이미 복원한 .NET 패키지를 사용한다. 먼저 Desktop과 ProtocolSmoke의 패키지를 준비한다.
PowerShell 래퍼는 기존 `models/tabby/venv/Scripts/python.exe`를 우선 사용한다.
없으면 PATH의 Python을 사용한다. 모델 폴더나 그 Python이 Git으로 전달된다고 가정하지 않는다.
직접 실행은 `python scripts/test-captions.py`다. Windows 외 기본 실행은 상태만 검사한다.
macOS 실기기·렌더 검증이 완료됐다는 뜻은 아니다.

소스가 전달되면 모델 없이 재현할 수 있다. 결과·PNG·로그는 Git 제외다.
이 자동화가 실제 음성·추론 지연·OS 합성기의 연속 깜빡임·사람의 읽기 편의를 대신하지는 않는다.
사용자 확인은 자동 판정하기 어려운 의미 품질과 최종 사용성 평가에 집중한다.

## 표시 입력 계약 검사

통합 실행의 `input-contract-checks` 단계는 `CaptionScheduleStore`를 직접 검사한다.
`contract.json`에 기존 계약 21개·문장 재사용 23개·후보 배정기 14개·전달 어댑터 10개 상황의 결과를 보관한다.
입력·범위·버전·토큰·읽기 보호·상한·퇴역과 실제 이력 기반 재사용 증거를 확인한다.
실제 표시하지 않은 범위의 완료 처리와 전체 final 복원 중 다른 발화의 입력 삭제를 거부한다.
후보 배정기는 새 저장소를 사용한다. 기본 live/mock은 기존 표시기를 사용한다.
독립 계약 검사는 화면을 실행하지 않는다.

독립 실행에는 .NET 빌드와 캐시가 필요하다. 새 출력 파일을 사용한다.

```powershell
$env:NUGET_PACKAGES = Join-Path $PWD '.nuget/packages'
dotnet build tests/EchoSub.ProtocolSmoke/EchoSub.ProtocolSmoke.csproj --no-restore
dotnet tests/EchoSub.ProtocolSmoke/bin/Debug/net10.0/EchoSub.ProtocolSmoke.dll --caption-contract benchmarks/results/<새 실행>/contract.json
```

기존 파일이면 종료 코드 2로 거부한다. 정상 계약 검사가 실패하면 종료 코드 1이다.
모델을 사용하지 않는다. 저장 예산은 문자열 payload이며 프로세스 메모리 측정은 아니다.
[계약](CAPTION_INPUT_CONTRACT.md)과 [2026-10-09 검증](evidence/caption-input-contract-windows-20261009.md)을 읽는다.
[재사용 계약](CAPTION_SENTENCE_REUSE.md)과 [2026-10-10 검증](evidence/caption-sentence-reuse-windows-20261010.md)을 함께 읽는다.

## 후보 배정기 화면 검사

통합 실행의 `candidate-render`는 실제 오버레이에 진단 입력을 전달한다.
`candidate-render/report.json`과 PNG 24개에서 읽기 보호·교정·재사용·append·철회·중지를 확인한다.
`candidate-pixels`는 같은 컨트롤 영역의 고정 위치와 기존 글자 픽셀을 검사한다.
후보 5개 상황과 기존 제품 렌더 15개 상황을 구분한다. 기본 실행 경로 전환 검사는 아직 남아 있다.
`-SkipRender`이면 두 화면 경로를 실행하지 않는다. .NET 10·복원 패키지와 Python/Pillow가 필요하다.
후보 Desktop CLI는 `--caption-schedule-report <새 경로>`다. 기존 보고서면 종료 코드 2로 거부한다.
자연 음성·모델 추론·OS 합성기의 연속 프레임은 포함하지 않는다.
[배정기 계약](CAPTION_SCHEDULED_READER.md)과 [이번 근거](evidence/caption-scheduled-reader-windows-20261010.md)를 읽는다.

## 기록 전달 어댑터 검사

`adapter-render`는 합성 `HistoryRecord`를 실제 선택 정책·Deck·어댑터·오버레이로 전달한다.
`adapter-pixels`는 PNG와 기존 글자 유지·고정 슬롯을 검사한다. 실제 worker·음성·추론은 실행하지 않는다.
`adapter-render/report.json`과 픽셀 보고서는 직접 후보 입력의 `candidate-render/`와 구분한다.
Desktop CLI는 `--caption-adapter-report <새 경로>`다.

`adapter-comparison`은 기존 16개 golden 전체 호환을 요구한다.
CLI 예상 종료 코드는 0이다. 실패·조기 삭제·사례 누락은 통합 검사 실패다.
`adapter_default_ready = true`는 같은 입력의 `live-render` 연결 검사도 통과했다는 뜻이다.
화면 검사를 생략하면 이 값은 false다. 실제 음성 합격으로 해석하지 않는다.
앞문장 증거 검사 6개 상황을 추가했다. 통합 진단 검사 3개를 포함한 계약 합계는 77개 상황 / 11,661 검사다.
직접 후보·어댑터 렌더는 각각 6개 상황·PNG 31개·픽셀 146개를 검사한다.
[실행 근거](evidence/caption-leading-prefix-windows-20261010.md)를 읽는다.

## 정책 비교 시뮬레이션

제품 변경 전 후보를 비교할 때 `& scripts/simulate-caption-policies.ps1`을 실행한다.
22개 고정 입력에 현재 `CaptionLines`와 문장 재사용·꼬리 합의·끝부분 보류를 적용한다.
입력은 Deck 뒤의 번역 전달 경계다. 원문 안정화나 모델의 추론 시간을 재현하지 않는다.
줄 폭은 32개 문자소, 시계 간격은 0.1초다. 후보 화면은 렌더하지 않는다.
생성된 `caption-simulation-*/simulation.json`의 입력·출력·해시·후보 판정을 확인한다.
`simulation.md`는 비교 요약, `execution.json`은 Git·SDK·CPU 기록이다.
실행마다 새 폴더를 사용한다. .NET 10과 복원한 패키지가 필요하며 모델/Python은 사용하지 않는다.

상태 판정 통과와 지연 채택 판정은 별개다. 후보의 기능 실패도 보고서에 남긴다.
고장 대조군을 검출하면 실행 종료 코드는 0일 수 있다. 이것은 모든 후보의 채택 승인이 아니다.
`selection`의 기능 판정과 지연 증가 사례를 함께 확인한다.
과부하의 `missing`과 `accounted_unshown`은 실제 미표시 내용이다. 무누락으로 집계하지 않는다.
같은 문장의 논리 재배정은 화면 깜빡임과 구분한다.
[2026-10-09 실행 근거](evidence/caption-policy-simulation-windows-20261009.md)를 따른다.


## live 경로 연결 검사 (2026-10-10)

`live-render`는 MainWindow와 같은 `SetLiveCards`를 사용한다.
기존 golden 15개 상황을 실제 오버레이에서 실행한다. PNG 85개·픽셀 398개를 검사한다.
`--caption-live-regression-report <새 경로>`로 개별 실행할 수 있다.
진단 파일 `live-render/timing.jsonl`에서 대기·종료·재사용·거부 사건을 확인한다.
기록 누락·쓰기 오류·본문 필드 유입은 실패다. 요약기는 Reused·InputRejected도 집계한다.
실제 worker·음성·모델은 실행하지 않는다. [최신 근거](evidence/caption-live-delivery-windows-20261010.md)를 읽는다.


## 보관한 엔진 기록의 두 표시기 비교 (2026-10-10)

2026-10-12 최신 배정기로 base·small 각 6회를 다시 비교했다.
각 표시기 4,351 검사 / PNG 176개 / 픽셀 953개가 통과했다.
첫 발화 배정 대기·종료 시각 차이는 12회 모두 0초다. 교정 문장 시각은 입력에 지정되지 않아 생략했다.
같은 revision 뒷문장 유지 조건은 이 입력에서 적용되지 않았다.
[최신 재비교 결과](evidence/caption-native-recheck-windows-20261012.md)를 읽는다.

`& scripts/test-captions.ps1 -ReplayInput @('<기록1.json>', '<기록2.json>')`로 실행한다.
`-ReplayInput`은 Windows 저장 화면 검사가 필요하다. `-SkipRender`와 함께 사용하지 않는다.
각 입력은 `runs`·history events·`final_record`가 있는 기존 native probe 결과다.
단독 비교는 `python scripts/compare-caption-replay.py <입력.json> <새 결과 폴더>`다.
재생 기본 경로는 새 live 표시기다. `--caption-replay-legacy`는 기존 표시기 비교용이다.
입력 해시·상태·PNG·픽셀·대기 사건과 실행별 마지막 줄 종료 시각을 보관한다.
FirstLine의 집계 단위가 달라 두 표시기의 백분위를 직접 속도 비교로 사용하지 않는다.
줄 경계에서 소비한 선행 공백은 보이지 않는다. 최종 교정 문장 내부 문자열은 정확히 비교한다.
입력·결과·PNG는 Git 제외다. 입력이 없는 PC는 기록 비교를 미실행으로 기록한다.
[12회 비교와 제외 범위](evidence/caption-recorded-comparison-windows-20261010.md)를 읽는다.


## 기본 합성 겹침 검사 (2026-10-10)

Windows 기본 실행은 `benchmarks/fixtures/caption-overlap-replay.json`의 6개 상황을 두 표시기로 비교한다.
합성 입력은 Git 대상이다. 음원·모델·과거 native 결과 파일은 필요하지 않다.
필수 문장 조각은 `required_fragments`의 minimum/maximum 횟수로 검사한다.
`expected_drops`로 허용한 폐기 수를 검사한다. 과부하 외의 5개 상황은 0건이다.
과부하는 3·4번 발화 폐기 2건을 허용한다. 1·2·5·6·7·8번 발화는 한 번씩 표시해야 한다.
`paired_first_visible_segments`는 같은 발화의 최초 Done 입력→첫 줄 배정 대기를 비교한다.
교정 조각의 최초 표시 증거는 아래 `paired_fragment_timings`로 구분한다.
기본 비교는 `recorded-0/`다. 추가 `-ReplayInput`은 `recorded-1/`부터 보관한다.
Windows 기본 실행 시간이 늘었다. `-SkipRender`는 이 비교도 생략하며 전체 표시 합격으로 표시하지 않는다.
[6개 상황의 효과와 한계](evidence/caption-overlap-comparison-windows-20261010.md)를 읽는다.

## 교정 조각 시각 (2026-10-10)

같은 아랫문장을 유지한 앞문장 교정의 화면 상황을 추가했다.
직접 후보·기록 어댑터는 각각 7개 상황 / 81개 검사 / PNG 37개 / 픽셀 175개다.
기존 아랫줄 Run·lease와 새 윗줄의 2.5초 보호를 확인한다.
계약은 82개 / 13,183 검사다. 가상 300개 교정의 1,800초 반복을 포함한다.
실제 음성 30분과 구분한다. [이번 실행](evidence/caption-retained-lower-windows-20261010.md)을 읽는다.

합성 입력의 `timing_targets`는 세션·epoch·발화·source revision·request ID와 정확한 `text`를 지정한다.
`unit_start`는 화면 부분 단위 시작 위치다. 기본값은 0이며, 임의로 원문 위치를 추측하지 않는다.
각 조각은 해당 입력 본문에서 정확히 한 번 나타나야 한다. 한 저장 줄 안의 조각만 측정한다.
`recorded-0/comparison.json`의 `paired_fragment_timings`에서 두 표시기의 상태·대기를 비교한다.
입력 직전 이미 표시된 조각은 `already_visible`이며 대기는 null이다. 새 버전 표시로 세지 않는다.
이전 전달·후속 버전 선도착·미표시도 null을 유지한다. 0초로 집계하지 않는다.
문자열 일치는 의미 품질이나 실제 렌더 request 소유권의 증거가 아니다.
버전 지정이 없는 예전 native 입력은 이 측정을 생략한다.
시각 검사 14개는 기본 검사에 포함한다. [측정 결과와 남은 대기](evidence/caption-fragment-timing-windows-20261010.md)를 읽는다.
