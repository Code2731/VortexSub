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
            var probeIndex = Array.IndexOf(args, "--overlay-probe-report");
            if (probeIndex >= 0 && probeIndex + 1 < args.Length)
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

