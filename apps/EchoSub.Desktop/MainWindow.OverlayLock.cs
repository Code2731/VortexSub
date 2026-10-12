using Avalonia.Controls;
using Avalonia.Automation;

namespace EchoSub.Desktop;

public sealed partial class MainWindow
{
    private readonly CheckBox overlayLock = new() { Content = "자막 창 잠금 · 클릭 통과" };
    private readonly TextBlock overlayLockStatus = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private bool updatingOverlayLock;

    private void InitializeOverlayLock()
    {
        overlayLock.IsEnabled = OperatingSystem.IsWindows();
        AutomationProperties.SetName(overlayLock, "자막 창 잠금과 클릭 통과");
        overlayLockStatus.Text = OperatingSystem.IsWindows()
            ? "잠금을 켜면 자막 창 아래로 클릭합니다. 이 옵션을 끄면 이동·크기 조절을 할 수 있습니다."
            : "클릭 통과는 현재 Windows에서 지원합니다.";
        overlayLock.IsCheckedChanged += (_, _) => ApplyOverlayLock();
    }

    private void ApplyOverlayLock()
    {
        if (updatingOverlayLock || !OperatingSystem.IsWindows()) return;
        try
        {
            if (overlay?.IsVisible == true) overlay.SetClickThrough(overlayLock.IsChecked == true);
            overlayLockStatus.Text = overlayLock.IsChecked == true
                ? "자막 창 잠금 선택됨 · 잠금을 해제하려면 이 옵션을 끄세요."
                : "잠금 해제됨 · 자막 창을 이동하거나 크기를 조절할 수 있습니다.";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Keep the last confirmed lock state if the OS rejected the transition.
            updatingOverlayLock = true;
            overlayLock.IsChecked = WindowsOverlayPlatform.Inspect(overlay!).ClickThrough;
            updatingOverlayLock = false;
            overlayLockStatus.Text = "자막 창 잠금을 변경하지 못했습니다. 창을 닫고 다시 표시하세요.";
            StartupDiagnostics.Write("Overlay lock change failed: " + error.Message);
        }
    }
}
