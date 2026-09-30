using System.Diagnostics;
using EchoSub.Desktop;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: EchoSub.ProtocolSmoke <absolute-worker-path>");
    return 2;
}

var workerPath = Path.GetFullPath(args[0]);

await using (var client = WorkerClient.Start(workerPath))
{
    var processId = client.ProcessId;
    var hello = await client.SendAsync("hello", new { client = "ProtocolSmoke", protocol_major = 1 });
    Require(hello.GetProperty("implementation").GetString() == "mock", "mock capability");
    Require(!hello.GetProperty("capabilities").GetProperty("system_audio").GetBoolean(), "system audio must be unavailable");
    Require(!hello.GetProperty("capabilities").GetProperty("fixture_asr").GetBoolean(), "native fixtures require opt-in");
    Require(!hello.GetProperty("capabilities").GetProperty("vad").GetBoolean(), "VAD fixtures require opt-in");
    foreach (var method in new[] { "transcribe_fixture", "reset_fixture_epoch", "start_capture", "stop_capture" })
    {
        try { await client.SendAsync(method); throw new Exception("Native fixture opt-in was bypassed"); }
        catch (WorkerException error) when (error.Code == "UNSUPPORTED_CAPABILITY") { }
    }

    var nonce = "한국어 日本語 😊\nnext";
    var ping = await client.SendAsync("ping", new { nonce });
    Require(ping.GetProperty("nonce").GetString() == nonce, "Unicode/newline round trip");

    var state = await client.SendAsync("get_state");
    Require(state.GetProperty("session").GetProperty("state").GetString() == "Idle", "initial state");

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
