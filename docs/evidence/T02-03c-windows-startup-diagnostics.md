# Initialize timeout: 원인 미확정·추가 관측

2026-10-01 Windows, T02-03c. **근본 원인은 아직 확정하지 못했다.**
10초 deadline은 제어 응답과 실패 표시에 대한 정책이며 native API 반환을
보장하지 않는다. 시작 안정성 gate는 미통과로 유지한다.

## 확인한 사실과 추정의 구분

- [T02-02d](T02-02d-windows-capture-startup.md)의 모델 없는 새 worker에서도
  `InitializeAudioClient` timeout이 있었다. ASR/VAD를 실패의 필수 조건으로
  볼 수 없다. T02-03b의 기본 장치 live 시작에서도 재현됐다.
- 현재 호출은 shared+loopback+event callback, buffer/periodicity=0,
  `GetMixFormat` 결과, STA 전용 native 스레드다.
  [Microsoft Initialize 계약](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize)의
  shared/event 인수와 STA 주의사항을 대조했다. 추가로 보정할 명백한 인수
  오류는 찾지 못했다. 드라이버/오디오 엔진 대기나 특정 장치의 책임을
  입증한 것은 아니다.
- 이전에는 장치 정보가 Initialize 성공 뒤 공개돼 timeout의 기본 장치가
  `endpoint=null`이었다. 이제 Initialize **호출 전에** 실제 endpoint ID,
  mix rate/channels/mask, native thread ID와 호출 정책을 공개한다.
  이것은 Ready가 아니다. 성공 후 기존 준비 검사를 통과해야 Running이 된다.

## 실패 순간의 스레드 증거 수집

모델 없는 `scripts/probe-capture-startup.ps1`은 실패하고 owner join이 남으면
shutdown **전에** 해당 소유 worker의 `case-N-worker.dmp`를 생성한다.
별도 helper가 Windows `MiniDumpWriteDump`로 스택·모듈·스레드 정보를 저장한다.
full-memory 옵션은 사용하지 않는다. helper 완료를 5초 기다리고 넘으면 소유
helper만 종료해 최대 5초 추가로 정리를 기다린다. 이후 기존 worker shutdown/
종료 정책을 실행한다. 수집 성공/오류/시간은 `dump` 필드에 기록하며 원래
시작 실패 판정은 유지한다. 수집이 대상 실행 시간에 영향을 줄 수 있다.
[Microsoft API 근거](https://learn.microsoft.com/en-us/windows/win32/api/minidumpapiset/nf-minidumpapiset-minidumpwritedump).

덤프는 Git 제외 results에만 둔다. 스택에 프로세스 데이터 일부가 포함될 수
있으므로 자동 업로드하지 않는다. 분석용 빌드는 다음처럼 심볼을 유지한다.

```powershell
$env:CARGO_PROFILE_RELEASE_DEBUG='1'
./scripts/probe-capture-startup.ps1 -Offline -Rounds 2
```

## 이번 실행

위 명령: 기본 선택+활성 endpoint 7개, 각각 2회, **16회 모두 정상 시작/종료**.
Opening 0.028735..2.590692초, 강제 종료 0회. 보고서:
`benchmarks/results/capture-startup-20261001-041341/report.json`.
실패가 없으므로 자동 실패 덤프는 생성되지 않았다. 성공 횟수로 이전 실패를
제외하거나 원인 해결을 주장하지 않는다.

별도 helper 생성 확인: 직접 만든 모델 없는 worker에서 **59,814 bytes** dump,
worker stdin EOF 뒤 exit 0. CaptureSmoke 최종 빌드 경고/오류 0.
근거 `benchmarks/results/dump-helper-20261001-041647/report.json`.
이는 덤프 작성 기능의 확인이며 실패 시 대기 스택 분석의 완료가 아니다.

WPR 10.0.26100의 Audio/CPU/GeneralProfile 존재만 확인했다. ETW를 기록하거나
서비스/드라이버를 변경하지 않았다. 다음 실패 덤프의 `native_thread_id` 스택을
심볼로 해석해 대기 지점과 호출 모듈을 확인하고, 사용자 프로세스 스택으로
원인을 구분할 수 없다면 Audio ETW와 시간대를 맞춰 조사한다.
