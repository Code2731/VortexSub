# 현재 draft 표시 대기 개선 (2026-10-01)

## 변경

같은 카드의 일반 갱신을 모두 1.25초로 제한하던 규칙에서 현재 draft만
0.25초로 줄였다. 이전 읽기 카드와 확정 카드의 일반 갱신은 1.25초다.
첫 번역·확정·반박된 prefix·번역 무효화는 계속 즉시 반영한다.
각 카드의 대기는 최신 하나이며 text/epoch/identity 검증은 유지한다.
새 segment/번역 단위는 기존 두 카드 정책을 사용한다.

표시 전용 DispatcherTimer는 0.1초이며 기존 0.5초 IPC 조회와 분리했다.
변경 없는 카드 상태의 오버레이 속성 대입을 생략한다. 읽기 만료 4~10초,
이전 자리 이동 시 수명 유지·반복 조회로 만료 연장 금지·Pause/Stop clear는 유지한다.
UI thread가 계속 진행돼야 timer가 작동하며 hard deadline을 보장하지 않는다.

## 계측

런처 `-CaptionTiming`은 기본 꺼진 metadata 진단을 켠다.
실제 client event reader에서 번역 수신을, Deck에서 반영/대기 시간을,
visible Overlay에서 속성 대입을 기록한다. 원문·번역은 기록하지 않는다.
worker PID와 전체 source/request identity를 사용하며 retained preview는
새 pending revision이 아니라 실제 표시 중인 번역 record로 식별한다.

background writer의 대기는 128개다. UI/IPC reader에서 disk IO나 로그 대기를
하지 않으며 고갈 시 drop count를 남긴다. 정상 창 종료에서 최대 0.5초 drain한다.
`summarize-caption-timing.py`는 동일 ID의 첫 visible Deck 적용을 연결한다.
at_s는 공통 Stopwatch monotonic 초이며 날짜/음성 시점이 아니다.
속성 대입은 렌더 완료로 해석하지 않는다.

## 확인

Windows `scripts/check.ps1` PASS: Rust 153개, C# 표시 fixture 39개 assertion,
로컬 HTTP fixture·IPC·기본 빌드. 구문/형식·diff 확인을 수행했다.
출력은 Git 제외 `benchmarks/results/caption-display-check-20261001.log`에 보관한다.

- 0초 표시 후 0.1초에 같은 draft 수정: 0.2초에는 기존 내용,
  0.3초 Tick에서 새 내용. Deck 대기는 **0.2초**다.
- 같은 fixture에서 0.15초 반복 snapshot이 pending 최초 시점을 바꾸지 않는다.
- 이전 자리로 이동한 카드는 1.25초 보호를 유지한다. 이후 pending은 적용된다.
- 첫 번역/확정의 즉시 적용, 4초 최소 읽기/만료, 표시 tick으로 부활 금지,
  retained preview의 정확한 revision/request를 확인했다.
- client의 event 관측 hook을 추가해도 기존 event buffer/typed history 검사가 통과한다.

기존 1.25초 제한/0.5초 timer에서 같은 가상 도착 예를 tick으로만 처리하면
1.5초에 반영(도착 뒤 1.4초 대기)된다. 이는 이전 규칙으로 계산한 비교이며,
새 fixture의 0.2초 결과와 실제 UI 성능 차이를 측정한 것은 아니다.
첫 자막은 원래 즉시 적용 대상이므로 이 변경으로 첫 번역 추론이 빨라지지는 않는다.

이번에는 화면/게임 조작·live 캡처·실제 진단 로그 수집을 실행하지 않았다.
실제 received→Deck/overlay 지연과 읽기 편의 수용은 사용자 실행에서 확인한다.
빠른 draft 수정으로 시각적 수정 빈도가 늘 수 있다. 오역·후속 조건 문제는
번역/안정 판별 문제이며 표시 대기 단축으로 해결된 것으로 보지 않는다.
새 자산은 다운로드하지 않았다.

## 다음

사용자 실행의 metadata 로그로 수신→Deck 대기와 표시 규칙의 영향을 확인한다.
이후 미완성 조건/숫자 조각의 임시 번역 기준 및 기존 오역 사례 회귀를 다룬다.
자연/일본어 음성·게임 경합·macOS와 제품 품질 gate는 남아 있다.
