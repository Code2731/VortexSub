using System.Text.Json;
using Avalonia.Controls;

namespace EchoSub.Desktop;

public sealed partial class MainWindow
{
    private DesktopPreferences preferences = new();
    private readonly string? isolatedPreferencesPath;
    private bool devicesListed;
    private readonly Button savePreferencesButton = new() { Content = "설정 저장" };
    private readonly TextBlock preferencesStatus = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock preparationStatus = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private bool PreferencesEnabled => live && (sessionProbeWorker is null || isolatedPreferencesPath is not null);

    private void RestoreDeviceChoices(EndpointChoice[] choices)
    {
        var preferredDevice = (endpoint.SelectedItem as EndpointChoice)?.Id ?? preferences.DeviceId;
        endpoint.ItemsSource = choices.Select(choice => choice.Id is null
            ? choice with { Name = "기본 출력 장치 · 시작 시 선택" } : choice).ToArray();
        devicesListed = true;
        endpoint.SelectedItem = endpoint.ItemsSource.Cast<EndpointChoice>().FirstOrDefault(choice => choice.Id == preferredDevice);
        if (endpoint.SelectedItem is null)
        {
            endpoint.SelectedIndex = 0;
            preferencesStatus.Text = "저장한 출력 장치가 없어 기본 출력 장치를 선택했습니다.";
        }
    }

    private void RestorePreferences(Slider width, Slider opacity)
    {
        savePreferencesButton.IsEnabled = PreferencesEnabled;
        preferencesStatus.Text = PreferencesEnabled
            ? "설정은 이 PC에 저장합니다. 앱을 닫을 때도 저장합니다."
            : "모의 실행은 개인 설정을 읽거나 저장하지 않습니다.";
        if (!PreferencesEnabled) return;
        try
        {
            preferences = DesktopPreferences.Load(isolatedPreferencesPath);
            language.SelectedItem = preferences.Language;
            width.Value = preferences.OverlayWidth;
            opacity.Value = preferences.CardOpacity;
            overlaySourceEnabled.IsChecked = preferences.ShowSource;
            partialEnabled.IsChecked = preferences.Partial;
            partialTranslationEnabled.IsChecked = preferences.PartialTranslation;
            cosmeticRevisions.IsChecked = preferences.CosmeticRevisions;
            captions.ShowSource = preferences.ShowSource;
            captions.StabilizeCosmeticRevisions = preferences.CosmeticRevisions;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            preferencesStatus.Text = "설정을 읽지 못했습니다. 기본값을 사용합니다. 설정 저장으로 다시 저장할 수 있습니다.";
            StartupDiagnostics.Write("Preferences load failed: " + error.Message);
        }
    }

    private void SavePreferences(Slider width, Slider opacity)
    {
        if (!PreferencesEnabled) return;
        try
        {
            preferences = new DesktopPreferences
            {
                Language = language.SelectedItem as string ?? "en",
                DeviceId = devicesListed ? (endpoint.SelectedItem as EndpointChoice)?.Id : preferences.DeviceId,
                OverlayWidth = width.Value, CardOpacity = opacity.Value,
                ShowSource = overlaySourceEnabled.IsChecked == true,
                Partial = partialEnabled.IsChecked == true,
                PartialTranslation = partialEnabled.IsChecked == true && partialTranslationEnabled.IsChecked == true,
                CosmeticRevisions = cosmeticRevisions.IsChecked == true
            };
            preferences.Save(isolatedPreferencesPath);
            preferencesStatus.Text = "설정을 저장했습니다. 다음 실행에서 복원합니다.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            preferencesStatus.Text = "설정을 저장하지 못했습니다. 현재 설정은 이번 실행에만 적용됩니다.";
            StartupDiagnostics.Write("Preferences save failed: " + error.Message);
        }
    }

    private void UpdatePreparationStatus()
    {
        preparationStatus.Text = sessionProbeWorker is not null || !live
            ? "화면 확인용 모의 실행입니다. 실제 음성을 처리하려면 실시간 런처로 실행하세요."
            : client is null ? "앱 연결을 확인하세요. 연결 실패의 상세 내용은 진단 탭에 있습니다."
            : !modelReady ? "음성 인식 모델을 준비해야 합니다. 진단 탭에서 모델 상태를 확인하세요."
            : sessionState is "Running" or "Preparing" or "Paused" or "Stopping"
                ? "음성 언어와 번역 설정을 변경하려면 자막을 중지하세요. 자막 창은 별도로 표시할 수 있습니다."
            : translationBusy || pendingActions > 0 ? "연결과 설정을 확인하고 있습니다. 잠시 기다리세요."
            : !startCaptureButton.IsEnabled ? "시작 준비가 끝나지 않았습니다. 설정과 진단 탭에서 상태를 확인하세요."
            : !translationConfigured ? "번역이 꺼져 있거나 연결되지 않았습니다. 설정 탭에서 번역 서버와 모델을 확인하세요. 원문 처리는 시작할 수 있습니다."
            : "음성 인식과 번역이 준비됐습니다. 자막 시작을 누른 뒤 음성을 재생하세요. 자막 창은 별도로 표시하세요.";
    }
}
