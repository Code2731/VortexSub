# 문장 끝의 관측 꼬리를 이용한 임시 번역

## 실행과 기본값

```powershell
run-live-cuda-fast.bat -PadShortPartials -SupportedPreview -CaptionTiming
./scripts/probe-paced-translation.ps1 -Backend cuda -Rounds 3 -SupportedPreview
```

기본 off다. fast live CUDA에서만 실험 CLI `--experimental-supported-preview`를
사용하며 DecodeWindow와 함께 선택할 수 없다. 부분 전사/번역 옵션도 세션 시작
전에 켠다. 파일 probe는 양쪽에 패딩을 적용하고 이 정책 off/on만 교대로 비교한다.
새 모델 다운로드나 캡처는 없다.

## 제한과 상태

영어 segment의 첫 문장에서 안정된 3단어 이상의 접두사가 관측 원문과 일치하고,
그 뒤 1~2개의 ASCII 단어와 문장 끝 `. ! ?`만 있는 경우 번역 입력을 확장한다.
꼬리는 80 bytes, 전체 단위는 384 bytes까지다. 숫자·부정·조건·조동사·약어 꼬리,
안정 접두사 안의 조건절, 미완성 숫자/조건, 문장 끝 없는 꼬리는 거부한다.
일본어/한국어 및 뒤 문장의 확장에는 적용하지 않는다.

추가 단어는 한 번 관측된 ASR 결과여서 여전히 바뀔 수 있다. `stable_source`,
PCM 범위와 session/epoch/segment/revision은 그대로 두고 현재 revision의
preview 번역에만 사용한다. 완료 시 그 문장을 확정 문맥으로 넘기지 않는다.
같은 원문이 실제 안정 구간에 들어온 뒤에만 단위 경계를 확정하고, 중복 번역
없이 뒤 단위를 선택한다. 관측 원문이 수정되면 확장 단위도 무효화한다.
기존 요청 identity·취소·stale 적용과 final 전체 전사는 유지한다. IPC 필드는 추가하지 않았다.

## 판단

후속 7문장 통제 비교와 미완성 조건/부정의 공통 보류 수정은
[통제 비교 기록](evidence/preview-risk-controls-windows-20261002.md)을 따른다.
전체 원문 조건 오역과 이전 정정 문맥 혼입이 남아 기본 off를 유지한다.

이번 파일에서 완전한 첫 문장에 대응하는 번역은 약 1초 빨랐지만 첫 출력 자체는
약 0.10초 늦었다. 뒤 조건절보다 먼저 나오는 번역 요청도 증가해 기본 활성화는
보류한다. [측정과 남은 품질 문제](evidence/supported-preview-windows-20261002.md).

집계의 `--first-source`는 작성된 첫 문장과 번역 요청 원문이 정확히 일치한
완료 시각을 찾는다. 번역 의미의 정확도를 자동 판정하는 지표는 아니다.
