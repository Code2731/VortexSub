namespace EchoSub.Desktop;

internal static class StartupDiagnostics
{
    private static readonly object Sync = new();

    public static void Write(string message)
    {
        var path = Environment.GetEnvironmentVariable("ECHOSUB_STARTUP_LOG");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            lock (Sync)
            {
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
