using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EchoSub.Desktop;

// Accessible only to the explicit session fixture entry point, never the ordinary UI.
public sealed partial class MainWindow
{
    internal (string? Session, string? State, bool Busy, int? Worker, bool Closed) SessionProbeState =>
        (sessionId, sessionState, pendingActions != 0, client?.ProcessId, closeReady);
    internal OverlayWindow? SessionProbeOverlay => overlay;
    internal CaptionCards SessionProbeCards => latestCards;
    internal string SessionProbeStatus => status.Text ?? "";

    internal void SessionProbeClick(string action)
    {
        RequireSessionProbe();
        Button button = action switch
        {
            "start" => startCaptureButton, "stop" => stopCaptureButton,
            "pause" => pauseButton, "resume" => resumeButton,
            "disconnect" => stopButton, "connect" => connectButton, "overlay" => overlayButton,
            _ => throw new ArgumentException("Unknown fixture action")
        };
        if (!button.IsEnabled) throw new IOException("Fixture button disabled: " + action + " · " + status.Text);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    internal async Task SessionProbeTranslateAsync(string source, string target)
    {
        RequireSessionProbe();
        await ExecuteAsync(async () =>
        {
            var connected = client ?? throw new IOException("Fixture worker disconnected");
            await connected.SendAsync("mock_segment", new { source });
            var record = (await connected.ReadHistoryAsync()).Records.Last();
            await connected.SendAsync("mock_translate", new { session_id = record.SessionId, epoch = record.Epoch,
                source_revision = record.SourceRevision, segment_id = record.SegmentId,
                translation_request_id = record.TranslationRequestId, text = target });
            // Do not call RefreshCoreAsync here: ordinary event/poll refresh must deliver it.
        });
    }

    internal void SessionProbeKillWorker()
    {
        RequireSessionProbe();
        var connected = client ?? throw new IOException("Fixture worker disconnected");
        using var owned = System.Diagnostics.Process.GetProcessById(connected.ProcessId);
        owned.Kill();
    }

    private void RequireSessionProbe()
    {
        if (sessionProbeWorker is null) throw new InvalidOperationException("Explicit session fixture required");
    }
}
