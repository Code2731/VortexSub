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
        if (!Enabled) return;
        if (name is "capture.partial_requested" or "capture.segmented" or "asr.started" or "asr.completed"
            or "source.partial" or "source.final" or "translation.started" or "translation.completed" or "translation.updated")
        {
            var identity = payload.TryGetProperty("record", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : payload;
            // Whitelist scalar metadata; never serialize the worker's text-bearing payload.
            Write(new { phase = "pipeline_event_received", at_s = Now, worker_pid = workerPid, event_name = name,
                worker_at_s = Seconds(payload, "worker_at_s"), session_id = Number(identity, "session_id"),
                epoch = Number(identity, "epoch"), segment_id = Number(identity, "segment_id"),
                source_revision = name is "source.partial" or "source.final" ?
                    Number(identity, "applied_source_revision") ?? Number(identity, "source_revision") : Number(identity, "source_revision"),
                translation_request_id = Number(identity, "translation_request_id"),
                audio_start_s = Seconds(payload, "audio_start_s"), audio_end_s = Seconds(payload, "audio_end_s"),
                voice_start_s = Seconds(payload, "voice_start_s"), voice_end_s = Seconds(payload, "voice_end_s"),
                adaptive_policy = AdaptivePolicy(payload), outcome_kind = OutcomeKind(payload),
                adaptive_growth_s = Seconds(payload, "adaptive_growth_s"),
                preview_hold_reason = HoldReason(payload), stable_chars = Number(payload, "stable_chars"), decode_s = Seconds(payload, "decode_s"),
                partial_deferred_wait_s = Seconds(payload, "partial_deferred_wait_s"),
                elapsed_s = Seconds(payload, "elapsed_s"), applied = Flag(payload, "applied"),
                window_attempted = Flag(payload, "window_attempted"), window_fallback = Flag(payload, "window_fallback"),
                queued = Flag(payload, "queued"), preview = Flag(payload, "preview") });
        }
        if (name != "translation.updated" || !payload.TryGetProperty("record", out var record) ||
            record.ValueKind != JsonValueKind.Object) return;
        Write(new { phase = "translation_event_received", at_s = Now, worker_pid = workerPid,
            session_id = Number(record, "session_id"), epoch = Number(record, "epoch"),
            segment_id = Number(record, "segment_id"), source_revision = Number(record, "source_revision"),
            translation_request_id = Number(record, "translation_request_id") });
    }

    private static string? AdaptivePolicy(JsonElement payload)
    {
        if (!payload.TryGetProperty("adaptive_policy", out var value) || value.ValueKind != JsonValueKind.String) return null;
        return value.GetString() is { } policy && policy is "Initial" or "Fixed" or "ConfirmSoon"
            or "StableProgress" or "EmptyBackoff" or "UnchangedBackoff" or "DecodeCostBackoff" ? policy : null;
    }

    private static string? OutcomeKind(JsonElement payload)
    {
        if (!payload.TryGetProperty("outcome_kind", out var value) || value.ValueKind != JsonValueKind.String) return null;
        return value.GetString() is { } kind && kind is "Text" or "NoSpeech" or "OverlapOnly" or "Cancelled" or "Failed" ? kind : null;
    }

    private static string? HoldReason(JsonElement payload)
    {
        if (!payload.TryGetProperty("preview_hold_reason", out var value) || value.ValueKind != JsonValueKind.String) return null;
        return value.GetString() is { } reason && reason is "Eligible" or "Disabled" or "NoStablePrefix"
            or "FinalTranslationQueued" or "FinalAsrQueued" or "FinalTranslationInFlight" or "Cadence"
            or "EmptyTail" or "TooShort" or "AlreadyTranslated" or "IncompleteCondition"
            or "IncompleteNumber" or "ConditionContinuation" or "DanglingWord" or "AsrUnavailable" ? reason : null;
    }

    private static double? Seconds(JsonElement value, string name) =>
        value.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number &&
        element.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 ? number : null;

    private static bool? Flag(JsonElement value, string name) =>
        value.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False ?
            element.GetBoolean() : null;

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
