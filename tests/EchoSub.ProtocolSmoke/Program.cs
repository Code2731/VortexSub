using System.Diagnostics;
using EchoSub.Desktop;

if (args is ["--lines-only"])
{
    CaptionLinesSmoke.Run();
    CaptionPipelineSmoke.Run();
    CaptionDeliverySmoke.Run();
    return 0;
}

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EchoSub.ProtocolSmoke <absolute-worker-path>");
    return 2;
}

var workerPath = Path.GetFullPath(args[0]);
CaptionPresentationSmoke.Run();
CaptionLinesSmoke.Run();
CaptionPipelineSmoke.Run();
CaptionDeliverySmoke.Run();
await HttpTranslationSmoke.Run(workerPath);

await using (var client = WorkerClient.Start(workerPath, arguments: new[] { "--mock-pipeline" }))
{
    int receivedEvents = 0;
    client.EventReceived += (_, payload) =>
    {
        if (payload.ValueKind == System.Text.Json.JsonValueKind.Object) Interlocked.Increment(ref receivedEvents);
    };
    await client.SendAsync("hello", new { client = "SuppressionSmoke", protocol_major = 1 });
    foreach (var outcome in new[] { "no_speech", "overlap_only" })
        await client.SendAsync("mock_segment", new { source = "fixture placeholder", outcome });
    for (var i = 0; i < 2; i++) await client.SendAsync("mock_segment", new { source = "안 돼, 안 돼" });
    var history = await client.ReadHistoryAsync();
    await WaitUntilAsync(() => Volatile.Read(ref receivedEvents) > 0);
    Require(receivedEvents > 0, "timing observer receives parsed worker events without replacing the event buffer");
    Require(history.Records[0].SourceState == "Skipped" && history.Records[0].SourceReason == "NoSpeech", "typed no-speech skip");
    Require(history.Records[1].SourceState == "Skipped" && history.Records[1].SourceReason == "OverlapOnly", "typed overlap-only skip");
    Require(history.Records.Take(2).All(r => r.Source.Length == 0 && r.TranslationState != "Pending"), "suppression does not invent source or translation");
    Require(history.Records.Skip(2).All(r => r.Source == "안 돼, 안 돼" && r.SourceState == "Final"), "real repetition is preserved");
}

await using (var client = WorkerClient.Start(workerPath, arguments: new[] { "--mock-pipeline", "--mock-session-control" }))
{
    var hello = await client.SendAsync("hello", new { client = "PartialSmoke", protocol_major = 1 });
    Require(hello.GetProperty("capabilities").GetProperty("source_partial").GetBoolean(), "explicit mock partial capability");
    var started = await client.SendAsync("start_session", new { history_policy = "retain", config = new { source_language = "en", partial_enabled = true } });
    var id = started.GetProperty("session_id").GetString()!;
    await client.SendAsync("mock_segment", new { kind = "partial", source = "인식 중" });
    var first = (await client.ReadHistoryAsync()).Records.Single();
    Require(first.SourceState == "Partial" && first.AppliedSourceRevision == first.SourceRevision, "typed partial source");
    await client.SendAsync("mock_segment", new { kind = "partial", source = "갱신 원문" });
    var updated = (await client.ReadHistoryAsync()).Records.Single();
    Require(updated.SegmentId == first.SegmentId && updated.SourceRevision > first.SourceRevision, "partial revision replaces same record");
    await client.SendAsync("mock_segment", new { source = "확정 원문" });
    var final = (await client.ReadHistoryAsync()).Records.Single();
    Require(final.SourceState == "Final" && final.SegmentId == first.SegmentId && final.SourceRevision > updated.SourceRevision, "final freezes same segment");
    await client.SendAsync("mock_segment", new { kind = "partial", source = "중단 원문" });
    await client.SendAsync("pause_session", new { session_id = id });
    var paused = await client.ReadHistoryAsync();
    Require(paused.Records[^1].SourceState == "Discarded", "pause discards active partial");
    await client.SendAsync("resume_session", new { session_id = id });
    await client.SendAsync("mock_segment", new { source = "재개 확정" });
    var resumed = await client.ReadHistoryAsync();
    Require(resumed.Records[^1].SegmentId > paused.Records[^1].SegmentId && resumed.Records[^1].Epoch > first.Epoch, "resume allocates fresh partial identity");
}

await using (var client = WorkerClient.Start(workerPath))
{
    var processId = client.ProcessId;
    var hello = await client.SendAsync("hello", new { client = "ProtocolSmoke", protocol_major = 1 });
    Require(hello.GetProperty("implementation").GetString() == "mock", "mock capability");
    Require(!hello.GetProperty("capabilities").GetProperty("system_audio").GetBoolean(), "system audio must be unavailable");
    Require(!hello.GetProperty("capabilities").GetProperty("fixture_asr").GetBoolean(), "native fixtures require opt-in");
    Require(!hello.GetProperty("capabilities").GetProperty("vad").GetBoolean(), "VAD fixtures require opt-in");
    Require(!hello.GetProperty("capabilities").GetProperty("live_asr").GetBoolean(), "live ASR requires opt-in");
    Require(!hello.GetProperty("capabilities").GetProperty("source_token_alignment").GetBoolean(), "token alignment requires native owner");
    Require(!hello.GetProperty("capabilities").GetProperty("history_export").GetBoolean(), "history export requires UUID mode");
    Require(!hello.GetProperty("capabilities").GetProperty("history_clear").GetBoolean(), "clear requires UUID mode");
    foreach (var method in new[] { "transcribe_fixture", "reset_fixture_epoch", "start_capture", "stop_capture", "start_session", "pause_session", "resume_session", "stop_session", "export_history", "clear_history" })
    {
        try { await client.SendAsync(method); throw new Exception("Native fixture opt-in was bypassed"); }
        catch (WorkerException error) when (error.Code == "UNSUPPORTED_CAPABILITY") { }
    }

    var nonce = "한국어 日本語 😊\nnext";
    var ping = await client.SendAsync("ping", new { nonce });
    Require(ping.GetProperty("nonce").GetString() == nonce, "Unicode/newline round trip");

    var state = await client.SendAsync("get_state");
    Require(state.GetProperty("session").GetProperty("state").GetString() == "Idle", "initial state");
    Require(!state.GetProperty("diagnostic_asr").GetProperty("adaptive_partials").GetBoolean(), "adaptive ASR requires explicit fast mode");
    Require(!state.GetProperty("diagnostic_asr").GetProperty("decode_window_enabled").GetBoolean()
        && state.GetProperty("diagnostic_asr").GetProperty("window_attempts").GetUInt64() == 0
        && state.GetProperty("diagnostic_asr").GetProperty("window_fallbacks").GetUInt64() == 0, "decode windows require explicit opt-in");
    var schedule = state.GetProperty("diagnostic_asr").GetProperty("partial_scheduler");
    Require(schedule.GetProperty("adaptive_growth_s").GetDouble() == 0.512 && schedule.GetProperty("adaptive_deferred").GetUInt64() == 0, "initial adaptive state is bounded and uses seconds");
    Require(!schedule.GetProperty("pending").GetBoolean(), "initial deferred partial is empty");
    foreach (var field in new[] { "requested", "deferred", "replaced", "dropped", "asr_applied", "asr_ignored" })
        Require(schedule.GetProperty(field).GetUInt64() == 0, $"initial scheduler {field}");
    Require(schedule.GetProperty("asr_decode_total_s").GetDouble() == 0 && schedule.GetProperty("last_deferred_wait_s").GetDouble() == 0, "scheduler durations use seconds");
    var capture = state.GetProperty("diagnostic_capture");
    Require(capture.GetProperty("startup_deadline_s").GetDouble() == 10, "capture startup deadline in seconds");
    Require(capture.GetProperty("opening_elapsed_s").ValueKind == System.Text.Json.JsonValueKind.Null, "idle capture has no opening duration");
    Require(capture.GetProperty("failure_native_phase").ValueKind == System.Text.Json.JsonValueKind.Null, "idle capture has no failure phase");

    try
    {
        await client.SendAsync("start_session");
        throw new Exception("Unsupported start_session unexpectedly succeeded");
    }
    catch (WorkerException error) when (error.Code == "UNSUPPORTED_CAPABILITY")
    {
    }

    var calls = Enumerable.Range(0, 64)
        .Select(index => client.SendAsync("ping", new { nonce = index.ToString() }))
        .ToArray();
    var responses = await Task.WhenAll(calls);
    for (var index = 0; index < responses.Length; index++)
    {
        Require(responses[index].GetProperty("nonce").GetString() == index.ToString(), "request response matching");
    }
    await client.DisposeAsync();
    Require(!IsAlive(processId), "normal shutdown left worker alive");
}

await using (var client = WorkerClient.Start(workerPath, arguments: new[] { "--mock-pipeline", "--mock-session-control" }))
{
    var hello = await client.SendAsync("hello", new { client = "SessionSmoke", protocol_major = 1 });
    Require(hello.GetProperty("capabilities").GetProperty("session_control").GetBoolean(), "explicit mock session capability");
    var config = new { history_policy = "retain", config = new { source_language = "en" } };
    var accepted = await client.SendAsync("start_session", config);
    var id = accepted.GetProperty("session_id").GetString()!;
    Require(Guid.TryParseExact(id, "D", out _), "worker UUID session identity");
    await client.SendAsync("mock_segment", new { source = "세션 원문" });
    var before = await client.ReadHistoryAsync();
    Require(before.Records[0].ProductSessionId == id && before.Records[0].SessionAudioStartSeconds >= 0, "UUID/session time in typed history");
    Require(DateTimeOffset.TryParse(before.Records[0].SessionStartedAtUtc, out var startedUtc) && startedUtc.Offset == TimeSpan.Zero, "UTC timestamp in history");
    await client.SendAsync("pause_session", new { session_id = id });
    var paused = await client.SendAsync("get_state");
    Require(paused.GetProperty("session").GetProperty("state").GetString() == "Paused", "paused state");
    Require(paused.GetProperty("session").GetProperty("epoch").GetUInt64() > before.Records[0].Epoch, "pause invalidates epoch");
    try { await client.SendAsync("resume_session", new { session_id = Guid.NewGuid().ToString() }); throw new Exception("Stale session accepted"); }
    catch (WorkerException error) when (error.Code == "STALE_SESSION") { }
    await client.SendAsync("resume_session", new { session_id = id });
    await client.SendAsync("mock_segment", new { source = "재개 원문" });
    var after = await client.ReadHistoryAsync();
    Require(after.Records.Count == 2 && after.Records[0].Source == "세션 원문", "history retained across pause");
    Require(after.Records[1].SegmentId > before.Records[0].SegmentId && after.Records[1].Epoch > before.Records[0].Epoch, "resume identity monotonic");
    Require(after.Records.All(r => r.ProductSessionId == id), "UUID survives Resume");
    await client.SendAsync("stop_session", new { session_id = id });
    var stopped = await client.SendAsync("get_state");
    Require(stopped.GetProperty("session").GetProperty("state").GetString() == "Idle", "mock stop reaches idle");
    Require(stopped.GetProperty("session").GetProperty("started_at_utc").GetString() == before.Records[0].SessionStartedAtUtc, "UTC retained through Pause/Resume/Stop");
    var next = await client.SendAsync("start_session", config);
    Require(next.GetProperty("session_id").GetString() != id, "new session has fresh UUID");
    Require((await client.ReadHistoryAsync()).Records.Count == 2, "new session retains old history");
    await client.SendAsync("mock_segment", new { source = "새 UUID 원문" });
    var mixed = await client.ReadHistoryAsync();
    Require(mixed.Records[0].ProductSessionId == id && mixed.Records[^1].ProductSessionId == next.GetProperty("session_id").GetString(), "UUID preserved across sessions");
}

await using (var client = WorkerClient.Start(workerPath, arguments: new[] { "--mock-pipeline", "--mock-session-control" }))
{
    await client.SendAsync("hello", new { client = "ExportSmoke", protocol_major = 1 });
    var start = await client.SendAsync("start_session", new { history_policy = "retain", config = new { source_language = "en" } });
    var id = start.GetProperty("session_id").GetString()!;
    await client.SendAsync("mock_segment", new { source = "한국어\r\n\r\n日本語 😊" });
    var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EchoSub-export-" + Guid.NewGuid()));
    Directory.CreateDirectory(directory);
    var txt = Path.Combine(directory, "history.txt");
    var srt = Path.Combine(directory, "history.srt");
    Exception? exportFailure = null;
    try
    {
        async Task Reject(string code, object p)
        {
            try { await client.SendAsync("export_history", p); throw new Exception("Export guard bypassed: " + code); }
            catch (WorkerException e) when (e.Code == code) { }
        }
        await Reject("INVALID_STATE", new { session_id = id, format = "txt", path = txt, overwrite = false });
        Require(!File.Exists(txt), "running export never writes");
        await client.SendAsync("pause_session", new { session_id = id });
        var result = await client.SendAsync("export_history", new { session_id = id, format = "txt", path = txt, overwrite = false });
        Require(result.GetProperty("record_count").GetInt32() == 1, "TXT retained record count");
        var content = File.ReadAllText(txt);
        Require(content.Contains("한국어\r\n\r\n日本語 😊") && content.Contains(id) && content.Contains("Started UTC:"), "TXT Unicode/UTC round trip");
        await Reject("EXPORT_EXISTS", new { session_id = id, format = "txt", path = txt, overwrite = false });
        Require(File.ReadAllText(txt) == content, "existing export preserved");
        await Reject("INVALID_REQUEST", new { session_id = id, format = "csv", path = srt, overwrite = true });
        await Reject("INVALID_REQUEST", new { session_id = id, format = "srt", path = "relative.srt", overwrite = true });
        await client.SendAsync("export_history", new { session_id = id, format = "srt", path = srt, overwrite = false });
        Require(File.ReadAllText(srt).StartsWith("1\n") && File.ReadAllText(srt).Contains(" --> ") && File.ReadAllText(srt).Contains("한국어\n日本語 😊"), "SRT consecutive cue/Unicode/blank line normalization");
        await client.SendAsync("export_history", new { session_id = id, format = "srt", path = txt, overwrite = true });
        Require(File.ReadAllText(txt) == File.ReadAllText(srt), "explicit replacement writes complete file");
        Require(Directory.GetFiles(directory, ".echosub-*.tmp").Length == 0, "no staging file remains");
        await client.SendAsync("stop_session", new { session_id = id });
        var next = await client.SendAsync("start_session", new { history_policy = "retain", config = new { source_language = "en" } });
        await client.SendAsync("stop_session", new { session_id = next.GetProperty("session_id").GetString() });
        await Reject("EMPTY_HISTORY", new { session_id = next.GetProperty("session_id").GetString(), format = "txt", path = txt, overwrite = true });
        await client.SendAsync("export_history", new { session_id = id, format = "srt", path = srt, overwrite = true });
        Require(File.ReadAllText(srt).Contains("日本語"), "retained older UUID export");
        var saved = File.ReadAllText(srt);
        var removed = await client.SendAsync("clear_history", new { session_id = id });
        Require(removed.GetProperty("removed_count").GetInt32() == 1 && (await client.ReadHistoryAsync()).Records.Count == 0, "clear typed retained history");
        Require(File.ReadAllText(srt) == saved, "clear never deletes exported files");
        await Reject("STALE_SESSION", new { session_id = id, format = "srt", path = srt, overwrite = true });
    }
    catch (Exception error) { exportFailure = error; throw; }
    finally
    {
        // A timed-out command may still be writing. Stop the owned worker first.
        try
        {
            await client.DisposeAsync();
            File.Delete(txt); File.Delete(srt); Directory.Delete(directory);
        }
        catch (Exception error) when (exportFailure is not null)
        {
            Console.Error.WriteLine($"Export cleanup also failed: {error.Message}");
        }
    }
}

await using (var client = WorkerClient.Start(workerPath))
{
    await client.SendAsync("hello", new { client = "ProtocolSmoke", protocol_major = 1 });
    var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    client.Disconnected += () => disconnected.TrySetResult();
    using (var process = Process.GetProcessById(client.ProcessId))
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
    await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    try
    {
        await client.SendAsync("ping", new { nonce = "after-kill" });
        throw new Exception("Killed worker unexpectedly accepted a request");
    }
    catch (IOException)
    {
    }
}

// Deterministic client-side loss/recovery, independent of OS pipe scheduling.
var eventBuffer = new WorkerEventBuffer();
var payload = System.Text.Json.JsonSerializer.SerializeToElement(new { value = "mock" });
eventBuffer.Publish(new WorkerEvent(2, "source.final", payload));
Require(eventBuffer.SnapshotRequired, "first sequence gap must require snapshot");
eventBuffer.AcceptSnapshot(2);
Require(!eventBuffer.SnapshotRequired, "snapshot covers sequence gap");
eventBuffer.Publish(new WorkerEvent(1, "source.final", payload));
Require(!eventBuffer.TryRead(out _), "delayed event covered by snapshot must be ignored");
for (ulong sequence = 3; sequence <= 259; sequence++) eventBuffer.Publish(new WorkerEvent(sequence, "source.final", payload));
Require(eventBuffer.SnapshotRequired, "bounded client queue overflow must require snapshot");
eventBuffer.AcceptSnapshot(258);
Require(eventBuffer.SnapshotRequired, "older snapshot cannot cover a newer drop");
eventBuffer.AcceptSnapshot(259);
Require(!eventBuffer.SnapshotRequired, "fresh snapshot covers client overflow");
eventBuffer.Publish(new WorkerEvent(260, "snapshot.required", payload));
Require(eventBuffer.SnapshotRequired, "explicit recovery event");
eventBuffer.Publish(new WorkerEvent(261, "source.final", payload));
eventBuffer.AcceptSnapshot(260);
Require(eventBuffer.TryRead(out var future) && future!.Sequence == 261, "snapshot retains newer queued event");

await using (var client = WorkerClient.Start(workerPath, enableMockPipeline: true))
{
    var hello = await client.SendAsync("hello", new { client = "MockProtocolSmoke", protocol_major = 1 });
    Require(hello.GetProperty("capabilities").GetProperty("mock_pipeline").GetBoolean(), "mock opt-in capability");
    Require(!hello.GetProperty("capabilities").GetProperty("asr").GetBoolean(), "mock must not advertise real inference");
    var admitted = await client.SendAsync("mock_segment", new { source = "한국어 日本語 🙂\nnext" });
    await WaitUntilAsync(() => client.Events.LastSequence >= admitted.GetProperty("last_seq").GetUInt64());
    Require(client.Events.TryRead(out var sourceEvent) && sourceEvent!.Name == "source.final", "asynchronous source event");
    var history = await client.ReadHistoryAsync();
    Require(history.Records.Count == 1, "typed mock history");
    var record = history.Records[0];
    Require(record.Source == "한국어 日本語 🙂\nnext", "history Unicode/newline");
    Require(record.SourceState == "Final" && record.TranslationState == "Pending", "final-only translation state");
    Require(record.AudioEndSeconds == 0.032, "history time in seconds");
    await client.SendAsync("mock_translate", new { session_id = record.SessionId, epoch = record.Epoch, source_revision = record.SourceRevision, segment_id = record.SegmentId, translation_request_id = record.TranslationRequestId, text = "번역\n🙂" });
    try
    {
        await client.SendAsync("get_history", new { expected_version = history.Version });
        throw new Exception("Stale snapshot unexpectedly succeeded");
    }
    catch (WorkerException error) when (error.Code == "STALE_SNAPSHOT") { }
    history = await client.ReadHistoryAsync();
    Require(history.Records[0].Translation == "번역\n🙂", "typed translated snapshot");
    var burst = await client.SendAsync("mock_burst", new { count = 300, source = "mock burst" });
    await WaitUntilAsync(() => client.Events.LastSequence >= burst.GetProperty("last_seq").GetUInt64());
    var ping = await client.SendAsync("ping", new { nonce = "after-events" });
    Require(ping.GetProperty("nonce").GetString() == "after-events", "event pressure must not block responses");
    history = await client.ReadHistoryAsync();
    Require(history.Records.Count == 301, "multi-page history recovery");
    Require(!client.Events.SnapshotRequired, "snapshot clears covered recovery state");
}
Console.WriteLine("PASS: C# client ↔ Rust worker, Unicode, 64 requests, bounded events, typed history/recovery, shutdown, worker death");
return 0;

static void Require(bool condition, string label)
{
    if (!condition) throw new Exception($"FAILED: {label}");
}

static bool IsAlive(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}


static async Task WaitUntilAsync(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (!condition()) await Task.Delay(10, timeout.Token);
}
