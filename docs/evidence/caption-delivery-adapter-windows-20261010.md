# 번역 기록 전달 어댑터 Windows 검증

검증일: 2026-10-10, Asia/Seoul. 결과 폴더 날짜는 UTC다.
브랜치 `master`, HEAD `15f1db0`. 이번 변경과 이전 작업은 미커밋이다.
커밋·푸시·다른 PC 전달은 하지 않았다.

## 구현

Deck가 선택한 `DisplayedTranslationRecord`를 `CaptionScheduleInput`으로 변환한다.
카드에 원문 범위·번역 revision/request를 전달한다. 상태 접두어에서 정보를 복원하지 않는다.
최신 ASR revision과 유지한 preview의 번역 revision을 구분한다.
`CaptionScheduleDelivery`가 명시적 세션·순서·식별자·본문을 검증한다.
source revision·terminal 실패·중지·새 epoch와 부분 단위의 전체 final 복원을 처리한다.
진단 오버레이에 연결했다. 기본 live/mock 경로는 기존 표시기를 사용한다.
직접 후보 입력·Deck 입력·기본 카드 입력의 혼용을 거부한다.

논리 첫 줄 대기와 큐 폐기 문자 수의 관측을 전달한다.
어댑터 거부와 저장소 거부·퇴역·재사용의 카운터를 분리한다.
Blocked 구간과 거부·재사용의 전체 로그 대응은 기본 전환 전 잔여 작업이다.

## 실행

구현 전에 기존 상태·화면·픽셀 회귀를 실행해 통과했다.
최종 명령은 저장소 루트 기준이다.

```powershell
& scripts/test-captions.ps1 -BaselineReport benchmarks/results/caption-regression-20261009T171504Z-fdc74342/report.json
```

결과: `benchmarks/results/caption-regression-20261009T172751Z-1460b730/`.

| 항목 | 결과 |
| --- | --- |
| 계약 합계 | 68개 상황 / 11,617 검사 통과 |
| 이번 어댑터 | 10개 상황 / 20개 검사 통과 |
| 기존 상태 | 189개 통과 |
| 기존 고정 입력 | 16개 상황 / 20,432 검사 통과 |
| 새 어댑터의 기존 golden 비교 | 13개 통과 / 3개 차이 |
| 타이밍 요약기 | 10개 통과 |
| 기존 렌더 | 15개 상황 / PNG 85개 / 픽셀 398개 통과 |
| 직접 후보 입력 렌더 | 5개 상황 / PNG 24개 / 픽셀 112개 통과 |
| 기록 어댑터 렌더 | 5개 상황 / 51개 검사 / PNG 24개 / 픽셀 112개 통과 |
| 기존 suite 대기 비교 | 기존 16개 호환 / 회귀 없음 |
| 검사 프로젝트·Desktop 빌드 | 경고 0 / 오류 0 |
| 통합 실행 | 종료 코드 0 |

전체 golden 호환을 통과했다고 표시하지 않는다.
비교 CLI는 예상 종료 코드 1이다. 통합 검사에서는 알려진 3개의 차이와 사유를 검증했다.
새 안전 오류나 다른 사례의 차이가 생기면 통합 검사는 실패한다.
`report.json`의 `adapter_default_ready`는 false다.

## 발견한 차이

| 기존 입력 | 새 결과 | 기존 기준과 차이 |
| --- | --- | --- |
| `source-revision-at-0.064s` | 2.75초에 윗줄 `기다려.`, 아랫줄 `오른쪽.` | 이미 읽은 앞문장을 재배정 |
| `repeated-source-revisions` | 2.75초에 윗줄 `기다려.`, 아랫줄 `멈춰.` | 이미 읽은 앞문장을 재배정 |
| `rollback-discards-unread-suffix` | 2.75초에 윗줄 `기다려.`, 아랫줄 `오른쪽.` | 전체 final 복원 시 앞부분을 재배정 |

첫 두 사례는 `required_target_once`도 실패한다. 세 사례의 줄 배정 golden이 다르다.
모든 비교 사례에서 조기 삭제는 0이었다. 순서·최종 내용·폐기 정책·대기 입력 소진·대기 상한 검사는 통과했다.
현재 계약은 revision 변경·겹침에서 전체 교정을 유지한다. 기존 표시기는 동일 접두어를 보존한다.
이 보수적 차이 때문에 기본 표시기 전환을 보류했다. 자연 음성 품질 문제가 해결됐다는 결과가 아니다.

## 근거와 제외 범위

`adapter-comparison.json`에 16개 전체 결과와 실패 항목을 보관한다.
`adapter-render/`는 합성 worker 기록 형식을 실제 선택 정책·Deck·어댑터·Avalonia 컨트롤로 전달한다.
실제 worker 프로세스·VAD·ASR·번역 모델을 실행한 결과가 아니다.
계약·비교·어댑터 화면 보고서의 동일 경로 재실행은 종료 코드 2로 거부했다.
SHA-256이 유지됐다. `overwrite-check.json`에 기록했다.

Windows 11 Home x64 / Ryzen 7 5800X / RTX 3080 / .NET SDK 10.0.102.
기존 Python/Pillow와 복원 패키지를 사용했다. GPU 추론·새 다운로드·개인 설정 변경은 없다.
결과·모델·캐시·로그·PNG는 Git 제외다. 소스도 아직 다른 PC로 전달하지 않았다.
Rust·IPC·전체 check 스크립트는 이번에 실행하지 않았다. 해당 소스는 변경하지 않았다.
실제 음성·의미 품질·OS 연속 프레임·실제 30분 gate·macOS는 미검증이다.
사용자 수동 재시험은 요청하지 않았다.

다음은 동일한 닫힌 앞문장의 실제 전달 증거 보존과 나머지 교정의 배정이다.
3개 차이와 기존 16개를 다시 비교한다. 그 뒤 기본 전환과 통합 진단 대응을 결정한다.
[어댑터 계약](../CAPTION_DELIVERY_ADAPTER.md)과 [인계](../HANDOFF.md)를 따른다.
