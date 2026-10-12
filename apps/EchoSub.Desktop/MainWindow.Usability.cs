using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace EchoSub.Desktop;

public sealed partial class MainWindow
{
    private readonly TextBlock captureActionHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock translationActionHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock historyActionHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border historyEmpty = new()
    {
        Padding = new Avalonia.Thickness(20), Background = Brushes.WhiteSmoke,
        Child = new TextBlock { Text = "아직 자막 기록이 없습니다.\n자막을 시작하면 최근 기록이 여기에 표시됩니다.",
            TextWrapping = TextWrapping.Wrap }
    };

    private static string UserHistoryText(HistoryRecord record)
    {
        var start = record.SessionAudioStartSeconds ?? record.AudioStartSeconds;
        var end = record.SessionAudioEndSeconds ?? record.AudioEndSeconds;
        var sourceLabel = record.SourceState switch
        {
            "Partial" or "FinalPending" => "인식 중", "Failed" => "인식 실패",
            "Skipped" => "건너뛴 구간", "Discarded" => "중단된 구간", _ => "원문"
        };
        var translation = record.TranslationState switch
        {
            "Done" when record.AppliedSourceRevision == record.SourceRevision =>
                (record.TranslationIsPreview ? "임시 번역: " : "번역: ") + record.Translation,
            "Pending" or "Done" => "번역 대기 중", "Failed" => "번역하지 못했습니다.",
            "Skipped" => "번역을 건너뛰었습니다.", _ => "번역 없음"
        };
        return $"{start:F2}–{end:F2}초\n{sourceLabel}: {record.Source}\n{translation}";
    }

    private static void SetControlOrder(params Control[] controls)
    {
        for (var index = 0; index < controls.Length; index++) controls[index].TabIndex = index + 10;
    }

    private void SetControlNames(Slider width, Slider opacity)
    {
        AutomationProperties.SetName(endpoint, "음성을 받을 출력 장치");
        AutomationProperties.SetName(language, "음성 언어");
        AutomationProperties.SetName(width, "자막 창 폭");
        AutomationProperties.SetName(overlayFontSize, "자막 글꼴 크기");
        AutomationProperties.SetName(opacity, "자막 배경 불투명도");
        AutomationProperties.SetName(translationEndpoint, "번역 서버 주소");
        AutomationProperties.SetName(translationModel, "번역 모델");
        AutomationProperties.SetName(translationProfile, "번역 입력 방식");
        AutomationProperties.SetName(history, "최근 자막 기록");
        AutomationProperties.SetName(exportSession, "저장할 세션");
        AutomationProperties.SetName(exportBilingualSrtButton, "원문+번역 SRT 저장");
    }

    private void UpdateUsabilityHints(bool available, bool connected)
    {
        overlayFontSize.IsEnabled = available && sessionState is null or "Idle";
        captureActionHint.Text = !connected ? "연결 후 시작할 수 있습니다. 진단 탭에서 다시 연결하세요."
            : !available ? "작업이 끝날 때까지 기다리세요."
            : sessionState == "Running" ? "자막 생성 중입니다. 장치나 언어를 바꾸려면 자막을 중지하세요."
            : sessionState == "Paused" ? resumeReady ? "재개로 자막을 이어서 생성할 수 있습니다." : "일시정지 처리를 마칠 때까지 기다리세요."
            : sessionState == "Preparing" ? "음성 캡처를 준비하고 있습니다. 중지 버튼으로 취소할 수 있습니다."
            : sessionState == "Stopping" ? "자막 중지와 캡처 정리가 끝날 때까지 기다리세요."
            : !modelReady ? "음성 인식 모델이 준비되지 않아 시작할 수 없습니다. 진단 탭을 확인하세요."
            : !captureStartable ? "새 자막을 시작할 수 없습니다. 진단 탭에서 상태를 확인하세요."
            : translationBusy ? "번역 처리가 끝날 때까지 기다리세요."
            : !startCaptureButton.IsEnabled ? "번역 설정 변경이 남아 있습니다. 설정 탭에서 선택 모델 적용을 누르세요."
            : "장치와 언어를 확인한 뒤 자막을 시작하세요.";
        translationActionHint.Text = !connected ? "연결 후 번역 설정을 변경할 수 있습니다. 진단 탭에서 다시 연결하세요."
            : !available ? "현재 작업이 끝난 뒤 변경할 수 있습니다."
            : !translationSupported ? "현재 연결은 번역 설정을 지원하지 않습니다. 실시간 런처로 실행하세요."
            : !captureStartable ? "번역 설정을 변경하려면 자막을 중지하세요."
            : !historyReady || translationBusy ? "진행 중인 처리가 끝난 뒤 변경할 수 있습니다."
            : translationModel.SelectedItem is not string ? "서버 연결 / 모델 조회 후 모델을 선택하세요."
            : "선택 모델 적용으로 번역 설정을 반영할 수 있습니다.";
        historyEmpty.IsVisible = snapshot?.Records.Count is null or 0;
        history.IsVisible = live && !historyEmpty.IsVisible;
        historyActionHint.Text = !connected ? "연결 후 기록을 저장할 수 있습니다."
            : selectingExport ? "파일 선택 또는 기록 처리가 끝날 때까지 기다리세요."
            : !available ? "현재 작업이 끝난 뒤 기록을 저장할 수 있습니다."
            : !exportSupported ? "현재 연결은 기록 저장을 지원하지 않습니다."
            : exportSession.SelectedItem is not ExportChoice ? "저장할 세션 기록이 없습니다."
            : !historyReady ? "기록을 저장하려면 자막을 일시정지하거나 중지하고 처리 완료를 기다리세요."
            : bilingualExportSupported ? "선택한 세션의 TXT, 원문 SRT 또는 원문+번역 SRT를 저장할 수 있습니다."
            : "선택한 세션의 TXT 또는 원문 SRT를 저장할 수 있습니다.";
        ToolTip.SetTip(startCaptureButton, captureActionHint.Text);
        ToolTip.SetTip(applyTranslationButton, translationActionHint.Text);
        ToolTip.SetTip(exportTxtButton, historyActionHint.Text);
        ToolTip.SetTip(exportSrtButton, historyActionHint.Text);
        ToolTip.SetTip(exportBilingualSrtButton, bilingualExportSupported ? historyActionHint.Text : "현재 연결은 원문+번역 SRT 저장을 지원하지 않습니다.");
    }
}
