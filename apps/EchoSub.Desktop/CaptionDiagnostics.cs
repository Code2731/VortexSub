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
        if (name is "capture.state" or "session.state" or "translator.state")
        {
            var state = MetadataToken(payload, "state");
            var error = MetadataToken(payload, name == "translator.state" ? "last_error" : "error");
            StartupDiagnostics.Write($"Worker {name}; PID={workerPid}; state={state}; error={error ?? "none"}; elapsed_s={Seconds(payload, "elapsed_s")}; accepted_audio_s={Seconds(payload, "accepted_audio_s")}");
            if (Enabled) Write(new { phase = "worker_state_received", at_s = Now, worker_pid = workerPid,
                event_name = name, state, error, epoch = Number(payload, "epoch"),
                session_id = Number(payload, "internal_session_id"),
                translator_error = MetadataToken(payload, "last_error"),
                input_profile = InputProfile(payload),
                elapsed_s = Seconds(payload, "elapsed_s"), accepted_audio_s = Seconds(payload, "accepted_audio_s") });
        }
        if (!Enabled) return;
        if (name is "capture.voice_observed" or "capture.asr_eligible" or "capture.partial_deferred" or "capture.segment_discarded")
        {
            Write(new { phase = "pre_asr_event_received", at_s = Now, worker_pid = workerPid,
                event_name = name, worker_at_s = Seconds(payload, "worker_at_s"),
                session_id = Number(payload, "session_id"), epoch = Number(payload, "epoch"),
                vad_segment_id = Number(payload, "vad_segment_id"),
                observed_worker_s = Seconds(payload, "observed_worker_s"),
                processing_started_worker_s = Seconds(payload, "processing_started_worker_s"),
                processing_kind = payload.TryGetProperty("processing_kind", out var processingKind) &&
                    processingKind.ValueKind == JsonValueKind.String && processingKind.GetString() is "push" or "poll"
                    ? processingKind.GetString() : null,
                vad_processing_s = Seconds(payload, "vad_processing_s"),
                voice_observed_worker_s = Seconds(payload, "voice_observed_worker_s"),
                voice_vad_started_worker_s = Seconds(payload, "voice_vad_started_worker_s"),
                first_eligible_worker_s = Seconds(payload, "first_eligible_worker_s"),
                first_voiced_frame_audio_s = Seconds(payload, "first_voiced_frame_audio_s"),
                partial_enabled = Flag(payload, "partial_enabled"),
                asr_busy = Flag(payload, "asr_busy"), preview_busy = Flag(payload, "preview_busy"),
                adaptive_wait = Flag(payload, "adaptive_wait") });
        }
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
                vad_segment_id = Number(payload, "vad_segment_id"),
                voice_observed_worker_s = Seconds(payload, "voice_observed_worker_s"),
                voice_vad_started_worker_s = Seconds(payload, "voice_vad_started_worker_s"),
                first_eligible_worker_s = Seconds(payload, "first_eligible_worker_s"),
                first_admitted_worker_s = Seconds(payload, "first_admitted_worker_s"),
                first_voiced_frame_audio_s = Seconds(payload, "first_voiced_frame_audio_s"),
                deferred_asr_observations = Number(payload, "deferred_asr_observations"),
                deferred_preview_observations = Number(payload, "deferred_preview_observations"),
                deferred_adaptive_observations = Number(payload, "deferred_adaptive_observations"),
                adaptive_policy = AdaptivePolicy(payload), outcome_kind = OutcomeKind(payload),
                adaptive_growth_s = Seconds(payload, "adaptive_growth_s"),
                preview_hold_reason = HoldReason(payload), stable_chars = Number(payload, "stable_chars"), decode_s = Seconds(payload, "decode_s"),
                partial_deferred_wait_s = Seconds(payload, "partial_deferred_wait_s"),
                elapsed_s = Seconds(payload, "elapsed_s"), error = MetadataToken(payload, "error"), applied = Flag(payload, "applied"),
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

    // State names and error codes only: never retain subtitle text or endpoint names.
    private static string? MetadataToken(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var token = value.GetString();
        return token is { Length: > 0 and <= 80 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? token : null;
    }

    public static void SnapshotApplied(int workerPid, HistorySnapshot snapshot, ulong internalSession, ulong epoch)
    {
        if (!Enabled) return;
        var latest = snapshot.Records.LastOrDefault(r => r.SessionId == internalSession && r.Epoch == epoch);
        Write(new { phase = "history_snapshot_applied", at_s = Now, worker_pid = workerPid,
            history_version = snapshot.Version, records = snapshot.Records.Count,
            session_id = internalSession, epoch, segment_id = latest?.SegmentId,
            source_revision = latest?.SourceRevision, applied_source_revision = latest?.AppliedSourceRevision,
            source_state = latest?.SourceState, translation_state = latest?.TranslationState,
            translation_request_id = latest?.TranslationRequestId, preview = latest?.TranslationIsPreview });
    }

    public static void StatePolled(int workerPid, JsonElement state, ulong? snapshotVersion)
    {
        var recording = Volatile.Read(ref active);
        if (recording is null) return;
        var capture = state.GetProperty("diagnostic_capture");
        var session = state.GetProperty("session");
        var translator = state.GetProperty("translator");
        var asr = state.GetProperty("diagnostic_asr");
        var observed = new Context(workerPid, Number(session, "internal_session_id"), Number(asr, "epoch"),
            MetadataToken(session, "state"), MetadataToken(capture, "state"), MetadataToken(translator, "state"),
            ModelId(translator), InputProfile(translator), Flag(translator, "isolated_context"),
            MetadataToken(capture, "error"), MetadataToken(translator, "last_error"));
        var now = Now;
        var changed = recording.Context != observed;
        recording.Context = observed;
        if (!changed && now - recording.LastStatePoll < 5) return;
        recording.LastStatePoll = now;
        Write(new { phase = "worker_state_polled", at_s = Now, worker_pid = workerPid,
            context_changed = changed, session_id = observed.Session, epoch = observed.Epoch,
            model_id = observed.Model, input_profile = observed.Profile, isolated_context = observed.Isolated,
            session_state = MetadataToken(session, "state"), capture_state = MetadataToken(capture, "state"),
            capture_error = MetadataToken(capture, "error"), accepted_audio_s = Seconds(capture, "accepted_audio_s"),
            history_version = Number(state, "history_version"), snapshot_version = snapshotVersion,
            decoding = Flag(asr, "decoding"), native_running = Flag(asr, "native_running"),
            pending_asr_inputs = Number(asr, "pending_inputs"),
            translator_state = MetadataToken(translator, "state"), translator_error = observed.TranslationError,
            catalog_pending = Flag(translator, "catalog_pending"),
            translation_completed_jobs = Number(translator, "completed_jobs"),
            translation_in_flight = Flag(translator, "in_flight") });
    }

    private sealed record Context(int WorkerPid, ulong? Session, ulong? Epoch, string? SessionState,
        string? CaptureState, string? TranslatorState, string? Model, string? Profile, bool? Isolated,
        string? CaptureError, string? TranslationError);

    private static string? InputProfile(JsonElement payload) =>
        payload.TryGetProperty("input_profile", out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } profile && profile is "standard" or "qwen-greedy" or "hymt2-greedy" ? profile : null;

    private static string? ModelId(JsonElement payload)
    {
        if (!payload.TryGetProperty("model_id", out var value) || value.ValueKind != JsonValueKind.String) return null;
        var id = value.GetString();
        return id is { Length: > 0 and <= 256 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? id : null;
    }

    public static void Cleared(string reason, int? workerPid)
    {
        if (Enabled) Write(new { phase = "deck_cleared", at_s = Now, worker_pid = workerPid, reason });
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
            or "IncompleteNumber" or "ConditionContinuation" or "DanglingWord" or "AsrUnavailable"
            or "IncompleteRepair" or "RepairTooLong" or "InvalidRepair" ? reason : null;
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
        var context = Volatile.Read(ref active)?.Context;
        Write(new { phase = "deck_applied", at_s = Now, worker_pid = workerPid, session_id = record.SessionId,
            epoch = record.Epoch, segment_id = record.SegmentId, source_revision = record.SourceRevision,
            translation_request_id = record.TranslationRequestId, slot = timing.Slot,
            deferred_s = timing.DeferredSeconds, overlay_visible = overlayVisible,
            context_matches = context is null ? (bool?)null : context.WorkerPid == workerPid &&
                context.Session == record.SessionId && context.Epoch == record.Epoch &&
                context.SessionState == "Running" && context.CaptureState == "Running" });
    }

    public static void OverlayAssigned(CaptionCards cards)
    {
        if (!Enabled) return;
        Write(new { phase = "overlay_assigned", at_s = Now, previous = cards.Previous is not null,
            current = cards.Current is not null });
    }

    public static void LineAssigned(string slot, CaptionIdentity? identity, int characters)
    {
        if (!Enabled) return;
        Write(new { phase = "caption_line_assigned", at_s = Now,
            worker_pid = Volatile.Read(ref active)?.Context?.WorkerPid, slot,
            session_id = identity?.Session, epoch = identity?.Epoch,
            segment_id = identity?.Segment, unit_start_utf16 = identity?.UnitStart,
            characters, has_text = characters > 0 });
    }

    public static void ReadingObserved(CaptionReadingObservation observation, bool overlayVisible)
    {
        if (!Enabled) return;
        var key = observation.Identity;
        Write(new { phase = "caption_reading", at_s = Now,
            worker_pid = Volatile.Read(ref active)?.Context?.WorkerPid, overlay_visible = overlayVisible,
            session_id = key?.Session, epoch = key?.Epoch, segment_id = key?.Segment,
            unit_start_utf16 = key?.UnitStart, kind = observation.Kind, reason = observation.Reason,
            reading_wait_s = observation.WaitSeconds, pending_units = observation.PendingUnits,
            blocked_s = observation.BlockedSeconds, reused_characters = observation.ReusedCharacters,
            characters = observation.Characters, queue_capacity = CaptionLines.MaximumPendingUnits,
            queue_max_age_s = CaptionLines.MaximumPendingSeconds,
            line_hold_s = CaptionLines.HoldSeconds, line_stagger_s = CaptionLines.LineStaggerSeconds });
    }

    public static void CaptionEventOverflow(int workerPid, ulong dropped)
    {
        var recording = Volatile.Read(ref active);
        if (recording is null || recording.CaptionWorker == workerPid && recording.CaptionDrops == dropped) return;
        recording.CaptionWorker = workerPid; recording.CaptionDrops = dropped;
        Write(new { phase = "caption_event_buffer", at_s = Now, worker_pid = workerPid,
            dropped_events = dropped, capacity = WorkerEventBuffer.MaximumCaptionEvents });
    }

    public static void TargetObserved(CaptionTargetObservation observation, int? workerPid, bool overlayVisible)
    {
        if (!Enabled) return;
        var record = observation.Record;
        // Metadata only: candidate/source text stays out of the normal timing file.
        Write(new { phase = "target_change_observed", at_s = Now, worker_pid = workerPid,
            observation_phase = observation.Phase, slot = observation.Slot,
            overlay_visible = overlayVisible,
            deck_at_s = observation.AtSeconds, session_id = record.SessionId, epoch = record.Epoch,
            policy_not_before_s = observation.NotBeforeSeconds,
            segment_id = record.SegmentId, source_revision = record.SourceRevision,
            translation_request_id = record.TranslationRequestId, change = observation.Change,
            semantic_contradiction = "UNASSESSED" });
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
        public Context? Context;
        public int? CaptionWorker;
        public ulong CaptionDrops;
        public double LastStatePoll;
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
