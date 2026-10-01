using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace EchoSub.Desktop;

// Optional metadata only. Disk writes stay off the UI and IPC reader threads.
internal static class CaptionDiagnostics
{
    private static readonly SemaphoreSlim lifecycle = new(1, 1);
    private static Recording? active;
    private static string? lastError;
    public static bool Enabled => Volatile.Read(ref active) is not null;
    public static string? LastError => Volatile.Read(ref lastError);
    public static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public static async Task<string?> SetEnabledAsync(bool enabled, string? requestedPath = null)
    {
        await lifecycle.WaitAsync();
        try
        {
            var previous = Interlocked.Exchange(ref active, null);
            if (previous is not null) await previous.DrainAsync();
            if (!enabled) return null;
            Volatile.Write(ref lastError, null);
            var recording = await Task.Run(() =>
            {
                var startupLog = Environment.GetEnvironmentVariable("ECHOSUB_STARTUP_LOG");
                var directory = string.IsNullOrWhiteSpace(startupLog) ?
                    System.IO.Path.Combine(Environment.CurrentDirectory, "logs") :
                    System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(startupLog))!;
                var path = string.IsNullOrWhiteSpace(requestedPath) ?
                    System.IO.Path.Combine(directory, $"caption-timing-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl") :
                    System.IO.Path.GetFullPath(requestedPath);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                return new Recording(path, new StreamWriter(path, append: true) { AutoFlush = true });
            });
            Volatile.Write(ref active, recording);
            recording.Writer = Task.Run(() => recording.WriteAsync());
            return recording.Path;
        }
        finally { lifecycle.Release(); }
    }

    private static void Write(object value)
    {
        var recording = Volatile.Read(ref active);
        if (recording is not null && !recording.Queue.Writer.TryWrite(JsonSerializer.Serialize(value)))
            Interlocked.Increment(ref recording.Dropped);
    }

    public static void Received(int workerPid, string name, JsonElement payload)
    {
        if (!Enabled || name != "translation.updated" || !payload.TryGetProperty("record", out var record) ||
            record.ValueKind != JsonValueKind.Object) return;
        Write(new { phase = "translation_event_received", at_s = Now, worker_pid = workerPid,
            session_id = Number(record, "session_id"), epoch = Number(record, "epoch"),
            segment_id = Number(record, "segment_id"), source_revision = Number(record, "source_revision"),
            translation_request_id = Number(record, "translation_request_id") });
    }

    public static void Applied(CaptionUpdateTiming timing, bool overlayVisible, int? workerPid)
    {
        if (!Enabled || timing.TranslationRecord is not { } record) return;
        Write(new { phase = "deck_applied", at_s = Now, worker_pid = workerPid, session_id = record.SessionId,
            epoch = record.Epoch, segment_id = record.SegmentId, source_revision = record.SourceRevision,
            translation_request_id = record.TranslationRequestId, slot = timing.Slot,
            deferred_s = timing.DeferredSeconds, overlay_visible = overlayVisible });
    }

    public static void OverlayAssigned(CaptionCards cards)
    {
        if (!Enabled) return;
        Write(new { phase = "overlay_assigned", at_s = Now, previous = cards.Previous is not null,
            current = cards.Current is not null });
    }

    public static async Task DrainAsync() => await SetEnabledAsync(false);

    private static ulong? Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number &&
        element.TryGetUInt64(out var number) ? number : null;

    private sealed class Recording(string path, StreamWriter stream)
    {
        public string Path { get; } = path;
        public Channel<string> Queue { get; } = Channel.CreateBounded<string>(
            new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public long Dropped;
        public Task Writer { get; set; } = Task.CompletedTask;

        public async Task DrainAsync()
        {
            Queue.Writer.TryComplete();
            try { await Writer.WaitAsync(TimeSpan.FromSeconds(0.5)); }
            catch (TimeoutException) { }
        }

        public async Task WriteAsync()
        {
            try
            {
                using (stream)
                {
                    await foreach (var line in Queue.Reader.ReadAllAsync())
                    {
                        await WriteDroppedAsync();
                        await stream.WriteLineAsync(line);
                    }
                    await WriteDroppedAsync();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Volatile.Write(ref lastError, error.Message);
                Interlocked.CompareExchange(ref active, null, this);
                Queue.Writer.TryComplete();
            }
        }

        private async Task WriteDroppedAsync()
        {
            var lost = Interlocked.Exchange(ref Dropped, 0);
            if (lost > 0) await stream.WriteLineAsync(JsonSerializer.Serialize(new { phase = "dropped", count = lost, at_s = Now }));
        }
    }
}
