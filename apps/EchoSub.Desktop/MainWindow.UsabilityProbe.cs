using Avalonia.Controls;

namespace EchoSub.Desktop;

public sealed partial class MainWindow
{
    internal Control UsabilityControl(string id)
    {
        RequireSessionProbe();
        return id switch
        {
            "device" => endpoint, "language" => language, "start" => startCaptureButton,
            "overlay" => overlayButton, "source" => overlaySourceEnabled,
            "width" => overlayWidth, "opacity" => cardOpacity,
            "address" => translationEndpoint, "model" => translationModel,
            "profile" => translationProfile, "history" => history,
            "session" => exportSession, "clear" => clearHistoryButton,
            "bilingual" => exportBilingualSrtButton,
            _ => throw new ArgumentException("Unknown usability fixture control")
        };
    }
    internal (bool EmptyVisible, bool ListVisible, int Count, bool ClearEnabled) UsabilityHistoryState =>
        (historyEmpty.IsVisible, history.IsVisible, snapshot?.Records.Count ?? 0, clearHistoryButton.IsEnabled);
    internal string UsabilityHistoryText => history.ItemsSource?.Cast<string>().FirstOrDefault() ?? "";
}
