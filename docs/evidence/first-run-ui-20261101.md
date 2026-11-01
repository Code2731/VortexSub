# 첫 실행 UI 구현 근거

## 날짜
2026-11-01

## 목표
`docs/FIRST_RUN_DISTRIBUTION_PLAN.md` 라운드 5의 첫 실행 UI 구현.

## 구현 내용

### 추가 파일
- `apps/EchoSub.Desktop/SetupFirstRun.cs` - 첫 실행 UI 클래스
- `apps/EchoSub.Desktop/MainWindow.Setup.cs` - 첫 실행 UI 탭 통합

### 수정 파일
- `apps/EchoSub.Desktop/MainWindow.Layout.cs` - "설치" 탭 추가
- `apps/EchoSub.Desktop/MainWindow.cs` - `InitializeSetup()` 호출

### UI 기능
1. **모델 조합 선택**
   - OS·아키텍처 기반 필터링
   - `SetupCatalog`에서 사용 가능한 프로필 표시

2. **음성 언어 선택**
   - en, ja, ko, zh 지원

3. **동의 확인**
   - 다운로드·설치 동의 체크박스

4. **설치 진행률 표시**
   - 다운로드·검증 단계 표시
   - 진행률 바 (0-100%)
   - 현재 자산·단계·바이트 표시

5. **설치 상태 확인**
   - `SetupReadiness.Evaluate()` 사용
   - 상태: Ready, DownloadRequired, AwaitingConsent, InsufficientSpace, StorageUnavailable, CatalogIncomplete, Unsupported

### 검증

#### 빌드
```
dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj
```
결과: **성공** (경고 0개 / 오류 0개)

#### 미검증 항목
- 실제 다운로드·설치 동작
- 실제 화면 표시
- 키보드 접근성
- macOS 호환성

## 다음 단계
- 실제 다운로드·설치 검증
- 화면 캡처 검증
- 키보드 접근성 검증
- macOS 호환성 검증

## 환경
- Windows 11
- .NET 10.0
- `master` / `7225e8e`
