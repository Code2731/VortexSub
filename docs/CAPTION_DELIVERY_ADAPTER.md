# 선택된 번역 기록 전달 어댑터

2026-10-10. `CaptionScheduleDelivery`의 계약이다.
[후보 배정기](CAPTION_SCHEDULED_READER.md)에 Deck가 선택한 번역 기록을 전달한다.
초기에는 진단 화면 경로에 연결했다. 후속으로 MainWindow live에 새 배정기를 연결했다.
현재 live의 기본 경로는 새 배정기이며 mock은 기존 표시기를 사용한다.
[현재 검증 범위](evidence/caption-native-recheck-windows-20261012.md)를 읽는다.

## 기록 선택과 식별자

`CaptionPresentation.DisplayedTranslationRecord`를 입력 근거로 사용한다.
최신 ASR 기록을 선택된 번역 기록으로 간주하지 않는다.
유효한 이전 preview를 유지한 카드의 ASR revision과 번역 revision은 다를 수 있다.
`CaptionScheduleInputs.FromRecord`가 선택된 기록의 적용 revision·원문 범위·번역 완료 상태를 검증한다.
Deck는 검증된 입력을 `CaptionCard.ScheduleInput`에 넣는다. 수신 시각은 전달 시 갱신한다.
입력 본문에 표시용 상태 접두어를 넣지 않는다. 기존 카드의 상태 접두어는 별도로 확인한다.

`CaptionCards.ScheduleContext`는 명시적인 product/session/epoch다.
새 context 또는 `ResetReading`이면 새 세션을 시작한다. context가 없는 중지 출력은 상태를 지운다.
식별자·본문 불일치와 입력 근거가 없는 옛 카드는 거부한다. 표시 문자열에서 식별자를 추측하지 않는다.
순서가 없는 카드 출력도 거부한다. 사유별 카운터는 포화 증가하며 본문을 보관하지 않는다.

## 전이와 진단

terminal invalidation은 해당 segment를 철회한다. source revision은 미표시 입력을 취소하고 읽기 기한을 유지한다.
같은 batch의 terminal invalidation이 source revision과 재전달보다 우선한다.
부분 원문 단위의 시작/끝 위치를 유지한다. 전체 final은 시작 위치 0으로 복원한다.
deck Tick과 만료는 새 입력을 만들지 않는다. 이미 배정한 입력은 읽기 정책으로 끝까지 처리한다.
중지·새 epoch는 이전 입력을 다시 표시하지 않는다.

배정기의 `FirstLine` 관측은 논리 줄 배정 대기다. 모델 추론·물리 화면 지연이 아니다.
`Dropped`에는 큐 사유와 미표시 문자 수를 전달한다.
어댑터의 입력 거부는 `Counters`, 배정 거부·퇴역·재사용은 저장소의 `Counters`에서 확인한다.
기본 경로 전환 전에 Blocked 구간·재사용·입력 거부의 통합 로그 대응도 마무리해야 한다.

## 자동 검증과 전환 판단

저장소 루트에서 `& scripts/test-captions.ps1`을 실행한다.
계약 검사에는 전달 어댑터 10개 상황이 추가됐다.
`adapter-render`는 합성 `HistoryRecord`를 실제 선택 정책·Deck·어댑터·오버레이로 전달한다.
`adapter-pixels`는 저장 화면을 검사한다. 실제 worker·음성·모델은 실행하지 않는다.
Desktop 진단 CLI는 `--caption-adapter-report <새 경로>`다.

`adapter-comparison`은 기존 16개 고정 입력의 golden과 비교한다.
16개가 모두 통과했다. 원문 수정·반복 수정·미표시 꼬리 폐기의 3개 차이를 해결했다.
`DeliveredLeadingPrefix` 증거는 같은 단위의 동일한 닫힌 앞문장만 소비한다.
변경된 뒤 문장은 읽기 보호가 끝난 뒤 배정한다. 임의의 뒷문장 재사용과 구분한다.
비교 CLI의 예상 종료 코드는 0이다. 알려진 차이를 허용하지 않는다.
MainWindow의 live 오버레이는 `SetLiveCards`로 새 배정기를 사용한다. 모의 표시와 기존 진단은 별도다.
`adapter_default_ready = true`는 고정 입력의 연결 검증이다. 실제 음성 합격은 아니다.

Blocked·BlockEnded·Reused·InputRejected가 통합 진단에 연결됐다. 같은 대기는 Tick마다 반복하지 않는다.
다음은 사용자 trace 재생 경로 연결과 같은 입력의 대기 비교다.
[최신 연결 근거](evidence/caption-live-delivery-windows-20261010.md)를 읽는다.
[앞문장 실행과 제외 범위](evidence/caption-leading-prefix-windows-20261010.md)를 읽는다.
이전 13개 통과 / 3개 차이는 [이전 근거](evidence/caption-delivery-adapter-windows-20261010.md)에 보관한다.
