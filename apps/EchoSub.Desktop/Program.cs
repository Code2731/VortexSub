using Avalonia;

namespace EchoSub.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            StartupDiagnostics.Write($"Unhandled exception: {eventArgs.ExceptionObject}");
        try
        {
            StartupDiagnostics.Write($"Starting desktop PID={Environment.ProcessId}; OS={Environment.OSVersion}");
            Environment.ExitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            StartupDiagnostics.Write($"Desktop stopped; exit={Environment.ExitCode}");
        }
        catch (Exception error)
        {
            StartupDiagnostics.Write($"Startup failed: {error}");
            Console.Error.WriteLine(error);
            Environment.ExitCode = 1;
        }
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

