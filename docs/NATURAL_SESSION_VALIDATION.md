# 자연 음성 30분 검증 준비

2026-10-05. 로드맵 1단계의 Windows 개발판 검증이다. v1.0 기본 설정 확정은 아니다.

## 고정할 실행 조건

- Whisper base / CUDA / 빠른 부분 전사 스케줄러.
- Hy-MT2 Q4_K_M / b11146 / `hymt2-greedy`.
- 부분 전사·안정된 부분 먼저 번역·마침표 갱신 묶기: 켜짐.
- 원문 표시: 꺼짐. 폭 760, 불투명도 0.75. 입력 언어 en 또는 ja를 실행별로 고정.
- decode window·짧은 패딩·supported preview·isolated context: 꺼짐.
- 자막 지연 기록: 켜짐. 게임 동시 실행은 이 기준선에 포함하지 않는다.

현재 앱 기본값과 다른 **평가용 프로필**이다. 모델 비교 중 다른 옵션을 바꾸지 않는다.
설정은 저장·복원되므로 실행 인수만으로 위 UI 옵션이 적용됐다고 판단하지 않는다.

## 입력 자료

관리 중인 자연 음원 manifest는 아직 비어 있다. 합성 자료를 자연 음성으로 표시하지 않는다.
사용자가 제공한 파일 또는 평소 재생하는 영상의 위치·언어·구간·출처를 기록한다.
30분 이상 재생할 자료를 준비한다. 파일은 해시를 기록하고 영상은 URL과 재생 구간을 기록한다.
`DeclaredDurationSeconds`는 사용자 신고값이다. 실제 재생 길이는 따로 확인한다.
검증 음성을 앱이 자동으로 녹음하거나 저장하지 않는다. 원본과 상세 결과는 Git 제외다.
언어별 의미 평가에는 정답 구간·원문·검토문을 갖춘 별도 50문장 자료가 필요하다.

현재 사용자 지정 자료는 [George Hotz의 영상](https://www.youtube.com/watch?v=lNGFAI7R0PE)이다.
제목: `George Hotz | Exploring | Tenstorrent Blackhole on Arch Linux | tinycorp.myshopify.com | Part 2`.
YouTube 플레이어에서 전체 길이 4:48:22(17,302초)를 확인했다.
영어 평가용으로 00:10:00~00:40:00을 후보로 기록했다. 실제 발화량·언어·광고는 아직 확인하지 않았다.
영상 다운로드나 대본 확보는 수행하지 않았다. 페이지에는 자막 사용 불가로 표시됐다.
이 자료만으로 50문장 정답 품질 평가가 준비됐다고 판단하지 않는다.

## 준비 명령

```powershell
# 설치 자산과 저장 옵션을 읽기만 한다. 음원을 아직 지정하지 않아도 실행 가능하다.
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-natural-session.ps1 -Language en
# 자료 확보 후 같은 명령에 -AudioPath <파일> 또는 -SourceUrl <영상URL>을 추가한다.
# 알려진 길이는 -DeclaredDurationSeconds 1800처럼 기록한다.
# 기본 시작은 600초다. 1,800초짜리 파일이면 -SourceStartSeconds 0을 지정한다.
```

현재 후보를 준비한 실제 명령:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-natural-session.ps1 -Language en -SourceUrl 'https://www.youtube.com/watch?v=lNGFAI7R0PE' -DeclaredDurationSeconds 17302 -SourceStartSeconds 600 -OutputDirectory benchmarks/results/natural-preparation-20261005
```

준비 실행은 종료 코드 0이었다. 모델/VAD/ONNX 해시와 실행 파일 존재를 확인했다.
저장 옵션과의 차이는 `CosmeticRevisions` 하나였다. 현재 꺼짐이며 평가 프로필은 켜짐이다.
실제 세션 전 설정 화면에서 마침표 갱신 묶기를 켜고 적용 상태를 확인한다.
개인 설정은 변경하지 않았다. 30분 검증은 아직 실행하지 않았다.

`benchmarks/results/natural-preparation-*/preparation.json`을 확인한다.
모델·VAD·ONNX 해시, 실행 파일 존재·해시, Git 상태와 필요한 UI 설정 변경을 기록한다.
파일 준비 성공은 실제 캡처·추론 성공이나 소스와 실행 파일의 일치를 뜻하지 않는다.
소스를 바꿨으면 실행 전에 최신 앱·native worker를 빌드한다.

## 실제 실행 순서

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-live.ps1 -TranslationModel hymt2 -AsrBackend cuda -FastPartials -CaptionTiming
```

1. 서버·모델 조회 후 실제 모델 ID와 적용 프로필을 확인한다.
2. 세션 시작 전에 위 UI 옵션과 출력 장치를 확인한다. 화면과 시작 시각을 기록한다.
3. 짧은 준비 구간으로 모델을 준비한다. 측정 세션은 새로 시작한다.
4. 같은 자료를 30분 재생한다. 자막 중단·원문 철회·빈 화면·늦은 수정·읽기 문제의 시각을 기록한다.
5. 세션을 종료한다. 필요하면 TXT/원문 SRT를 직접 내보낸다.
6. 메인과 번역 서버가 정리됐는지 확인한다. timing 로그·실행 설정·관측 메모를 같은 결과 폴더에 보관한다.

## 판정과 한계

### 자동 관찰 진입점 (2026-10-06)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-live.ps1 -TranslationModel hymt2 -AsrBackend cuda -FastPartials -CaptionTiming -NaturalSessionReport benchmarks/results/natural-session-next/observation.json -NaturalSessionSeconds 1800
```

설치된 모델·런타임과 native worker가 필요하다. 최초 준비는 위 준비 명령을 따른다.
관찰 진입점은 영어 평가 옵션을 적용하고 개인 설정을 읽거나 저장하지 않는다.
실제 기본 출력 장치로 세션을 시작한다. 재생은 별도로 제어해야 한다.
30초 준비 세션을 끝내고 새 세션으로 본 측정을 시작한다.
각 세션은 Running 상태와 해당 재생 기록을 기다린다. 기다리는 동안 측정 시간은 늘지 않는다.
상태·현재 세션의 보관 기록 개수·앱/worker 메모리를 5초마다 기록한다.
서버/GPU 메모리, 브라우저 자동 재생 제어와 물리 음성 시각 동기화는 포함하지 않는다.
기존 근거를 덮어쓰지 않도록 실행마다 새 결과 경로를 지정한다.
이번 시작 시도는 수신 음성 0초로 실패했다. 실제 30분 검증은 미완료다.
[실행 근거](evidence/natural-session-attempt-windows-20261006.md).

### 재생 기록과 세션 분리 (2026-10-06 후속)

1. 보고서의 `phase=AwaitWarmupPlayback`을 확인한다. 후보 영상을 재생한다.
2. 브라우저에서 재생 상태·영상 위치를 확인한다. 그 확인 시각을 UTC로 기록한다.
3. 아래 명령으로 준비 재생 기록을 전달한다. 시각은 실제 확인값을 사용한다.
4. 30초 준비가 끝나면 영상 재생을 중지한다. 보고서는 새 세션 시작으로 전환한다.
5. `phase=AwaitMeasurementPlayback`에서 영상을 600초 위치로 이동하고 재생한다.
   재생 상태·위치·시각을 다시 확인한다. Measurement 기록을 전달한다.
6. 측정 종료까지 재생 진행·광고·정지 여부를 별도로 관찰한다.

```powershell
# $observedUtc는 재생 상태를 실제 확인한 UTC 시각이다. 아래 명령은 영상을 재생하지 않는다.
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/mark-natural-playback.ps1 -ReportPath benchmarks/results/natural-session-next/observation.json -Phase Warmup -PositionSeconds 600 -ObservedUtc $observedUtc
# 새 세션의 대기 상태에서 확인 시각을 다시 지정한다.
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/mark-natural-playback.ps1 -ReportPath benchmarks/results/natural-session-next/observation.json -Phase Measurement -PositionSeconds 600 -ObservedUtc $observedUtc
```

기록은 실행 ID·자료 URL·단계·시각으로 검증한다. 보고서는 10초 이내 갱신돼야 한다.
확인 시각은 캡처 준비 이후이고 최근 10초 이내여야 한다. 본 측정 위치는 600초 ±5초다.
재생 기록 대기는 최대 120초다. 조건 불일치·세션 실패·음성/번역 없음은 Incomplete다.
기존 보고서와 재생 기록은 덮어쓰지 않는다. 원본 실패 결과는 그대로 남긴다.
준비 기록은 본 측정 기록 개수에 포함하지 않는다. 단계와 세션 ID를 따로 저장한다.
재생 기록은 관찰자의 선언이다. 연속 재생·정확한 음성 시작·전체 대사 보존의 증거는 아니다.
이 후속 변경은 빌드·스크립트 구문만 확인했다. 실제 준비/본 측정 실행은 미검증이다.
[후속 구현 범위](evidence/natural-session-coordination-windows-20261006.md).

기존 `scripts/summarize-caption-timing.py <로그>`로 ASR·번역·읽기 대기를 나눠 요약한다.
Python 실행 환경을 따로 준비한다. 로그의 누락값과 eviction은 0으로 해석하지 않는다.
로그는 물리 화면 지연과 정답 음성 시작/끝을 직접 증명하지 않는다.
P95 출시 판정은 [시각 정의와 음원 기준](07_VALIDATION.md)을 충족한 별도 자료에서 수행한다.
30분 동안 crash/deadlock, 지속적인 backlog·메모리 증가, 정상 대사 누락을 확인한다.
단순히 앱이 30분 열려 있었다고 성공 처리하지 않는다.

서버 중단·장치 분리·Pause/Stop은 별도 시나리오로 수행한다. 기준선 재생 중 섞지 않는다.
보류한 백신/캡처 시작 원인 조사는 재개하지 않는다. 캡처가 되지 않으면 실제 실행은 미완료로 기록한다.
2시간 soak·100회 제어·게임 공존·macOS 검증은 이후 로드맵에 남는다.
