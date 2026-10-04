# 정정 번역→두 줄 표시 재생 검증 — Windows, 2026-10-03

## 입력과 구현

전회 Whisper base/small CUDA→Hy-MT2 실제 재생의 방향 정정 기록을 사용했다.
각 6회, 총 12회의 원문/번역 갱신 record와 PCM 시작 기준 `at_s`를 그대로
읽어 생산 `CaptionDeck(LineReadingManaged=true)`→`OverlayWindow`에 공급했다.
원문 갱신도 포함하므로 번역 문자열만 바꾼 문례와는 다르다.
product session 이름만 독립 재생용으로 지정했다. 원래 session/epoch/segment,
source/applied revision, request ID, preview/source/prefix는 유지한다.

앱의 명시적 `--caption-replay-input`/`--caption-replay-report` 진단 경로를 추가했다.
JSON 입력 32 MiB, 1..24회, 회당 history event 4096개, 이벤트 시각 0..120초를
제한한다. 이벤트 시각과 0.1초 tick을 합쳐 가상 시계로 실행하고 마지막 이벤트
뒤 30초까지 읽기 완료를 확인한다. 폭 420 DIP, 실제 Avalonia controls/font/render다.
읽기 상태 변경과 record 도착 시 PNG를 저장한다. 새 모델/오디오 다운로드는 없다.

새 픽셀 검사 도구는 같은 run의 유지된 문자열이 같은 픽셀인지, append가 기존
글자 픽셀을 보존하는지, 빈 줄/글자 유무와 고정 slot bounds가 맞는지 확인한다.
일반 실행의 표시 알고리즘과 옵션 기본값은 변경하지 않았다.

## 결과

| 입력 ASR | 재생 | 상태·읽기 검사 | 저장 이미지 | 픽셀 검사 |
|---|---:|---:|---:|---:|
| base | 6/6 | 2174 | 87 | 472 |
| small | 6/6 | 2177 | 89 | 481 |

총 4351개 상태 검사와 953개 픽셀 검사가 모두 통과했다.
줄 교체는 마지막 글자 추가 이후 최소 2.5초를 지켰고 slot geometry는 유지됐다.
최종 target은 읽기 순서로 누락 없이 한 번 완성됐으며 동일 final 도착 때문에
완성된 target이 재생되는 일은 없었다. 이미 읽은 접두사가 같은 경우에는 suffix만
표시하므로 최종 문장 전체가 항상 한 화면에 동시에 있는 것은 아니다.
모든 입력은 결국 두 줄을 비우고 종료됐다.

base 첫 run은 정정 번역이 3.288초에 도착한 뒤 기존 줄의 읽기를 기다려
3.700초에 `왼쪽 길을 가세요. 아니요.` / `오른쪽 길을 가세요.`로 표시됐다.
실제 저장 이미지도 직접 확인했다.

오른쪽 정정이 처음 나타난 저장 이미지의 가상 시각/번역 후 읽기 대기 중앙값:

| ASR | supported | 첫 오른쪽 표시 (초) | 읽기 대기 (초) |
|---|---|---:|---:|
| base | off | 3.700 | 0.368 |
| base | on | 3.918 | 0.000 |
| small | off | 3.800 | 0.399 |
| small | on | 3.800 | 0.434 |

각 n=3이며 0.1초 tick의 양자화를 포함한다. 이 대기는 읽기 보호의 의도된 효과다.
가상 시계의 저장 렌더 시각이므로 실제 음성→물리 화면 지연 개선량이 아니다.
Windows compositor의 연속 프레임, 실제 시간 GUI 부하, live IPC/MainWindow 경로,
자연 음성·캡처·게임·30분 사용 및 macOS는 이번 검증에 포함되지 않는다.

## 회귀와 재현

일반 check에 두 실제 정정 target의 축약 fixture 8개 assertions를 추가했다.
미읽기 줄 유지, 한 번 도착한 final의 표시, 동일 final 재생 방지, epoch 변경 시
정정 상태 폐기를 생산 Deck+Lines에서 검사한다. 연결 검사 총 34개가 통과했다.
전체 check의 Rust tests/build, C# build/IPC, 표시 상태 41/63/34 assertions도 통과했다.

```powershell
dotnet run --project apps/EchoSub.Desktop --no-build --no-restore -- --caption-replay-input J:/MyProject/VortexSub/benchmarks/results/paced-repair-v2-base-direction-20261003/report.json --caption-replay-report J:/MyProject/VortexSub/benchmarks/results/caption-repair-base-render-20261003/report.json
models/tabby/venv/Scripts/python.exe -X utf8 scripts/check-caption-replay-render.py benchmarks/results/caption-repair-base-render-20261003/report.json
$env:ECHOSUB_OFFLINE='1'; & scripts/check.ps1
```

첫 명령 전 앱을 build한다. small은 해당 native report와 별도 출력 폴더로 바꾼다.
Git 제외 `benchmarks/results/caption-repair-{base|small}-render-20261003/`에
report/PNG/pixel-report가 있으며 `caption-repair-render-summary-20261003.json`에
시각 집계, `caption-repair-final-check-20261003.log`에 최종 전체 검사 로그가 있다.
다음은 P3.2의 실제 시간 앱/세션 재시작·실패 복원 확인이며 자연 음성 수용은 별도다.
