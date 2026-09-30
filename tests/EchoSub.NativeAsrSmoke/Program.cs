using System.Diagnostics;
using System.Text.Json;
using EchoSub.Desktop;

if (args.Length != 6) throw new ArgumentException("worker model sha256 manifest backend report are required");
var options = new[] { "--diagnostic-asr", "--asr-model", args[1], "--asr-sha256", args[2], "--asr-backend", args[4] };
using var manifest = JsonDocument.Parse(File.ReadAllText(args[3]));
var fixtures = manifest.RootElement.GetProperty("fixtures").EnumerateArray().Select(x => x.Clone()).ToArray();
var root = Path.GetDirectoryName(args[3])!;
var decodeSeconds = new List<double>();
var resetSeconds = new List<double>();
var returnSeconds = new List<double>();
double loadSeconds;
int finals = 0;
int suppressed = 0;
int readyEvents = 0;
double maxPingSeconds = 0;
double shutdownSeconds;

await using (var client = WorkerClient.Start(args[0], arguments: options))
{
    var hello = await client.SendAsync("hello", new { protocol_major = 1, client = "NativeAsrSmoke" });
    Require(hello.GetProperty("implementation").GetString() == "native-asr-fixture", "native implementation");
    var ready = await Event(client, "model.state");
    Require(ready.GetProperty("state").GetString() == "Ready", "model Ready");
    loadSeconds = ready.GetProperty("model_load_s").GetDouble();
    foreach (var fixture in fixtures)
    {
        var accepted = await Submit(client, fixture);
        var segment = accepted.GetProperty("segment_id").GetUInt64();
        if (fixture.GetProperty("kind").GetString() == "silence")
        {
            await Event(client, "fixture.suppressed", segment);
            suppressed++;
            continue;
        }
        var complete = await Event(client, "asr.completed", segment);
        Require(complete.GetProperty("applied").GetBoolean(), "current result applied");
        decodeSeconds.Add(complete.GetProperty("decode_s").GetDouble());
        await Event(client, "source.final", segment);
        var history = await client.ReadHistoryAsync();
        var record = history.Records.Single(x => x.SegmentId == segment);
        Require(record.SourceState == "Final" && !string.IsNullOrWhiteSpace(record.Source), "real source history");
        Require(record.TranslationState == "None" && record.Translation.Length == 0, "translation unavailable");
        Require(record.AppliedSourceRevision == record.SourceRevision, "revision identity");
        Require(record.AudioEndSeconds - record.AudioStartSeconds <= 8.0, "bounded audio duration");
        finals++;
    }
    // Hash errors are asynchronous loader failures and never enter native inference.
    var first = fixtures.First(x => x.GetProperty("language").GetString() == "en");
    var baseline = (await client.ReadHistoryAsync()).Records.First(x => x.SourceState == "Final").Source;
    var bad = await client.SendAsync("transcribe_fixture", new {
        path = Path.GetFullPath(Path.Combine(root, first.GetProperty("path").GetString()!)),
        sha256 = new string('0', 64), language = "en" });
    var failure = await Event(client, "fixture.failed", bad.GetProperty("segment_id").GetUInt64());
    Require(failure.GetProperty("code").GetString() == "INPUT_HASH_MISMATCH", "fixture hash rejection");
    for (int round = 0; round < 10; round++)
    {
        var accepted = await Submit(client, first);
        var oldSegment = accepted.GetProperty("segment_id").GetUInt64();
        await Event(client, "asr.started", oldSegment);
        var wait = Stopwatch.StartNew();
        while (true)
        {
            var state = await client.SendAsync("get_state");
            if (state.GetProperty("diagnostic_asr").GetProperty("native_running").GetBoolean()) break;
            Require(wait.Elapsed.TotalSeconds < 10, "native running observation");
            await Task.Delay(1);
        }
        var clock = Stopwatch.StartNew();
        var reset = await client.SendAsync("reset_fixture_epoch");
        resetSeconds.Add(clock.Elapsed.TotalSeconds);
        // Admit the next epoch while the old context still owns its PCM lease.
        var next = await Submit(client, first);
        var newSegment = next.GetProperty("segment_id").GetUInt64();
        var old = await Event(client, "asr.completed", oldSegment);
        returnSeconds.Add(clock.Elapsed.TotalSeconds);
        Require(!old.GetProperty("applied").GetBoolean(), "old epoch result ignored");
        Require(old.GetProperty("abort_observed").GetBoolean(), "native cancellation observed");
        await Event(client, "source.final", newSegment);
        var history = await client.ReadHistoryAsync();
        Require(history.Records.Single(x => x.SegmentId == oldSegment).SourceState == "Discarded", "cancelled history terminal");
        var record = history.Records.Single(x => x.SegmentId == newSegment);
        Require(record.Epoch == reset.GetProperty("epoch").GetUInt64() && record.SourceState == "Final", "restart final identity");
        Require(record.Source == baseline, "reused context reproduces source");
    }
    var finalState = await client.SendAsync("get_state");
    Require(finalState.GetProperty("diagnostic_asr").GetProperty("completed_jobs").GetInt32() == finals + 20,
        "silence and invalid input made zero ASR calls");
    Require(readyEvents == 1, "context ready once across epoch restarts");
}

// Model hash failure leaves control requests available; no inference context loads.
var badOptions = options.ToArray();
badOptions[4] = new string('0', 64);
await using (var client = WorkerClient.Start(args[0], arguments: badOptions))
{
    await client.SendAsync("hello", new { protocol_major = 1, client = "NativeAsrSmoke" });
    var failed = await Event(client, "model.state");
    Require(failed.GetProperty("state").GetString() == "Failed", "model hash rejection");
    await client.SendAsync("ping", new { nonce = "after-model-failure" });
}
await using (var client = WorkerClient.Start(args[0], arguments: options))
{
    await client.SendAsync("hello", new { protocol_major = 1, client = "NativeAsrSmoke" });
    await Event(client, "model.state");
    var input = await Submit(client, fixtures.First(x => x.GetProperty("kind").GetString() != "silence"));
    await Event(client, "asr.started", input.GetProperty("segment_id").GetUInt64());
    var wait = Stopwatch.StartNew();
    while (!(await client.SendAsync("get_state")).GetProperty("diagnostic_asr").GetProperty("native_running").GetBoolean())
    {
        Require(wait.Elapsed.TotalSeconds < 10, "shutdown native running observation");
        await Task.Delay(1);
    }
    using var process = Process.GetProcessById(client.ProcessId);
    var clock = Stopwatch.StartNew();
    await client.SendAsync("shutdown");
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    shutdownSeconds = clock.Elapsed.TotalSeconds;
    Require(process.ExitCode == 0, "active decode shutdown joined normally");
}
var report = new {
    backend = args[4], model = Path.GetFileName(args[1]), model_sha256 = args[2],
    fixture_kind = "synthetic_tts", vad = false, translation = false, capture = false,
    finals, suppressed, cancellation_restart_rounds = resetSeconds.Count, model_load_s = loadSeconds,
    decode_mean_s = decodeSeconds.Average(), decode_max_s = decodeSeconds.Max(),
    reset_response_max_s = resetSeconds.Max(), native_return_max_s = returnSeconds.Max(),
    active_decode_shutdown_s = shutdownSeconds,
    ping_max_s = maxPingSeconds, fixture_hash_rejected = true, model_hash_rejected = true,
    stale_final_applied = false, passed = true };
File.WriteAllText(args[5], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));

async Task<JsonElement> Submit(WorkerClient client, JsonElement fixture) => await client.SendAsync("transcribe_fixture", new {
    path = Path.GetFullPath(Path.Combine(root, fixture.GetProperty("path").GetString()!)),
    sha256 = fixture.GetProperty("sha256").GetString(), language = fixture.GetProperty("language").GetString() });

async Task<JsonElement> Event(WorkerClient client, string name, ulong? segment = null)
{
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed.TotalSeconds < 30)
    {
        while (client.Events.TryRead(out var message))
        {
            if (message!.Name == "model.state" && message.Payload.GetProperty("state").GetString() == "Ready") readyEvents++;
            if (message!.Name == name && (!segment.HasValue || Segment(message.Payload) == segment))
                return message.Payload;
        }
        // Exercise control response while native load/decode/file I/O runs elsewhere.
        var ping = Stopwatch.StartNew();
        await client.SendAsync("ping", new { nonce = "during-native-work" });
        maxPingSeconds = Math.Max(maxPingSeconds, ping.Elapsed.TotalSeconds);
        await Task.Delay(5);
    }
    throw new TimeoutException($"Missing {name}");
}
static void Require(bool valid, string label)
{
    if (!valid) throw new Exception($"Native ASR smoke failed: {label}");
}
static ulong? Segment(JsonElement payload)
{
    if (payload.TryGetProperty("segment_id", out var id)) return id.GetUInt64();
    if (payload.TryGetProperty("record", out var record)) return record.GetProperty("segment_id").GetUInt64();
    return null;
}
