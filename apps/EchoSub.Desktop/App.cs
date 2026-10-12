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
            var regressionIndex = Array.IndexOf(args, "--caption-regression-report");
            var liveRegressionIndex = Array.IndexOf(args, "--caption-live-regression-report");
            var scheduledIndex = Array.IndexOf(args, "--caption-schedule-report");
            var adapterIndex = Array.IndexOf(args, "--caption-adapter-report");
            var naturalIndex = Array.IndexOf(args, "--natural-session-report");
            var usabilityIndex = Array.IndexOf(args, "--ui-usability-probe-report");
            var usabilityWorkerIndex = Array.IndexOf(args, "--ui-usability-probe-worker");
            var recoveryIndex = Array.IndexOf(args, "--ui-recovery-probe-report");
            var recoveryWorkerIndex = Array.IndexOf(args, "--ui-recovery-probe-worker");
            var restartSettingsIndex = Array.IndexOf(args, "--ui-preferences-read-path");
            var restartReportIndex = Array.IndexOf(args, "--ui-preferences-read-report");
            var captureIndex = Array.IndexOf(args, "--ui-capture-dir");
            var captureWorkerIndex = Array.IndexOf(args, "--ui-capture-worker");
            var modelEndpointIndex = Array.IndexOf(args, "--ui-model-capture-endpoint");
            var sessionProbeIndex = Array.IndexOf(args, "--caption-session-probe-report");
            var sessionWorkerIndex = Array.IndexOf(args, "--caption-session-probe-worker");
            var probeIndex = Array.IndexOf(args, "--overlay-probe-report");
            var lineProbeIndex = Array.IndexOf(args, "--caption-line-probe-report");
            var replayIndex = Array.IndexOf(args, "--caption-replay-input");
            var replayReportIndex = Array.IndexOf(args, "--caption-replay-report");
            if (liveRegressionIndex >= 0 && liveRegressionIndex + 1 < args.Length)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = CaptionRegressionProbe.RunAsync(desktop, args[liveRegressionIndex + 1], scheduled: true));
            }
            else if (adapterIndex >= 0 && adapterIndex + 1 < args.Length)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = CaptionScheduledProbe.RunAsync(desktop, args[adapterIndex + 1], fromDeck: true));
            }
            else if (scheduledIndex >= 0 && scheduledIndex + 1 < args.Length)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = CaptionScheduledProbe.RunAsync(desktop, args[scheduledIndex + 1]));
            }
            else if (regressionIndex >= 0 && regressionIndex + 1 < args.Length)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = CaptionRegressionProbe.RunAsync(desktop, args[regressionIndex + 1]));
            }
            else if (naturalIndex >= 0)
            {
                if (naturalIndex + 2 >= args.Length || !int.TryParse(args[naturalIndex + 2], out var naturalSeconds) || naturalSeconds is < 30 or > 7200)
                    throw new ArgumentException("Natural observation requires a report path and 30..7200 seconds");
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = NaturalSessionObservation.RunAsync(desktop, args[naturalIndex + 1], naturalSeconds));
            }
            else if (usabilityIndex >= 0 && usabilityIndex + 1 < args.Length && usabilityWorkerIndex >= 0 && usabilityWorkerIndex + 1 < args.Length)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = UiUsabilityProbe.RunAsync(desktop,
                    args[usabilityWorkerIndex + 1], args[usabilityIndex + 1]));
            }
            else if (restartSettingsIndex >= 0 && restartSettingsIndex + 1 < args.Length && restartReportIndex >= 0 && restartReportIndex + 1 < args.Length && recoveryWorkerIndex >= 0 && recoveryWorkerIndex + 1 < args.Length)
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
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = UiCapture.RunAsync(desktop, args[captureWorkerIndex + 1], args[captureIndex + 1],
                    modelEndpointIndex >= 0 && modelEndpointIndex + 1 < args.Length ? args[modelEndpointIndex + 1] : null,
                    args.Contains("--ui-model-capture-sequence"), args.Contains("--ui-model-capture-revisions")));
            }
            else if (sessionProbeIndex >= 0 && sessionProbeIndex + 1 < args.Length && sessionWorkerIndex >= 0 && sessionWorkerIndex + 1 < args.Length)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = CaptionSessionProbe.RunAsync(desktop, args[sessionWorkerIndex + 1], args[sessionProbeIndex + 1]));
            }
            else if (replayIndex >= 0 && replayIndex + 1 < args.Length && replayReportIndex >= 0 && replayReportIndex + 1 < args.Length)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = CaptionReplayProbe.RunAsync(desktop, args[replayIndex + 1], args[replayReportIndex + 1], legacy: args.Contains("--caption-replay-legacy")));
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

