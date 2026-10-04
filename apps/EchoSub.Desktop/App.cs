using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace EchoSub.Desktop;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var recoveryIndex = Array.IndexOf(args, "--ui-recovery-probe-report");
            var recoveryWorkerIndex = Array.IndexOf(args, "--ui-recovery-probe-worker");
            var restartSettingsIndex = Array.IndexOf(args, "--ui-preferences-read-path");
            var restartReportIndex = Array.IndexOf(args, "--ui-preferences-read-report");
            var captureIndex = Array.IndexOf(args, "--ui-capture-dir");
            var captureWorkerIndex = Array.IndexOf(args, "--ui-capture-worker");
            var sessionProbeIndex = Array.IndexOf(args, "--caption-session-probe-report");
            var sessionWorkerIndex = Array.IndexOf(args, "--caption-session-probe-worker");
            var probeIndex = Array.IndexOf(args, "--overlay-probe-report");
            var lineProbeIndex = Array.IndexOf(args, "--caption-line-probe-report");
            var replayIndex = Array.IndexOf(args, "--caption-replay-input");
            var replayReportIndex = Array.IndexOf(args, "--caption-replay-report");
            if (restartSettingsIndex >= 0 && restartSettingsIndex + 1 < args.Length && restartReportIndex >= 0 && restartReportIndex + 1 < args.Length && recoveryWorkerIndex >= 0 && recoveryWorkerIndex + 1 < args.Length)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = UiRecoveryProbe.ReadPreferencesInNewProcessAsync(desktop,
                    args[recoveryWorkerIndex + 1], args[restartSettingsIndex + 1], args[restartReportIndex + 1]));
            }
            else if (recoveryIndex >= 0 && recoveryIndex + 1 < args.Length && recoveryWorkerIndex >= 0 && recoveryWorkerIndex + 1 < args.Length)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = UiRecoveryProbe.RunAsync(desktop, args[recoveryWorkerIndex + 1], args[recoveryIndex + 1]));
            }
            else if (captureIndex >= 0 && captureIndex + 1 < args.Length && captureWorkerIndex >= 0 && captureWorkerIndex + 1 < args.Length)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = UiCapture.RunAsync(desktop, args[captureWorkerIndex + 1], args[captureIndex + 1]));
            }
            else if (sessionProbeIndex >= 0 && sessionProbeIndex + 1 < args.Length && sessionWorkerIndex >= 0 && sessionWorkerIndex + 1 < args.Length)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = CaptionSessionProbe.RunAsync(desktop, args[sessionWorkerIndex + 1], args[sessionProbeIndex + 1]));
            }
            else if (replayIndex >= 0 && replayIndex + 1 < args.Length && replayReportIndex >= 0 && replayReportIndex + 1 < args.Length)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = CaptionReplayProbe.RunAsync(desktop, args[replayIndex + 1], args[replayReportIndex + 1]));
            }
            else if (lineProbeIndex >= 0 && lineProbeIndex + 1 < args.Length)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = CaptionLineProbe.RunAsync(desktop, args[lineProbeIndex + 1]));
            }
            else if (probeIndex >= 0 && probeIndex + 1 < args.Length)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = OverlayProbe.RunAsync(desktop, args[probeIndex + 1]));
            }
            else
            {
                desktop.MainWindow = new MainWindow();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

