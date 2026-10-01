# 구현 상태

## 두 번역 모델 실제 비교 (2026-10-02)

사용자 동의 후 두 후보 4.32 GB를 다운로드했고 고정 크기/SHA-256이 일치한다.
Qwen 1.7B/TranslateGemma/기준 Qwen 4B의 실제 로컬 HTTP 유효 요청 198회,
응답 오류 0. TranslateGemma의 최초 ChatML 자동 대체 6회는 무효로 제외하고
GGUF 공식 템플릿 렌더링→tokenize→completion 어댑터를 진단 도구에 보완했다.
TranslateGemma는 영어 귀환을 복구했으나 엔진/방어막 행동 반전 3/3·일본어
귀환 오류·물약 숫자/행동 오류가 남았다. Qwen 1.7B도 조건/방향/언어 혼입 오류가
남아 두 후보 모두 품질 gate false·기본 모델 유지다. 새 runtime/앱 연결 없음.
[실행·템플릿 호환·수동 검토](evidence/translation-model-candidates-windows-20261002.md).

## 별도 번역 모델 후보 준비 기록 (2026-10-02)

공식 Qwen3 1.7B Q8_0와 커뮤니티 GGUF TranslateGemma 4B Q4_K_M을 선정하고
revision/크기/해시·라이선스를 별도 manifest에 고정했다. 합계 약 4.32 GB,
준비 당시 다운로드 동의 대기·실제 후보 추론 미실행이었다. 이후 실행 결과는
위의 두 번역 모델 실제 비교를 따른다. 기존 비교 도구에 manifest/
모델/문맥 조건 선택과 Gemma 공식 user 입력, Qwen thinking off를 준비했다.
HY-MT는 배포 라이선스의 한국 지역 제외로 이번 후보에서 제외했다.
Gemma의 현재 llama.cpp 입력 템플릿 호환은 파일 준비 뒤 확인해야 한다.
[선정·명령·수용 조건](TRANSLATION_MODEL_CANDIDATES.md).

## 번역 의미·문맥 통제 비교 (2026-10-02)

6개 완전한 작성 원문 × 세 문맥 조건 × 기존/문맥 분리/일반 텍스트 × 3회,
실제 로컬 HTTP 162회 완료·응답 오류 0. 귀환→출발 오류는 문맥 없음·일반
텍스트에도 반복됐고 물약 가져가기→마시기·미만→이하 오류도 남았다.
현재 모델/양자화/runtime 조합의 한계 후보이며 모델만의 원인으로 단정하지 않는다.
fixture 선택과 plain 대조군을 기존 비교 스크립트에 추가했다. 품질 gate false,
앱 기본 유지·Laya 보류·다음은 별도 번역 모델 후보 비교다. 새 다운로드 없음.
[실행·문장별 검토](evidence/translation-semantic-controls-windows-20261002.md).

## 후반 자막 중단 조사·진단 보완 (2026-10-02)

후속 사용자 실행(06:04~06:13)에서는 중단이 재현되지 않았다. 세션 510.073초,
캡처 수신 507.104초, 번역 적용 374회·화면 적용 338회를 기록했다. 마지막 화면
적용은 사용자가 세션 종료를 누르기 약 0.82초 전이며, 캡처/세션은 Running에서
사용자 종료 요청 후 Stopping→Stopped/Idle로 전환했다. 캡처 오류·앱 통신 오류·
강제 종료는 기록되지 않았다. 이번 실행은 정상 동작 근거이며 최초 중단의
원인 규명이나 수정 완료를 뜻하지 않는다.

사용자는 앱이 반응하지만 새 자막이 나오지 않아 세션 종료를 눌렀다.
05:38 실행 로그에서 앱 정상 종료, 마지막 ASR/HTTP 번역 성공을 확인했다.
마지막 번역 수신 일부에 deck 적용 기록이 없지만, 전사 취소·자막 비움은
사용자 종료에 따른 것일 수 있어 장애 원인으로 확정하지 않는다.
마지막 화면 적용부터 사용자 종료까지 약 1.98초이며 마지막 번역 수신부터는
약 0.92초다. 같은 번역 유지/전사 수정/조회 타이밍도 적용 기록 부재를 만들 수
있으므로 이 자료만으로 장시간 입력 또는 추론 중단을 입증하지 못한다.
기존 지연 로그는 `capture.state`/`session.state`와 history snapshot 적용을
기록하지 않아 입력 중단·기록 조회·표시 경로를 구분할 근거가 부족했다.
상태 전환/캡처 오류 코드와 사용자 종료·일시정지 요청을 desktop 로그에 추가하고,
지연 기록에는 snapshot identity와 5초 간격 상태 메타데이터를 추가했다.
원문·번역·장치명은 기록하지 않는다. Windows Desktop 빌드 PASS(경고/오류 0).
실제 장시간 재현·원인 확정·Mac은 미완료이며 동작 복구 수정으로 간주하지 않는다.

## CUDA 런처 빌드 설정·오류 로그 보완 (2026-10-02)

사용자 런처 기록에서 CMake 실패와 `Native model probe build failed`를 확인했다.
전체 stderr가 빠져 최초 직접 원인은 확정하지 못했으며, 기존 `native` 설정
재빌드는 3분 10초에 성공했다. 이전 성공 설정 `86`과 런처 `native`의 차이가
native 캐시 정리·재빌드를 유발하므로, 기본 대상을 `nvidia-smi`에서 감지하도록
변경했다. 명시적 환경 변수는 유지하며 GPU 조회에는 10초 제한을 둔다.
전체 Cargo 출력을 `logs/native-build-*.log`에 저장하고 실패 메시지에 경로를 넣었다.
Windows/RTX 3080/CUDA 12.6에서 자동 감지 `86` CUDA/VAD worker release 빌드
PASS(4분 16초), 배치파일과 같은 Windows PowerShell 5.1 재빌드 PASS(0.30초,
캐시 정리 없음). 명령은 `scripts/build-model-probe.ps1 -Backend cuda
-Package echosub-worker -Vad -Offline`. 앱 UI·실제 캡처·Mac·다중 GPU는 미검증.

## 문맥 분리 번역 앱 체크박스 (2026-10-02)

로컬 번역 서버 설정에 **이전 문맥 분리 번역 · 실험 기능 / 기본 끔**을 추가했다.
Ready 모델이 있으면 체크 변경으로 재설정하고, 연결 전에는 다음 모델 설정에
선택을 전달한다. 실행/일시정지/정리 중 변경은 막고 적용 상태를 표시한다.
실패 시 즉시 변경 선택을 되돌리며 기존 worker에는 새 필드를 보내지 않는다.
`configure_translation.isolated_context` boolean/capability/적용 상태를 추가했다.
Windows `scripts/check.ps1`의 Rust/IPC·C# smoke·빌드 PASS. 실제 UI 클릭과 Mac은 미검증.

## 번역 프롬프트·문맥 전달 후보 (2026-10-02)

12개 영/일 원문을 이전 문맥 없음/있음으로 짝지어 기존 지시·지시 강화·문맥 분리·
언어명 명시·예시·한국어 지시를 비교했다. 영어 문맥 혼입과 명령 반전이 줄었으나
`until I return` 오역, 일본어 정정 혼입, 숫자/어휘 오류와 새 부정 오류가 남았다.
품질 gate false이며 지시 강화/문맥 분리는 `-IsolatedTranslationContext` 실험으로
연결했다. 기본은 기존 요청을 유지한다. Laya는 미도입·후속 보류 판단 후보다.
다음은 번역 의미 오류의 모델 역량/문맥 영향 분리와 필요 시 번역 전용 후보 비교다.
[실험 계약](TRANSLATION_PROMPT_CONTEXT.md) · [근거](evidence/translation-prompt-context-windows-20261002.md).
요청 body 동등성·최종 HTTP 48회/실제 Rust owner 48회, 후보 native 파일 42회 확인.
workspace/CUDA release·문법/fmt·C# 빌드 완료; 일반 테스트·최종 CLI의 실제 UI/Mac 미실행.

## 조건·부정·정정 통제 비교와 보류 수정 (2026-10-02)

7개 합성 영어 음성에서 기존/관측 꼬리 정책 각 3회, 수정 전후 총 84회 비교했다.
기존 정책에도 있던 `only if the shield is`의 반대 조건 번역과 `You must not`의
행동 없는 번역을 발견해 공통 preview 보류를 수정했다. 재비교 각 6/6에서 해당
중간 결과가 사라졌고 해당 문장의 첫 번역은 약 0.54~0.64초 늦어졌다.
전체 원문을 받은 `until I return` 오역과 이전 정정 문맥의 “아니요” 혼입은 남는다.
품질 gate false·SupportedPreview 기본 off. 다음은 전체 원문/이전 문맥/프롬프트 비교다.
실제 파일/HTTP·CUDA release·문법/fmt/workspace build 확인; 일반 테스트·live/UI/Mac 미실행.
[재현·문장별 검토](evidence/preview-risk-controls-windows-20261002.md).

## 문장 끝 관측 꼬리 preview 비교 (2026-10-02)

영어 첫 문장의 관측된 1~2단어 꼬리만 임시 번역 입력에 넣는 실험 옵션을 연결했다.
stable/history 경계를 즉시 확정하지 않고 실제 안정 뒤 중복 없이 경계를 확정한다.
같은 패딩 기준의 실제 ASR/HTTP 각 3회에서 첫 출력 1.749→1.852초로 늦어졌지만,
완전한 첫 원문에 대응하는 번역은 2.853→1.852초로 약 1초 빨랐다.
첫 “왼쪽을 선택…” 수정은 없어졌으나 뒤 조건절 전 조기 번역 요청과 원문 수정은 증가했다.
최종 ASR 6/6 동일, 품질 gate false·기본 off. 다음은 조건/부정/숫자/방향 수정의 통제 비교다.
native release·실제 파일/HTTP·문법/fmt/workspace build 확인; 일반 테스트·live/UI/자연/Mac 미실행.
[계약](SUPPORTED_PREVIEW.md) · [측정](evidence/supported-preview-windows-20261002.md).

## 짧은 partial 패딩 owner/번역 비교 (2026-10-02)

native owner와 런처 실험 옵션 `-PadShortPartials`를 연결했다. 기본 off,
DecodeWindow와 동시 선택 금지다. 원래 PCM 범위·identity와 final 전체 입력을 유지한다.
같은 적응형 스케줄러/실제 Qwen HTTP의 교대 각 3회에서 첫 원문
1.378→0.854초·안정 구간 1.637→1.111초였지만 첫 번역 1.812→1.793초로
큰 가속은 없었다. `the`로 끝난 안정 원문은 DanglingWord로 보류되고,
on 첫 번역은 “왼쪽을 선택…”이었다가 완전한 문장으로 수정됐다.
최종 ASR 6/6 동일, 번역 품질 gate false. 다음은 미완성 꼬리의 preview 정책 비교다.
CUDA/VAD release·실제 파일/HTTP probe·문법/fmt 확인; 일반 테스트와 live/UI/자연/Mac 미실행.
[계약](SHORT_PARTIAL_PADDING.md) · [실측](evidence/short-partial-padding-windows-20261002.md).

## SenseVoice 후보 비교와 짧은 Whisper 입력 실험 (2026-10-02)

사용자 동의 후 약 259 MB의 고정 모델/wheel을 해시 검증·별도 설치했다.
영/한 합성 각 10개와 무음 1개를 전체/부분 입력 각 3회 비교했다.
SenseVoice CPU 전체 decode 영어 0.067671초, Whisper CUDA 0.046737초로
엔진 교체에 따른 추론 가속은 없었다. Whisper가 0.8초 입력을 거부하는 것을
확인하고 파일 probe에 1.02초 zero-pad 후보를 추가했다. 첫 원문 시뮬레이션
영어 1.093→0.854초·한국어 1.102→0.849초이며 최종 원문 60/60 동일했다.
일부 부분 원문의 오인식과 수정이 남아 제품 기본값은 변경하지 않았다.
다음은 패딩 후보의 생산 스케줄러/번역 paced 비교다. Python/PowerShell 문법,
Rust fmt·native release build 확인; 일반 테스트·live/화면·자연/일본어·Mac 미실행.
[실측과 판단](evidence/asr-candidates-windows-20261002.md).

## 실제 부분 전사 window/fallback 연결 (2026-10-02)

적용된 revision에만 정렬 mapping을 만들고 owner에서 축소 재결합 실패 시
전체 snapshot으로 재전사한다. 취소/단일 예약과 확정 전체 입력을 유지한다.
실험 옵션 `run-live-cuda-fast.bat -DecodeWindow`는 기본 off다.
실제 paced CUDA/HTTP 각 3회에서 첫 번역 1.830→1.841초, 누적 decode
1.296→1.477초로 속도 개선은 없었다. 축소 2회 중 fallback 1회/실행이며
최종 원문은 6회 같았다. 강제 fallback의 실제 완전한 원문 복원도 확인했다.
Windows check Rust 163개·C# 표시 39 assertion/HTTP/IPC와 빌드, Python 6개 PASS.
실제 게임/캡처/화면·자연/일본어 음성·macOS는 미검증이다.
다음은 합의한 ASR 후보 비교다. [연결·측정 근거](evidence/window-owner-windows-20261002.md).

## DTW 단어 정렬 파일 경로 (2026-10-02)

기존 interval과 별도로 DTW 발화점을 추출하고 검증·재결합하는 파일 경로를 추가했다.
동일 CUDA 파일 조건별 3회에서 일반 interval은 모두 거부됐지만 DTW는 모두
0.928초 축소/전체 원문 재결합에 성공했다. 중앙값 일반 전체 0.182729초,
DTW 전체 0.205880초, DTW 축소 0.174018초다. 일반 대비 차이가 작고 prefix
정렬 비용도 있어 실시간 개선은 미확인이다. Rust 94개 PASS, native CUDA 빌드와
6회 파일 비교 완료. 라이브는 아직 전체 입력/DTW off다.
[구현 범위](DECODE_WINDOWS.md) · [측정·다음](evidence/dtw-alignment-windows-20261002.md).

## Decode window 기반 분리 (2026-10-02)

제품 range/PCM window를 core API와 job에서 분리했다. 정렬·overlap 재결합은
fixture/파일 probe에 연결했고 라이브는 전체 입력을 유지한다. CUDA 실제 파일의
3초/4초 전사 모두 `path`의 길이 0 시간으로 후보를 거부해 추가 속도 개선은
확인하지 못했다. 관련 Rust 93개 PASS, native CUDA/VAD 빌드·파일 probe 완료.
단어 시간 정렬과 owner 재결합/fallback 연결이 남았다.
[구조와 다음](DECODE_WINDOWS.md) · [근거](evidence/decode-window-windows-20261002.md).

## 적응형 부분 전사 스케줄 (2026-10-02)

첫 요청을 0.8초로 복구하고 빠른 모드의 실제 재전사를 결과별 새 오디오
조건(0.256~1.024초)으로 선택한다. 빈/반복 결과와 decode 비용도 반영한다.
동일 합성 영어 파일·실제 CUDA ASR/Qwen HTTP 각각 3회에서 첫 번역
중앙값 2.016→1.738초, 누적 decode 1.398→1.049초다. 품질 gate는 false다.
Windows check(Rust 157개·C# 표시 39개 assertion·HTTP/IPC), Python 요약기
5개·native CUDA/VAD 빌드 PASS. 실제 VAD/캡처/화면·게임·macOS는 미실행이다.
[정책·다음 단계](ADAPTIVE_PARTIALS.md) · [측정 근거](evidence/adaptive-partial-windows-20261002.md).

## 빠른 모드 첫 요청·보류 이유 (2026-10-02)

사용자 빠른 모드 로그에서 39개 번역 구간 중 35개가 확정 전 첫 번역을 냈다.
첫 원문→안정 원문 중앙값 0.514초, 번역 처리 0.212초, UI 반영 0.033초다.
일부 안정 원문→첫 번역 요청은 1초 이상이며 기존 로그로 원인은 구분하지 못한다.
빠른 모드 첫 요청을 0.8→0.5초(실효 0.512초)로 앞당기고 보류 이유를 추가했다.
기본 모드·두 전사 일치·0.5초 요청 간격·미완성/확정 우선 규칙은 유지한다.
Windows check(Rust 154개·C# 표시 39개 assertion·HTTP/IPC), 요약기 4개와
native CUDA/VAD 빌드 PASS. 실제 변경 후 지연/품질·UI·게임·macOS는 미실행이다.
[근거·위험·다음](evidence/early-partial-windows-20261002.md).

## 단계별 자막 지연 계측 (2026-10-01)

기존 자막 지연 기록 옵션에 worker의 접수·ASR·안정 원문·번역 전달/완료
metadata를 연결했다. worker/UI 시계를 분리해 단계별 초 단위 통계를 계산한다.
샘플 음성 길이는 벽시계 지연으로 해석하지 않는다. 기존 로그에는 새 단계가 없다.
Windows check(Rust 153개·C# 표시 39개 assertion·HTTP/IPC·빌드),
요약기 fixture 3개 PASS. 이전 보류 규칙과 충돌한 기존 tail fixture 입력을 수정했다.
실제 단계 로그·미완성 사례 HTTP 품질 비교·UI·게임·macOS는 미실행이다.
[사용·범위](CAPTION_READING.md) · [확인 근거](evidence/pipeline-timing-windows-20261001.md).

## 사용자 표시 로그·미완성 영어 조각 보류 (2026-10-01)

사용자 로그 32건에서 번역 수신→Deck 중앙값 0.033초/최대 0.036초,
Deck 보호 대기 0초, 누락/drop 0을 확인했다. 서버 처리 중앙값 0.287초다.
음성→자막 전체 시간과 실제 화면 렌더 완료의 측정은 아니다.
미완성 숫자/조건/관사 조각의 영어 임시 번역을 보류하도록 구현했다.
기본 worker·native CUDA/VAD release 빌드 및 fmt/diff PASS.
이번 변경의 회귀 실행·실제 번역 품질/지연 비교·UI·게임·macOS는 미실행이다.
[측정 근거·구현 한계·다음](evidence/caption-user-log-windows-20261001.md).

## 자막 지연 기록 UI 옵션 (2026-10-01)

앱에 **자막 지연 기록** 체크박스와 저장 경로/오류 표시를 추가했다.
기본 꺼짐, 실행 중 전환 가능, 다시 켤 때 새 파일 생성이다.
텍스트 없는 metadata·128개 비차단 대기·background 쓰기 정책을 유지한다.
Windows `dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore`
PASS(경고/오류 0). 테스트 및 실제 UI 클릭·게임·macOS는 미실행이다.

## 현재 draft 갱신과 표시 계측 (2026-10-01)

현재 draft 수정 간격 1.25→0.25초, 표시 timer 0.5→0.1초로 분리했다.
이전 읽기 카드 보호·4~10초 만료·확정/반박 즉시 적용과 기본 IPC 주기는 유지한다.
`-CaptionTiming`으로 metadata 수신→Deck/overlay 대입을 기록할 수 있다.
check(Rust 153개, C# 표시 39개·HTTP/IPC·빌드) PASS. fixture의 수정 대기 0.2초
확인이며 화면 지연 감소의 실측은 아니다. 실제 UI/log·게임·macOS는 미실행이다.
[규칙·사용](CAPTION_READING.md) · [근거·다음](evidence/caption-display-windows-20261001.md).

## 실제 paced ASR→Qwen·빠른 모드 (2026-10-01)

동일 GPU에서 실제 ASR/HTTP를 연결해 1초·0.5초 간격 각각 3회 측정했다.
첫 번역 중앙값 3.361→2.330초, 안정 prefix 3.107→2.087초다.
확정 번역은 8.109→8.062초로 큰 차이가 없다. 누적 decode 0.741→1.283초,
HTTP 4→5회와 미완성 정보의 조기 노출/오역도 확인했다. 품질 gate false.
`run-live-cuda-fast.bat` 선택 실행을 추가했고 기본 간격/기본 꺼짐을 유지한다.
check(Rust 153개·C# 표시 29개·IPC/HTTP), native CUDA/VAD 빌드 PASS.
실제 빠른 모드 UI/장치·게임·자연/일본어·macOS는 미실행이다.
[측정·사용·다음 작업](evidence/paced-translation-windows-20261001.md).

## Whisper CPU/CUDA 부분 전사 비교 (2026-10-01)

동일 7.605초 합성 영어 파일을 backend/간격별 3회, 총 12회 측정했다.
1초 간격 중앙값은 첫 텍스트 CPU 2.781→CUDA 2.182초, 확정 완료
8.554→7.810초, 누적 decode 5.410→1.115초다. 모든 확정 원문은 같았다.
0.25초 CUDA는 첫 텍스트 1.363초지만 partial 25회와 비용 증가가 있어
기본 간격은 유지한다. Qwen/게임 경합·화면/품질 수용은 미검증이다.
`run-live-cuda.bat` 선택 경로를 추가했고 기본 CPU를 유지했다.
native CPU/CUDA 파일 측정·빌드·fmt/스크립트 구문 PASS; 실제 CUDA UI는 미실행.
[조건·한계·다음 작업](evidence/partial-backends-windows-20261001.md).

## 단위 번역 실측·trim 검토 (2026-10-01)

같은 전사 trace와 실제 Qwen HTTP로 기존 prefix/단위 방식을 비교했다.
긴 영어 7.605초에서 요청 9→5회, 첫 임시 3.352→3.409초, 확정
8.942→8.918초다. 요청 중복은 감소했지만 첫 출력 가속은 관측하지 못했다.
gate 오역·귀환 조건 누락을 확인해 품질 gate false·기본 꺼짐을 유지한다.
CPU timed token 두 전사에서 단어 길이 0으로 trim 후보를 거부했다.
라이브 trim은 미구현이며 다음은 기존 Whisper CPU/CUDA paced 비용 비교와
품질 회귀 기준이다. 화면 지연·자연/일본어 ASR·게임·macOS는 미검증.
[조건·오류·재현](evidence/translation-units-trim-windows-20261001.md).

## 문장·짧은 절 임시 번역 (2026-10-01)

성공한 닫힌 단위 뒤의 원문만 최대 384 UTF-8 바이트로 번역한다. 앞부분은
문맥으로 전달하고 중복 요청을 생략한다. 한 stable revision에서도 후속 단위를
처리하며 원문 반박 시 되돌린다. unit/prefix IPC guard와 같은 segment의 카드
이동을 연결했다. 최종은 전체 원문 번역이며 원문/오디오 범위는 유지한다.
check(Rust 152개, C# 표시 29개 assertion·IPC/HTTP)와 native CPU/VAD 빌드 PASS.
실제 Qwen 파일 replay 비교와 token trim 가능성은 위 후속 측정에서 확인했다.
음성→화면 지연·게임·렌더링·macOS 및 라이브 trim은 남아 있다.
[계약과 한계](TRANSLATION_UNITS.md).

## 두 카드 읽기 정책 (2026-10-01)

이전 읽기 카드와 현재 draft를 분리했다. 글자 수에 따른 4~10초 만료,
동일 구간 일반 교체 최소 1.25초·최신 대기 하나를 적용한다. 첫 번역/확정/
prefix 반박은 즉시 적용한다. 이전 카드가 읽기 중이면 최신 자리만 교체해
두 카드 상한을 지키며 일부 중간 구간은 생략될 수 있다. 반복 조회와 같은
번역의 원문 revision은 만료를 연장하지 않는다. 원문은 계속 기본 꺼짐이다.
Desktop/ProtocolSmoke 빌드 PASS; 테스트 실행·실제 화면/발화 평가는 미실행.
[표시 계약](CAPTION_READING.md). 다음은 문장/짧은 절의 조기 번역이다.

## 부분 전사 스케줄러·계측 (2026-10-01)

실행 중 partial을 추월하지 않고 최신 대기 범위 하나로 병합한다. 임시 HTTP의
반환까지 다음 partial을 보류하고 확정은 우선 처리한다. 완료 적용/무시·누적
decode 초·최신 대기 초를 상태에 추가했다. 전체 check(Rust 149개, C# 표시
21개 assertion·IPC/HTTP), native CPU/VAD 빌드와 실제 속도 파일 재생 PASS.
0.25초 스트레스에서 부분 갱신 0→9; 기본 1초의 첫 원문 지연은 거의 동일했다.
기본 간격은 유지한다. VAD→HTTP 전체 지연/화면/게임/macOS는 미실행이다.
[근거](evidence/partial-scheduling-windows-20261001.md). 다음은 두 카드 읽기 정책이다.

## 실시간 번역 OSS 검토 (2026-10-01)

SimulStreaming, Sublume, LiveTranslate, LiveCaptions-Translator의 고정 커밋
소스에서 partial 처리·번역 commit·자막 표시 정책을 검토했다.
다음 순서는 실행 중 partial 추월 방지/계측, 읽기 카드와 draft 분리,
의미 단위 번역, 모델 비교다. 모델/runtime 설치나 외부 앱 실행은 하지 않았다.
현재 지연의 실제 원인 판정은 후속이다. 첫 스케줄러 구현은 위 항목에 기록했다.
[검토 문서](REALTIME_OSS_REVIEW.md).

## 임시 자막 읽기 시간 보완 (2026-10-01)

새 부분 전사 요청마다 표시가 비는 문제를 보완했다. UI에서 같은 segment의
기존 임시 자막을 다음 전사/번역 대기 동안 유지하고 유효한 새 번역으로 교체한다.
원문 prefix 수정·다른 segment·Pause/Stop/epoch 변경 시 지운다. 임시 자막은
첫 표시부터 5초이며 반복 조회로 연장하지 않는다. UI와 smoke 프로젝트 빌드
PASS; 이번 변경의 테스트 실행·실제 화면 조작은 미실행이다.

## 안정 prefix·임시 번역 갱신 (2026-10-01)

연속 두 전사의 공통 앞부분과 기본 꺼짐인 임시 번역 옵션을 연결했다.
확정 우선·revision/epoch 무효화·1.5초 deadline·최신 대기 1개와 표시 라벨을
구현했다. Rust 146개/C# HTTP·IPC·표시 21개 assertion, native CPU/VAD 빌드 PASS.
기존 CPU Whisper prefix + 실제 HTTP 오프라인 비교 10회 PASS; 긴 합성 음원에서
첫 번역 5.753초 개선, 짧은 문장에서는 이득 없음. live/UI 수용은 미검증이고
의미 반전/조건 오역으로 품질 채택 보류다. [계약](STREAMING_TRANSLATION.md) ·
[근거](evidence/streaming-translation-windows-20261001.md).
SenseVoice/Fun-ASR와 Qwen3 1.7B의 [후보 검토](MODEL_CANDIDATES.md)는 조사 단계다.
현재 4B-Instruct-2507은 비사고 전용이며 새 모델 다운로드/실측은 하지 않았다.

## 번역 엔진 비교 갱신 (2026-10-01)

동의받은 TabbyAPI/ExLlamaV3 모델·격리 런타임 설치, production HTTP 비교 도구와
선택형 `run-tabby.bat`을 추가했다. 현재 설정에서 두 실행 순서의 평균은
llama.cpp 0.118728~0.130975초, Tabby 0.157779~0.160002초다. 기본 런처는 llama를
유지한다. 후보 제한 해제 조건의 Tabby 우위를 현재 앱으로 확대하지 않는다.
두 서버의 CPU 파일 전사→번역→history, Rust 138개/C# HTTP·IPC·표시 14개 PASS.
조건 오역·누락으로 품질 gate는 false다. 게임/화면/live E2E·Mac 수용은 미검증.
[측정과 한계](evidence/translation-engines-windows-20261001.md) ·
[실행·재현](TRANSLATION_ENGINES.md). 다음은 live 단계별 지연 및 ASR 가속 후보다.

최종 갱신: 2026-10-01. 결과 범위: Windows T00-01, 독립형 T00-02 WASAPI probe, T00-04.1 모델 harness, T00-04.2 실제 취소·수명 probe, T00-04.4 동시 부하, T00-04.3 MOCK 오버레이, T01-01a/b 독립 오디오·발화 코어, T01-02a 독립 상태·작업 큐, T01-02b mock worker 전달·버전 history. T02-01a에서 실제 Whisper를 worker 파일 진단과 history에 연결했다. T02-01b에서 실제 Silero VAD도 파일 경로에 연결했다. T02-02a에서 실제 WASAPI PCM 정규화·worker 제어를 연결했다. T02-02b에서 Windows live 캡처→연속 VAD→Whisper→history도 진단으로 연결했다. T02-02e에서 진단 원문 history·오버레이 UI를 연결했다. T02-03a에서 UUID session 제어 어댑터를 연결했다. T02-03c/d에서 UTC·원문 저장·기록 삭제를, T02-04a에서 기본 꺼짐인 부분 전사를 연결했다. 전체 제품 wire·번역과 macOS 번들은 미구현이며 실제 원문 UI 렌더링 수용은 미검증이다.

| 작업 | 상태 | 근거 및 다음 단계 |
|---|---|---|
| BASE-00 | PARTIAL; T00-01 준비 완료 | 분리 명세·SDK·대응표, 고정 모델 3개·합성 음원 마련. 자연/일본어 음원과 Mac 정보 미확보. |
| T00-01 | PASS (Windows); BLOCKED (macOS) | 양 언어 빌드, worker 프로토콜, C# 클라이언트, 숨김 GUI 실행 중 자식 worker 생성과 부모 강제 종료 후 worker 정리 확인. 실제 창 클릭/시각 QA와 Mac 실행은 미실행. |
| T00-02 | PARTIAL (Windows) | WASAPI render loopback probe가 실제 PCM·mix format·레벨·device/QPC 위치·플래그를 출력한다. 600.2초 실행에서 587.32초 분량의 PCM 프레임 수신. 통제된 fixture, 실제 기본 장치 전환·고정 장치 분리 시험은 미실행. |
| T00-03 | BLOCKED (macOS) | Mac 실기기와 번들·권한 환경 없음. |
| T00-04.1 | PARTIAL (Windows); BLOCKED (macOS) | 동의받은 모델 3개 다운로드·SHA 검증, 재사용 ASR context/fixture harness와 로컬 번역 harness 구현. 합성 en/ko 각 10개+무음, 번역 en/ja 각 10개 실행. 자연 발화·일본어 음원·VRAM peak·Mac 미검증. 품질 채택 보류. [번역 검토](evidence/T00-04.1-translation-review.md) |
| T00-04.2 | PASS (Windows probe scope); BLOCKED (macOS) | CPU/CUDA base/small 각각 두 시점별 취소·같은 context 재시작 10회, 정상 종료 10회, 강제 종료 후 새 프로세스 복구 10회. CPU small encoder 취소 최대 2.254569초. 협력 취소+native 반환 대기 정책. 캡처 Stop/epoch/UI 통합·Mac 미검증. [측정·수명 정책](evidence/T00-04.2-windows-cancellation.md) |
| T00-04.4 | PASS (Windows probe scope); BLOCKED (macOS) | 5조건 각 302초, 실제 동시 구간 300초 이상. base 동시 전사/번역 각 1208회 완료·건너뜀 0; small 동시 전사 1189회 완료·19개 건너뜀. 오류/OOM 0. base 성능 후보 유지, small 4 Hz 기본값 보류. [측정과 후속 설정](evidence/T00-04.4-windows-contention.md) |
| T00-04.3 | PARTIAL (Windows); BLOCKED (macOS) | MOCK 오버레이, 표시/숨김, 메인 창 폭·불투명도 조절, 드래그 핸들, 화면 작업 영역 기준 초기 위치 구현. 125% 배율의 HWND 속성·투명도·크기/위치 변경 확인. 다른 앱 포커스·게임 합성·실제 마우스 조작은 미검증. |
| T01-01 | PARTIAL: T01-01a/b PASS (Windows fixture scope) | 공통 float32 mono/stereo 정규화, 16 kHz/512-frame, sample 시간축·gap, 12초 rolling/유한 immutable snapshot 구현. 정규화 15개+VAD 15개 fixture. 확률 기반 발화·8초 분할·watchdog 구현. 실제 Silero·native clock·worker/Mac 미검증. [오디오](AUDIO_CORE.md) · [VAD](VAD_CORE.md) |
| T01-02 | PASS (Windows mock core/IPC scope); 실제 inference 통합 미검증 | 단일 실행·final 2/partial 1·번역 2 대기, 전체 키 검증, final 동결, 취소 반환 대기, 번역 terminal과 버전 history 구현. 코어 fixture 23개+전달 신규 9개 Rust 시험·확장 C# smoke. worker event 256/응답 32 예약·seq/페이지 복구 구현. 실제 native 파일 ASR은 T02-01a에서 별도 연결; HTTP/UI history/Mac 미연결. [코어](PIPELINE_CORE.md) · [전달](WORKER_DELIVERY.md) |
| M1 | PARTIAL | 정규화·발화·상태/작업 큐의 결정론적 코어 구현. 유한 event 큐·버전 IPC snapshot/C# 클라이언트도 연결. 실제 Silero state/context의 파일 경로는 T02-01b에서 확인. WASAPI bounded PCM 경로는 T02-02a에서 확인. Windows live VAD/ASR은 T02-02b 진단에서 확인. UI 렌더링·Mac은 미검증. |
| T02-01 | PARTIAL: T02-01a/b PASS (Windows CPU 파일 진단) | 실제 Whisper owner·파일 loader·ASR-only history·epoch 취소/재시작 연결. 실제 Silero 파일 probability/state reset 연결. partial·GPU worker·UI·Mac 미검증. [VAD](WORKER_VAD.md) [계약](WORKER_ASR.md) · [측정](evidence/T02-01a-windows-worker-asr.md) |
| T02-02 | PARTIAL: T02-02a/b PASS (Windows CPU 진단) | WASAPI bounded PCM·QPC/sample 시작점·재시작 gap, 연속 Silero→Whisper final/history, Stop/취소·활성 shutdown/EOF 연결. pinned device polling. 제품 Pause·장치 전환/soak·Mac 미검증. [live 계약](WORKER_LIVE_ASR.md) · [측정](evidence/T02-02b-windows-live-asr.md) |
| T02-02c | PARTIAL: 시작 실패 처리·관측 구현; 무음 시작 gate 미통과 | 10초 Opening timeout/첫 실패 phase 보존·bounded 관측, STA·shared/event-driven 인수 0 보정, whole-fixture 재생 및 실패 checkpoint 추가. 실제 Initialize 대기를 재현했으며 보정 뒤에도 EOF용 무음 새 worker 시작 실패가 남음. [추가 근거](evidence/T02-02c-windows-capture-startup.md) |
| T02-02d | PARTIAL: 모델 없는 장치별 새 worker probe 구현·검증 | matrix 62회+PowerShell 5.1 기본 1회 중 정상 시작 62/Initialize timeout 1, 전부 정상 프로세스 종료·강제 종료 0. ASR/VAD 없이도 지연 재현, Failed 뒤 Ping/재시작 거부/정리 확인. 이후 반복 성공으로 시작 안정성 gate를 올리지 않음. [probe 계약](CAPTURE_STARTUP_PROBE.md) · [근거](evidence/T02-02d-windows-capture-startup.md) |
| T02-02e | PARTIAL: 진단 원문 UI 연결·빌드 통과; 화면 수용 미검증 | 장치/언어·Start/Stop·실패/join·worker 재연결, 버전 history·현재 epoch/revision 원문·5초 오버레이 구현. Rust 91개·C# 빌드/IPC 통과, PS5.1 live 창/연결 로그 확인. 실제 클릭/원문/만료/게임 포커스 미검증. [실행·범위](LIVE_UI.md) |
| T02-02f | PARTIAL: UI 조작·조회 분리; 조작 지연 실측 미검증 | 자동/수동 조회의 버튼 잠금 제거·사용자 명령 시 조회 취소, snapshot 지연 적용, IPC와 독립된 원문 만료, 반복 창 닫기 중 worker 정리 보호. Windows Rust 91개·포맷/빌드·C# 빌드/IPC smoke PASS. 미실행 수동 항목은 [UI 계약](LIVE_UI.md)을 따른다. 제품 session/Pause는 후속. |
| T02-03a | PARTIAL: UUID session 제어 어댑터·실제 CPU IPC 확인 | 시작/Pause/Resume/Stop·숫자 ID 대응·history 유지·native 반환 후 Idle·UI 버튼 연결. Rust 92개·확장 C# smoke·native 빌드와 실제 final 3개 PASS. 화면 조작·full 도중 Pause·전체 제품 wire·Mac 후속. [계약](SESSION_CONTROL.md) · [근거](evidence/T02-03a-windows-session-control.md) |
| T02-03b | PARTIAL: UUID history·세션 상대 시간·native Pause 확인 | source/history UUID 메타데이터·UI UUID 필터·최대 1,001개 session metadata 구현. Rust 92개·C# IPC PASS, 빈 세션 1,001회 뒤 UUID 유지. 실제 첫 시작 timeout 1회; 재실행 native Pause/Resume·final 3개 PASS. 시작/화면/품질 gate는 유지. [근거](evidence/T02-03b-windows-session-history.md) |
| T02-03c | PARTIAL: UTC·원문 TXT/SRT export·시작 진단 보완 | Rust 94개·C# IPC/native CPU 빌드 PASS, 실제 두 세션 TXT/SRT 파일 저장 PASS. 모델 없는 시작 16/16 정상; 다음 실패용 endpoint/thread ID·덤프 수집 추가. 근본 원인·UI 저장 창·제품 수용 미확정. [export 근거](evidence/T02-03c-windows-history-export.md) · [시작 진단](evidence/T02-03c-windows-startup-diagnostics.md) |
| T02-03d | PARTIAL: 세션 기록 삭제·재개 ID 보존 | Rust 95개·C# IPC/native CPU 빌드 및 실제 이전 세션 삭제/새 세션·저장 파일 보존 PASS. UI 확인 창 조작·Mac 미검증. 사용자 요청으로 시작 timeout 원인 분석은 후속으로 보류. [근거](evidence/T02-03d-windows-history-clear.md) |
| T02-04a | PARTIAL: opt-in 부분 전사·동일 구간 revision·확정 우선 연결 | Rust 98개·C# IPC/native CPU PASS. 실제 부분 6개 revision→확정 3개 및 Pause/Resume/export/clear PASS. 첫 시작 timeout 1회 보존; UI/품질/Mac 미검증. [계약](WORKER_PARTIAL_ASR.md) · [근거](evidence/T02-04a-windows-partial-asr.md) |
| T02-04b | PARTIAL: 시간/span 기반 경계 정합·빈 결과 skip | Rust 105개·C# IPC/native CPU PASS. 실제 8초 분할/0.608초 겹침/continuation/Final 확인; 제거 span 0으로 실제 dedup 효과 미검증. 무음 단계 입력 격리 실패로 전체 probe exit 1. [계약](ASR_RECONCILIATION.md) · [근거](evidence/T02-04b-windows-asr-reconciliation.md) |
| M2 | PARTIAL | CPU 실제 VAD·전사·제어/history 및 Windows opt-in 부분 전사 연결. 시작 안정성·화면 자막·자연 음성 경계 수용 미통과. |
| T02-04c | PARTIAL: token 시간·byte 정합과 통제된 파일 무음 | Rust 110개·C# IPC/native CPU PASS. 600초 파일 무음에서 VAD/ASR/history 0; 실제 파일 token 시간 수집 확인. 실제 live dedup·자연 음성·UI·Mac 미검증. [계약](ASR_TOKEN_ALIGNMENT.md) · [근거](evidence/T02-04c-windows-token-alignment.md) |
| T03-01a | PARTIAL: 로컬 번역 요청·응답 계약 | 신규 fixture 8개 포함 Rust 118개·C# IPC/build PASS. 숫자 loopback 주소, 문맥/Unicode 예산, deadline·bypass, 모델 목록·응답 검사 구현. HTTP owner/worker/UI 연결은 다음 단계. [계약](TRANSLATION_CONTRACT.md) · [근거](evidence/T03-01a-windows-translation-contract.md) |
| T03-01b | PARTIAL: bounded HTTP owner·실제 로컬 번역 | Rust 130개·C# IPC/build PASS. 모델 조회/선택, 공유 deadline·최대 1회 재시도·취소/종료·응답 상한 구현. 실제 Qwen en/ja 20/20 응답, 평균 0.133초/최대 0.350초. 귀환 조건 오역/누락으로 품질 보류; worker/UI 연결 후속. [근거](evidence/T03-01b-windows-translation-http.md) |
| T03-01c | PARTIAL: worker final→HTTP→history | Rust 138개·C# typed HTTP/IPC PASS. 실제 CPU Whisper 영어 3개 번역 완료·한국어 1개 bypass; full-key 적용·Pause/Stop/epoch·큐·오류 시 원문 보존 fixture PASS. UI/live E2E/품질/Mac 후속. [계약](WORKER_TRANSLATION.md) · [근거](evidence/T03-01c-windows-worker-translation.md) |
| M3 | PARTIAL: worker 번역/history 연결 | 실제 파일 ASR→번역 확인; UI·양 OS live E2E·품질 수용은 후속. |
| M4~M5 | NOT_STARTED | 해당 제품 통합/실기기 수용 결과 없음. |

## 확인된 Windows 개발 환경

### Initialize 단독 호출 (2026-10-01)

사용자 요청으로 `cargo run -p echosub-capture-windows --locked --offline -- --initialize-only`를 실행했다. 기본 render endpoint, 48,000 Hz/stereo/float32, shared loopback + event callback, duration/periodicity 0으로 `IAudioClient::Initialize`가 **2.085610초에 Ok(())**를 반환했다. 이 경로는 Start/GetService/PCM 수신과 모델 추론을 실행하지 않는다. 독립 debug probe의 이번 호출에서는 10초 지연을 재현하지 못했으며, worker release의 기존 실패 원인은 확정하지 않았다. 직전 두 worker 실패 중 백신 승인 창은 없었다는 사용자 관찰도 기록한다.

| 항목 | 관측값 |
|---|---|
| OS | Windows NT 10.0 build 26200, win-x64 |
| CPU | AMD64 Family 25 Model 33, 논리 프로세서 16개 (`PROCESSOR_IDENTIFIER`) |
| GPU | NVIDIA GeForce RTX 3080, 10240 MiB, 드라이버 617.14 (`nvidia-smi`) |
| RAM | 미확인. WMI/CIM 조회가 접근 거부됨 |
| Rust | rustc/cargo 1.90.0, stable Windows MSVC |
| .NET | SDK 10.0.102, 런타임 10.0.2 |
| Git | 2.55.0.windows.3; 이 저장소는 이번 작업에서 초기화됨 |
| Native 도구 | VS 2022 Community MSVC 14.44.35207, CMake 3.31.6, Ninja, `C:/Program Files/LLVM/bin/libclang.dll`, CUDA Toolkit 12.6 확보. ASR 스크립트가 설치 경로를 찾아 프로세스 PATH에 추가 |
| Mac | 실기기, OS, SDK, 서명/권한 정보 미확인 |

## 실행 결과

| 범위 | 명령 또는 방법 | 결과 |
|---|---|---|
| Rust 컴파일 | `cargo check --workspace --offline` | PASS: Windows capture crate 포함 |
| Rust 시험 | `cargo test --workspace --locked --offline` | PASS: worker 프로토콜 5개, PCM 레벨 2개 |
| UI 빌드 | `dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore` | PASS: 경고 0, 오류 0. `AVALONIA_TELEMETRY_OPTOUT=1` 설정 |
| C#↔Rust 통합 | `dotnet run --project tests/EchoSub.ProtocolSmoke/EchoSub.ProtocolSmoke.csproj --no-build -- <worker.exe>` | PASS: Unicode, 64개 동시 요청, 정상/강제 종료 |
| 저장소 검증 스크립트 | `ECHOSUB_OFFLINE=1`, 로컬 NuGet source로 `scripts/check.ps1` | PASS: Rust 포맷/7개 시험, 양 프로젝트 빌드, C# smoke |
| Windows 앱 프로세스 | 숨김 `EchoSub.Desktop.exe` 실행 후 프로세스 관찰 | PASS: UI 프로세스 생존, 자식 worker 생성, 부모 종료 후 새 worker 0개 |
| Windows UI 시각·클릭 확인 | 창에서 연결·Ping·재연결 | SKIPPED: 숨김 프로세스 시험만 수행 |
| Windows 장치 열거 | `echosub-capture-windows --list` | PASS: 활성 render endpoint 7개, 기본 장치 `스피커(GSX 1000 Main Audio)` 식별 |
| Windows 지속 loopback | `echosub-capture-windows --seconds 600` | PARTIAL HW-W01: 48 kHz, stereo, float32, mask `0x3`; 실행 600.2초, 58,732 packets/28,191,360 frames(587.32초), silent 41, 첫 packet discontinuity 1, timestamp error 0, position gap 0, 마지막 무음 중 250ms timeout 29. [원본 메트릭](evidence/T00-02-windows-10min.log) |
| Windows 장치 고정 | `--device-id`로 기본 ID 및 없는 ID 지정 | PARTIAL HW-W02: 실제 ID로 capture 시작, 없는 ID는 전환 없이 `device_unavailable` 및 exit 1. 실제 장치 전환·분리 미실행 |
| Windows MOCK 오버레이 | `scripts/probe-overlay.ps1` | PASS (관측 범위): 125% 배율, 실제 Transparent, HWND TOPMOST/NOACTIVATE/TOOLWINDOW, 표시→크기/위치 변경→숨김→재표시. [보고서](evidence/T00-04.3-windows-overlay.json), [Avalonia 렌더 프레임](evidence/T00-04.3-windows-overlay.png). 다른 앱과 합성한 데스크톱 캡처는 아님 |
| Windows 오버레이 포커스 | probe 전/후 `GetForegroundWindow` | BLOCKED: 세 번 모두 HWND 0. 유지 여부는 null이며 PASS로 간주하지 않는다. 물리 드래그/리사이즈·다른 앱 입력·게임 위 표시도 미실행 |
| macOS 빌드/실행 | Mac에서 `bash scripts/check.sh` | BLOCKED: Mac 환경 없음 |
| Windows 모델 probe 및 Mac 캡처 | P0-MODEL, HW-M01/02 | PARTIAL (Windows 합성 ASR/작성 번역); BLOCKED (Mac) |

T00-04.1 추가 검증: 기본 workspace Rust 시험 9개(프로토콜 5, PCM 2, scoring 2), UI·C# smoke 빌드/실행, 번역 .NET 빌드가 통과했다. native CPU/CUDA release와 실제 모델별 21회 전사도 성공했다. 초기 CPU 빌드의 `/O2` 누락과 CUDA architecture 오류를 수정했다. 최적화 CPU 재측정은 base 평균 약 0.621~0.695초, small 약 2.317~2.379초다. 초기 비최적화/컴파일 부하 측정은 채택 비교에서 제외한다. 합성 음성의 WER/CER는 실제 발화 수용 근거가 아니다. 번역은 20회 성공했으나 귀환 조건을 출발 조건으로 잘못 옮긴 사례가 있어 품질 gate는 false다. [실행·오류·한계](evidence/T00-04.1-windows-models.md)

T00-04.2 추가 검증: 최종 두 시점 실행에서 취소/재시작 총 80회, 정상 종료 40회, 소유 자식 강제 종료 및 복구 40회 통과. 취소 callback acknowledgement, 출력 segment 미반환, 다음 decode의 비겹침·문자열 일치, context 해제 후 join을 확인했다. pre-cancel과 token 재사용 거부도 통과했다. native 취소는 연산 경계에서 지연될 수 있으므로 캡처/UI 제어 스레드에서 기다리지 않는다. 모든 새 ASR/취소 시간 출력은 초다.

Windows probe는 render endpoint의 loopback만 열며 microphone endpoint를 열지 않았다. 실제 음원 fixture를 시간에 맞춰 재생한 통제 검증과 전환·분리 시험이 없으므로 HW-W01/W02 gate는 PASS로 올리지 않았다. `--seconds 600`은 실행 벽시계 시간이며, 초기 장치 open과 마지막 재생 중단 때문에 누적 PCM 프레임은 600초에 못 미쳤다. 마지막 silent 패킷 뒤에는 packet이 멈춰 timeout으로 기록됐다.

## 미해결 항목

2026-10-01 사용자 관찰: 백신 프로그램이 권한 승인을 요청했고, 사용자가 약 10초
뒤 승인 버튼을 눌렀다고 보고했다. 이 실행에서는 외부 승인 대기가 10초 캡처 시작
deadline을 넘긴 것으로 해석할 수 있다. 백신 로그/차단 대상과 이전 각 실패의
승인 창 발생 여부는 미확인이므로 모든 Initialize timeout의 원인 확정으로 확대하지
않는다. 승인 뒤 재실행 결과를 확인할 대상으로 기록하며 deadline은 그대로 유지한다.

2026-09-30 Cargo 경로 수정: 사용자 런처의 `cargo` 인식 실패를 확인했다. 설치는 `%USERPROFILE%/.cargo/bin/cargo.exe`에 존재하지만 탐색기에서 시작한 프로세스의 PATH에서 누락될 수 있다. 런처가 PATH 및 표준 설치 폴더를 검색하고 절대 실행 경로를 사용하도록 수정했다. .NET도 같은 방식으로 찾는다. PATH를 System32만 남긴 Windows PowerShell 5.1 실행에서 두 SDK 검색→경고/오류 없는 빌드→메인 창·worker 연결→정상 종료를 확인했다. 시스템/사용자 전역 PATH는 변경하지 않았다.

2026-09-30 실행 진단 보완: `run.ps1`의 우클릭 실행에서 창이 보이지 않는 신고가 있었다. 재현 실행에서는 NuGet 온라인 조회 경고 후 실제 메인 HWND와 worker가 생성됐다. 사용자 실행의 최초 종료 원인은 미확정이다. 런처 단계 출력·오류 시 대기·`logs/` 실행/시작 로그, 패키지 복원 재사용, 직접 EXE 실행과 중앙 배치를 추가했다. Windows PowerShell 5.1에서 빌드→실제 창 visible→worker 연결→WM_CLOSE 정상 종료/worker 정리를 확인했다. 루트 `run.cmd`는 더블클릭 진입점이다.

- 로컬 NuGet 패키지는 존재했지만 기본 sandbox global-packages 경로와 달라 처음 복원에 실패했다. `ECHOSUB_NUGET_SOURCE`와 저장소 내부 `.nuget/packages` 경로로 복원했다.
- Avalonia build telemetry가 허용되지 않은 AppData 로그 경로에 쓰려 하여 sandbox 빌드가 실패했다. 공식 환경변수 `AVALONIA_TELEMETRY_OPTOUT=1`을 검증/실행 스크립트에서 설정한다.
- 모델 3개와 native Windows 도구, 로컬 합성 fixture는 확보했다. 자연 발화·일본어 음원·배경음·긴 발화와 Mac 실기기는 미확보다. 모델 revision/hash/license와 재현 명령은 `benchmarks/model-downloads.json`과 `benchmarks/README.md`를 따른다.
- T00-02 probe는 독립 실행 파일이며 250ms 장치 상태 polling을 쓴다. 기본 worker의 capture capability는 false이며 T02-02 진단 opt-in에서 true다. 장치 알림·실기기 전환/분리 수용은 후속이다.

T00-04.3의 Windows 창 속성과 Avalonia 프레임은 확인했으나 다른 앱의 입력 포커스를 조회할 수 없었다. `ShowActivated=false`와 `WS_EX_NOACTIVATE`의 적용 사실을 입력 유지의 실측으로 확대하지 않는다. [실행 및 수동 검증 절차](OVERLAY_PROBE.md)를 일반 사용자 데스크톱에서 수행한다.

T00-04.4 Windows 동시 부하 probe를 완료했다. T01-01a 독립 오디오 코어도 구현했다. T01-01b 확률 기반 발화 구간·packet-stop watchdog도 구현했다. T01-02a 상태기계·유한 작업 큐도 구현했다. T01-02b mock worker 전달·유한 event 큐·버전 snapshot/C# 복구도 구현했다. T02-01a에서 실제 ASR owner를 파일 진단으로 연결했다. T02-01b에서 동의받은 Silero/ORT를 파일 분할과 연결했다. T02-02a는 Windows capture PCM 큐·정규화·worker 제어를 연결했다. T02-02b에서 QPC/sample 시작점·gap과 지속 live VAD/ASR owner를 연결했다. 다음은 제품 session/Pause 계약과 실제 UI 원문 history·오버레이 연결이다. T00-04.1은 자연 음성/일본어/Mac, T00-04.2는 Mac·실제 worker 통합 보완이 필요하며 M0 전체는 미통과다. T00-02의 통제 음원 10분·장치 전환/분리와 T00-04.3의 다른 앱 입력·게임 위 표시·수동 조절은 실제 조작으로 완료한다.


T00-04.4 추가 검증: 총 8437회 추론 완료, 실패 0, small 전사 요청 건너뜀 19. GPU 사용과 번역 full offload를 로그로 확인했고 소유 프로세스/API key 잔여는 0이다. 장치 전체 GPU 메모리 관측 최대는 small 동시 6721 MiB이며 프로세스 VRAM peak가 아니다. 모든 새 번역·동시 부하 시간도 초다. 실제 자막 latency·게임 공존·품질 gate·Mac은 미검증이다.

T01-01a 추가 검증: Windows의 pure PCM fixture 15개와 기존 9개 시험, Rust fmt/workspace·C# 빌드와 IPC smoke 통과. 필터 center 기준의 sample 범위, packet 분할 불변성, 고주파 alias 억제, backwards anchor/stale epoch 거부, snapshot 수명·고갈을 확인했다. 실제 VAD 무음 억제·ASR 호출 0회·native callback 비차단 수용·Mac은 미검증이다.


T01-01b 추가 검증: mock 확률 VAD fixture 15개와 기존 24개, Rust fmt/workspace·C# 빌드·IPC smoke 통과. 600초 exact-zero PCM에서 mock 모델/ASR 요청 0, 8초/overlap 분할, healthy packet-stop의 실제 PCM만 final, 오류/Pause/Stop 폐기와 gap reset을 확인했다. 실제 Silero state/context·자연 음성·native watchdog·Mac 수용은 미검증이다. [범위와 후속 계약](VAD_CORE.md)

T01-02a 추가 검증: 생성 PCM/mock 결과 fixture 23개와 기존 39개, Rust fmt/workspace·C# 빌드·IPC smoke 통과. 최신 partial·final 우선/동결·큐 초과 기록, epoch/새 session 뒤 stale 결과 거부, native 반환 전 예약 유지, 번역 deadline/terminal, history 버전·1,000개 상한을 확인했다. 실제 native/HTTP·worker 전달/event 큐·IPC snapshot·overlap 텍스트 정합·Mac은 미검증이다. [계약과 다음 단위](PIPELINE_CORE.md)

T01-02b 추가 검증: transport 5개+protocol 신규 4개, 기존 62개로 Rust 71개·fmt/workspace·C# 빌드·확장 smoke 통과. event 256/제어 예약 32, seq/로컬 수신 고갈 복구, typed Unicode/초 단위 DTO와 301개 history 페이지를 확인했다. stdout unread 시 약 5초 후 오류 종료했다. 일반 실행은 empty history·실제 추론 capability false이며 생성 결과는 명시적 mock opt-in에서만 전달한다. 실제 native 캡처 안전 종료·모델·UI history·Mac은 미검증이다. [계약·시험](WORKER_DELIVERY.md)

T02-01a 추가 검증: Rust 74개·fmt/workspace·C# 빌드/IPC smoke와 실제 CPU base worker probe 통과. 합성 en/ko final 20개 평균 0.577181초, 취소/재시작 10회, reset 응답 최대 0.000187초·native 반환 최대 0.565063초, 추론 중 정상 종료 0.516145초를 관측했다. 무음/잘못된 WAV 해시는 ASR 0회이며 모델 해시 오류 뒤에도 제어 응답이 가능하다. 실제 Silero·캡처·UI·GPU worker·Mac은 미검증이다. [근거](evidence/T02-01a-windows-worker-asr.md)

T02-01b 추가 검증: Rust 79개·기본 C# IPC smoke와 CPU 실제 Silero→Whisper 파일 진단 통과. final 32개·ko-08 빈 ASR 실패 1개를 보존, 무음·톤·잡음 3개는 ASR 0회다. epoch reset 10회·두 발화 분리·모델/DLL 해시 오류를 확인했다. 파일별 VAD 평균 0.008699초·최대 0.014931초이며 화면 latency가 아니다. 품질 gate false, live capture·UI·자연/일본어 음성·Mac은 미검증. [근거](evidence/T02-01b-windows-worker-vad.md)

T02-02a 추가 검증: Rust 84개·fmt/workspace·C# IPC와 실제 loopback Start/Stop 3회, 활성 shutdown/부모 EOF 통과. 최종 608 packets·accepted PCM 6.048초, Stop 응답 최대 0.000219초·join 완료 최대 0.031628초. 장치 mix 48 kHz stereo/mask 0x3. live_asr=false이며 원문 history/UI는 생성하지 않는다. 실제 큐 고갈·장치 전환/분리·장기/게임·Mac은 미검증. [근거](evidence/T02-02a-windows-worker-capture.md)

T02-02b 추가 검증: Windows CPU live loopback→연속 Silero→Whisper final/history 2회 확인, native 실행 중 Stop/재시작 3회·full 예약 유지 중 새 캡처 1회, 해시 실패·활성 shutdown/EOF 통과. Rust 89개·fmt/workspace·C# IPC PASS. 단독 최종 run의 Stop 응답 최대 0.000797초·capture/VAD join 확인 0.043802초·그 이후 full 반환 확인 0.409312초, 재시작 gap 최소 0.364750초. 중간 final 하나가 8초 상한에 도달했고 추가 run에서 client Opening timeout을 관측했다. native API phase와 C# fast-exit handle/예외 처리를 보완한 최종 PCM/live 회귀는 통과했다. 시작 지연의 원인은 미확정이다. 품질/부하·제품 gate는 미통과이며 UI·Pause·장치/soak·CUDA/Mac은 후속이다. [계약](WORKER_LIVE_ASR.md) · [근거](evidence/T02-02b-windows-live-asr.md)

T02-02c 추가 검증: 최종 Rust 91개·fmt/workspace·C# 빌드/IPC smoke PASS. 최종 재생 중 PCM Start/Stop 20회·즉시 Stop·활성 shutdown/EOF PASS(40.320초 PCM, 첫 Opening 3.160569초). 실제 live whole-fixture final 1.792/1.824초·취소/재시작 3회와 종료가 통과한 실행도 있으나, 보정 뒤 다음 무음 새 worker의 Initialize가 10.010085초에 timeout으로 실패했다. 첫 실패 phase/경과 시간·pending join/이전 검증 checkpoint를 보존한다. 무음 cold-start gate는 미통과이며 제품 UI 연결은 이 오류/소유 프로세스 복구 계약을 정리한 뒤 진행한다. [근거](evidence/T02-02c-windows-capture-startup.md)

T02-02e: 실제 원문 진단 UI를 연결하고 Windows 저장소 검사를 통과했다. 다음은 [수동 UI 확인](LIVE_UI.md)의 Start/Stop·원문·만료·실패/재연결 및 제품 session/Pause 계약이다. native 시작 안정성 gate는 유지한다.

T02-03a: UUID session 제어를 기존 Windows live pipeline에 연결했다. Stop 응답 0.000244초, native 반환/정리 후 Idle 확인 0.514054초(단일 실행). 화면/품질 gate는 유지하며 다음은 UUID source/history wire·세션 시간축과 native full 중 Pause/Resume 보완이다.

T02-03b: UUID source/history 메타데이터와 세션 상대 시간을 연결했다. 첫 시작은 Initialize timeout 10.006663초로 실패했고 재실행은 native Pause 뒤 옛 record Discarded·Resume/new UUID final을 확인했다. 시작 안정성과 전체 제품 wire 전환·UI 수용은 미완료다.

T02-03c: 시작 UTC·TXT/원문 SRT와 UUID 세션 선택 저장을 연결했다. 저장소 Rust 94개·C#
IPC, 실제 두 세션 파일 생성 PASS. Initialize 전 endpoint/thread ID와 실패 덤프 수집을
추가했다. 모델 없는 16회는 모두 성공해 실패 덤프 분석은 미실행이며 원인은 미확정이다.

## T03-02a 데스크톱 번역 표시 (2026-10-01)

로컬 서버 주소/실제 모델 목록 선택, 준비·실패 상태와 번역 끄기를 연결했다.
번역 history와 원문+한국어 오버레이를 연결하고 현재 UUID/epoch/applied revision과
request ID로 표시를 제한한다. 실패 시 원문 유지, 늦은 번역의 5초 수명 연장 금지를
14개 C# 표시 검사로 확인했다. Rust 138개·C# HTTP/IPC·native CPU/VAD 빌드 PASS.
실제 UI 조작/화면·live E2E·품질·Mac 수용은 미검증이다.
[실행 안내](DESKTOP_TRANSLATION.md) · [근거](evidence/T03-02a-windows-desktop-translation.md).
다음 T03-02b는 서버 복구/다중 모델 선택 진단과 유한 번역 표시 프리셋이다.
