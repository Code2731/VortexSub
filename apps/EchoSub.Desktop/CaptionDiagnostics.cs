using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace EchoSub.Desktop;

// Optional metadata only. Disk writes stay off the UI and IPC reader threads.
internal static class CaptionDiagnostics
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("ECHOSUB_CAPTION_TIMING_LOG");
    private static readonly Channel<string>? Queue = string.IsNullOrWhiteSpace(Path) ? null :
        Channel.CreateBounded<string>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static long dropped;
    private static readonly Task? Writer = Queue is null ? null : Task.Run(WriteAsync);
    public static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    private static void Write(object value)
    {
        if (Queue is not null && !Queue.Writer.TryWrite(JsonSerializer.Serialize(value))) Interlocked.Increment(ref dropped);
    }

    public static void Received(int workerPid, string name, JsonElement payload)
    {
        if (Queue is null || name != "translation.updated" || !payload.TryGetProperty("record", out var record) ||
            record.ValueKind != JsonValueKind.Object) return;
        Write(new { phase = "translation_event_received", at_s = Now, worker_pid = workerPid,
            session_id = Number(record, "session_id"), epoch = Number(record, "epoch"),
            segment_id = Number(record, "segment_id"), source_revision = Number(record, "source_revision"),
            translation_request_id = Number(record, "translation_request_id") });
    }

    public static void Applied(CaptionUpdateTiming timing, bool overlayVisible, int? workerPid)
    {
        if (Queue is null || timing.TranslationRecord is not { } record) return;
        Write(new { phase = "deck_applied", at_s = Now, worker_pid = workerPid, session_id = record.SessionId,
            epoch = record.Epoch, segment_id = record.SegmentId, source_revision = record.SourceRevision,
            translation_request_id = record.TranslationRequestId, slot = timing.Slot,
            deferred_s = timing.DeferredSeconds, overlay_visible = overlayVisible });
    }

    public static void OverlayAssigned(CaptionCards cards)
    {
        if (Queue is null) return;
        Write(new { phase = "overlay_assigned", at_s = Now, previous = cards.Previous is not null,
            current = cards.Current is not null });
    }

    public static async Task DrainAsync()
    {
        if (Queue is null || Writer is null) return;
        Queue.Writer.TryComplete();
        try { await Writer.WaitAsync(TimeSpan.FromSeconds(0.5)); }
        catch (TimeoutException) { }
    }

    private static ulong? Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number &&
        element.TryGetUInt64(out var number) ? number : null;

    private static async Task WriteAsync()
    {
        try
        {
            using var writer = new StreamWriter(Path!, append: true) { AutoFlush = true };
            await foreach (var line in Queue!.Reader.ReadAllAsync())
            {
                var lost = Interlocked.Exchange(ref dropped, 0);
                if (lost > 0) await writer.WriteLineAsync(JsonSerializer.Serialize(new { phase = "dropped", count = lost, at_s = Now }));
                await writer.WriteLineAsync(line);
            }
        }
        catch (IOException) { Queue!.Writer.TryComplete(); }
        catch (UnauthorizedAccessException) { Queue!.Writer.TryComplete(); }
        catch (ArgumentException) { Queue!.Writer.TryComplete(); }
        catch (NotSupportedException) { Queue!.Writer.TryComplete(); }
    }
}
