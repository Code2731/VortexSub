using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EchoSub.Desktop;

/// <summary>
/// First-run setup UI integration for MainWindow.
/// Adds a "설치" tab for model installation and setup.
/// </summary>
public sealed partial class MainWindow
{
    private SetupFirstRun? setupFirstRun;
    private readonly TextBlock setupStatus = new() { TextWrapping = TextWrapping.Wrap };

    /// <summary>
    /// Initializes the first-run setup UI.
    /// </summary>
    private void InitializeSetup()
    {
        try
        {
            // Get the catalog
            var catalog = SetupCatalog.Embedded();

            // Get the host info
            var host = GetSetupHost();

            // Get the install root
            var installRoot = GetInstallRoot();

            // Create the transport
            var transport = new SetupHttpsTransport();

            // Create the first-run UI
            setupFirstRun = new SetupFirstRun(catalog, host, installRoot, transport);

            setupStatus.Text = "설치 탭을 확인하세요.";
        }
        catch (Exception ex)
        {
            setupStatus.Text = $"설치 초기화 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// Gets the current system information for setup.
    /// </summary>
    private static SetupHost GetSetupHost()
    {
        var os = OperatingSystem.IsWindows() ? "windows" :
                 OperatingSystem.IsMacOS() ? "macos" : "unknown";
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => "unknown"
        };

        // Get free space
        long? freeBytes = null;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(GetInstallRoot())!);
            if (drive.IsReady) freeBytes = drive.AvailableFreeSpace;
        }
        catch { }

        // Get verified backends (simplified - real implementation would check actual capabilities)
        var backends = new List<string> { "asr:cpu" };
        // TODO: Add CUDA detection when available

        return new SetupHost(os, arch, backends, freeBytes, true);
    }

    /// <summary>
    /// Gets the installation root directory.
    /// </summary>
    private static string GetInstallRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "EchoSub", "install");
    }

    /// <summary>
    /// Creates the setup tab content.
    /// </summary>
    private Control CreateSetupTab()
    {
        if (setupFirstRun is null)
        {
            return new TextBlock
            {
                Text = "설치 UI를 초기화할 수 없습니다.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16)
            };
        }

        return setupFirstRun.CreatePanel();
    }
}
