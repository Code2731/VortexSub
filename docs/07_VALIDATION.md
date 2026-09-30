# 07. 검증·측정·출시 기준

이 문서의 수치는 **검증 목표**다. 아직 실행한 제품 벤치마크는 없다. mock 테스트와 모델 테스트와 실기기 시스템 캡처 테스트를 각각 기록한다.

## 1. 테스트 계층

| 계층 | 내용 | 일반 CI |
|---|---|---|
| Pure unit | 시간축, segmentation, revision, queue, SRT, config | 항상 |
| Protocol integration | UI/client↔worker, 잘못된 메시지, EOF, 느린 reader | 항상 |
| Fixture pipeline | deterministic PCM 입력 + mock ASR/translation | 항상 |
| Real model | 실제 whisper/번역 모델 + 권한 확인된 음원 | 별도 opt-in job |
| Platform capture | 실제 출력 오디오, OS 권한, 장치 변경 | Windows/Mac 실기기 |
| GPU/thermal | 실제 GPU·메모리·게임 동시 사용·장시간 부하 | 실제 기준 장치 |
| Packaging | SDK 없는 환경에서 패키지 설치·실행 | release 전 |

실제 모델이 다운로드되지 않았거나 해당 OS runner가 없으면 해당 테스트는 SKIPPED/BLOCKED로 기록한다. 일반 CI가 green이라는 이유로 제품이 동작한다고 판정하지 않는다.

## 2. 필수 테스트 목록

| ID | 검증 | 관련 요구 |
|---|---|---|
| UT-001 | 44.1/48kHz stereo→16kHz mono의 샘플 수·길이·연속성 | AUD-001/005 |
| UT-002 | 불연속/장치 교체에서 sample clock reset, session time은 유지 | AUD-005 |
| UT-003 | 512-frame 경계와 VAD state/context 수명 | ASR-003 |
| UT-004 | partial 여러 번→final 한 번→늦은 partial 폐기 | ASR-002/TR-003 |
| UT-005 | 같은 문자열의 실제 반복은 보존, overlap 중복만 제거 | ASR-004 |
| UT-006 | Pause/Stop/새 session 뒤 이전 번역 응답 폐기 | TR-003/NF-004 |
| UT-007 | source==target에서 HTTP 요청 0회 | TR-002 |
| UT-008 | 큐 상한·drop/gap·최신 snapshot·backpressure | NF-001/002 |
| UT-009 | 한국어/일본어·emoji·newline의 IPC round-trip | UI-002/HIS-002 |
| UT-010 | SRT 시간 정렬·start<end·final-only·UTF-8 | HIS-002 |
| UT-011 | key와 transcript가 default log/config에 없음 | SEC-002 |
| UT-012 | loopback 이외 endpoint/redirect 차단 | SEC-001 |
| IT-001 | worker stdout log 오염/잘못된 version/대형 payload 거부 | OPS-001 |
| IT-002 | worker kill/parent EOF/느린 reader/고아 프로세스 방지 | OPS-001 |
| IT-003 | HTTP timeout, 401, 429, 500, 잘못된 JSON, tool_calls | TR-001/002 |
| IT-004 | 2개 요청이 역순 반환되어도 원문-번역 정확히 결합 | TR-003 |
| IT-005 | 무음 입력 동안 ASR/translation 실제 호출 0회 | ASR-003/NF-005 |
| IT-006 | 무음 중 capture packet이 멈춰도 pending 발화가 final/폐기 상태로 종료 | ASR-002/AUD-005 |
| HW-W01 | Windows 실제 loopback 10분, microphone 미사용 | AUD-001 |
| HW-W02 | default-follow 전환과 fixed-device 분리 | AUD-002/005 |
| HW-M01 | macOS 실제 system audio 10분, 권한 deny/allow/retry | AUD-001/004 |
| HW-M02 | signed app/worker 권한 귀속과 재실행 | AUD-004/OPS-002 |
| HW-UI01 | 창 이동·DPI·모니터 분리·click-through 해제 | UI-002/003/004 |
| HW-E2E | 실제 시스템 음성→ASR→번역→overlay 양 OS | P0 전체 |
| REL-001 | 개발 SDK 없는 환경에서 설치·모델 준비·실행 | OPS-002 |
| REL-002 | 2시간 연속 실행 + 100회 start/pause/stop | NF-002/004 |

## 3. 음원 세트

초기 품질 corpus 목표: 영어 20분, 일본어 20분, 한국어 10분, 무음/효과음/음악 10분. 각 언어에 깨끗한 1인 음성, 빠른 말, 배경음, 숫자·이름·부정문, 긴 발화와 실제 반복 발화를 포함한다.

직접 녹음하거나 재배포/시험 권한을 확인한 자료만 저장소에 넣는다. 게임/애니 클립을 자동 수집해서 공개 저장소에 넣지 않는다. 개인 실험용 비공개 음원도 경로·해시·출처·허용 범위를 별도 기록한다.

fixture 단위로 정답 전사, 발화 구간, 번역 검토문을 작성한다. `benchmarks/manifest.example.json`은 현재 빈 목록이며 실측/음원을 꾸며 넣지 않았다.

## 4. 지연 정의

- `last_voiced_ms`: 정답 fixture 또는 VAD가 추정한 마지막 음성 sample 시점. 어느 방식인지 결과에 기록한다.
- `source_final_render_ms`: final 원문이 UI에 실제 반영된 시점.
- `translation_final_render_ms`: 같은 segment 번역이 UI에 반영된 시점.
- `L_source = source_final_render_ms - last_voiced_ms`.
- `L_translation = translation_final_render_ms - last_voiced_ms`.
- `L_partial = first_partial_render_ms - first_voiced_ms`.

worker decode 완료 시간만으로 end-to-end UI 지연을 대신하지 않는다. 별도 프로세스 시계는 IPC handshake/clock mapping으로 정합하고 오차를 기록한다.

ASR 작업별 RTF는 `decode_wall_time / decoded_audio_duration`이다. 겹치는 partial 재인식이 있으므로 이 값만으로 전체 실시간 처리 능력을 판단하지 않는다. 실제 입력 1분 동안 사용한 총 ASR 처리 시간, pending age, gap 비율을 함께 기록한다.

## 5. 기준 목표

warm-up 완료, 기본 Balanced, 게임 미실행, 2–8초 발화 fixture 기준이다.

| 항목 | 초기 목표 | 적용 |
|---|---:|---|
| 원문 final 지연 | P95 ≤ 2.0초 | 양 기준 장치에서 측정 |
| 번역 final 지연 | P95 ≤ 4.0초 | 기본 번역 후보와 함께 측정 |
| 긴 발화 partial 첫 표시 | P95 ≤ 2.5초 | partial 켠 상태 |
| 앱 관리형 메모리 | M3 Pro peak ≤ 10 GiB | UI+worker+번역 프로세스 합산, 공유 메모리 중복 측정 주의 |
| Windows GPU 메모리 | peak dedicated ≤ 5 GiB | ASR+관리형 번역, CPU RAM 별도 기록 |
| 2시간 stability | crash/deadlock 0 | gap/skip/latency 추이 함께 공개 |
| silent fixture | final 자막 0 | 디지털 무음 10분 |
| 가벼운 제어 | 캡처 Stop 목표 ≤ 500ms | 모델 종료와 캡처 중단 시간을 분리 |

목표 미달 시 임계값을 조용히 낮추거나 더 큰 모델로 바꾸지 않는다. base/partial off/짧은 문맥 등 변경의 품질·지연 영향을 비교하고 결정 기록을 갱신한다.

## 6. 품질 평가

영어는 WER, 일본어·한국어는 정규화 규칙을 고정한 CER를 기본 지표로 삼는다. 문자열 정규화가 숫자/부정 표현의 중요한 오류를 숨기지 않게 원문 비교도 유지한다. 자동 점수는 품질 분석 도구이며 단독 출시 기준으로 쓰지 않는다.

번역은 영어→한국어와 일본어→한국어 각각 최소 50개 대사를 검토한다. 의미 보존, 부정·수량·고유명사, 문맥 적합성, 불필요한 추가, 지시문 오인 여부를 기록한다.

초기 내부 gate: 각 방향 50개 중 의미 전달 합격 45개 이상, 숫자·부정·명령 오인 관련 중대 오류 0개를 목표로 둔다. 번역 출력과 검토자의 판정을 보관한다. 이 제한된 평가 결과를 전체 언어/모든 콘텐츠 정확도로 일반화하지 않는다.

## 7. 열·게임 동시 실행

M3 Pro 36GB와 RTX 3080 10GB에서 각각 30분 음성 재생 시험 후 2시간 soak를 수행한다. device/OS/전원 상태/ASR·번역 모델 해시/context/thread/quant를 남긴다.

Windows 게임 시험은 동일 설정·동일 장면에서 EchoSub off/on의 평균 FPS와 1% low, 자막 지연, VRAM을 비교한다. 팬 RPM·온도·전력은 수집 가능한 도구에서만 측정하고 앱에 보편적으로 제공되는 값처럼 표시하지 않는다.

OS별 프로세스 CPU% 의미가 다르면 normalized aggregate 비율로 변환하거나 원래 도구 정의를 그대로 명시한다. 외부 서버 모드의 메모리는 측정 가능 범위와 누락 프로세스를 기록한다.

## 8. 출시 gate

**개발 MVP:** P0 요구 전부 구현, 필수 unit/IPC 테스트 통과, 양 OS의 HW-E2E 수행, 번역 실패 시 전사 유지, 주요 오류 경로 검증.

**제품 베타:** P0B 포함, 승인된 기본 모델·변환본·런타임의 라이선스/해시 기록, 관리형 실행, 패키징 테스트, 두 OS soak, 알려진 문제 목록, 위 성능/품질 목표의 실제 결과와 미달 조치 결정.

하드웨어 테스트를 못 한 OS는 지원 완료로 표시하지 않는다. GPU와 창 관리자 특성을 mock으로 대체하지 않는다.

