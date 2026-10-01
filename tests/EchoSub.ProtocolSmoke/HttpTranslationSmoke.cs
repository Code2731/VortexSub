using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EchoSub.Desktop;

internal static class HttpTranslationSmoke
{
    public static async Task Run(string worker)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/";
        using var stop = new CancellationTokenSource();
        int posts = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                    using var stream = socket.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
                    var first = await reader.ReadLineAsync(stop.Token);
                    int length = 0;
                    while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..]);
                    if (length > 64 * 1024) throw new Exception("Fixture request too large");
                    var chars = new char[length];
                    if (length > 0) await reader.ReadBlockAsync(chars, stop.Token);
                    int status = 200;
                    string body;
                    if (first!.StartsWith("GET /v1/models ")) body = "{\"data\":[{\"id\":\"fixture/model\"}]}";
                    else
                    {
                        Interlocked.Increment(ref posts);
                        using var request = JsonDocument.Parse(new string(chars));
                        var messages = request.RootElement.GetProperty("messages");
                        using var payload = JsonDocument.Parse(messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!);
                        if (payload.RootElement.GetProperty("source_text").GetString() == "fail") { status = 401; body = "server-private-detail"; }
                        else body = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"번역 완료\",\"tool_calls\":null}}]}";
                    }
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                    await stream.WriteAsync(bytes, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        });
        var timingPath = Path.Combine(Path.GetTempPath(), $"echosub-timing-smoke-{Guid.NewGuid():N}.jsonl");
        try
        {
            await CaptionDiagnostics.SetEnabledAsync(true, timingPath);
            await using var client = WorkerClient.Start(worker, arguments: new[] { "--mock-pipeline", "--diagnostic-translation" });
            client.EventReceived += (name, payload) => CaptionDiagnostics.Received(client.ProcessId, name, payload);
            var hello = await client.SendAsync("hello", new { client = "HttpTranslationSmoke", protocol_major = 1 });
            Require(hello.GetProperty("capabilities").GetProperty("translation").GetBoolean(), "HTTP capability");
            await client.SendAsync("configure_translation", new { endpoint, model_id = "fixture/model" });
            var clock = Stopwatch.StartNew();
            while ((await client.SendAsync("get_state")).GetProperty("translator").GetProperty("state").GetString() != "Ready")
            {
                Require(clock.Elapsed.TotalSeconds < 4, "Catalog Ready"); await Task.Delay(10);
            }
            await client.SendAsync("mock_segment", new { source = "Don't move: 42 enemies." });
            var first = await Terminal(client, 1);
            Require(first.SourceState == "Final" && first.Source == "Don't move: 42 enemies." && first.Translation == "번역 완료" && first.TranslationState == "Done", "typed final and translation");
            Require(first.TranslationRequestId is > 0 && first.AppliedSourceRevision == first.SourceRevision, "full applied source identity");
            await client.SendAsync("mock_segment", new { source = "fail" });
            var failed = await Terminal(client, 2);
            Require(failed.Source == "fail" && failed.SourceState == "Final" && failed.TranslationState == "Failed" && failed.Translation.Length == 0, "HTTP failure preserves source");
            Require(Volatile.Read(ref posts) == 2, "401 is not retried");
            var state = await client.SendAsync("get_state");
            Require(!state.ToString().Contains("server-private-detail"), "server body excluded from IPC state");
            await client.SendAsync("disable_translation");
            using (var metadata = JsonDocument.Parse("""{"worker_at_s":1,"stable_chars":15,"preview_hold_reason":"IncompleteNumber","adaptive_policy":"ConfirmSoon","adaptive_growth_s":0.256,"outcome_kind":"Text","window_attempted":true,"window_fallback":true,"record":{"session_id":1,"epoch":1,"segment_id":99,"source_revision":1,"source":"private transcript"}}"""))
                CaptionDiagnostics.Received(client.ProcessId, "source.partial", metadata.RootElement);
            using (var metadata = JsonDocument.Parse("""{"worker_at_s":1,"preview_hold_reason":"private hold detail","adaptive_policy":"private policy","outcome_kind":"private outcome","record":{"session_id":1,"epoch":1,"segment_id":99,"source_revision":1}}"""))
                CaptionDiagnostics.Received(client.ProcessId, "source.partial", metadata.RootElement);
            await CaptionDiagnostics.DrainAsync();
            var timingLines = File.ReadAllLines(timingPath);
            var timingRows = timingLines.Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            var started = timingRows.Single(row => row.GetProperty("phase").GetString() == "pipeline_event_received"
                && row.GetProperty("event_name").GetString() == "translation.started"
                && row.GetProperty("translation_request_id").GetUInt64() == first.TranslationRequestId);
            var completed = timingRows.Single(row => row.GetProperty("phase").GetString() == "pipeline_event_received"
                && row.GetProperty("event_name").GetString() == "translation.completed"
                && row.GetProperty("translation_request_id").GetUInt64() == first.TranslationRequestId);
            Require(completed.GetProperty("worker_at_s").GetDouble() >= started.GetProperty("worker_at_s").GetDouble(), "worker timing shares one origin");
            Require(started.GetProperty("source_revision").GetUInt64() == first.SourceRevision, "timing source identity");
            Require(timingRows.Any(row => row.TryGetProperty("preview_hold_reason", out var reason) && reason.GetString() == "IncompleteNumber"), "known hold reason logged");
            Require(timingRows.Any(row => row.TryGetProperty("adaptive_policy", out var policy) && policy.GetString() == "ConfirmSoon"), "adaptive policy metadata logged");
            Require(timingRows.Any(row => row.TryGetProperty("window_attempted", out var attempted) && attempted.ValueKind == JsonValueKind.True && row.GetProperty("window_fallback").ValueKind == JsonValueKind.True), "window fallback flags logged without text");
            Require(!string.Join("\n", timingLines).Contains("private"), "hold reason whitelist excludes unrecognized text");
            Require(!string.Join("\n", timingLines).Contains("Don't move") && !string.Join("\n", timingLines).Contains("번역 완료"), "timing excludes transcript and translation text");
            await using var previewClient = WorkerClient.Start(worker, arguments: new[] { "--mock-pipeline", "--mock-session-control", "--diagnostic-translation" });
            var previewHello = await previewClient.SendAsync("hello", new { client = "HttpPreviewSmoke", protocol_major = 1 });
            Require(previewHello.GetProperty("capabilities").GetProperty("partial_translation").GetBoolean(), "preview capability");
            await previewClient.SendAsync("configure_translation", new { endpoint, model_id = "fixture/model" });
            clock.Restart();
            while ((await previewClient.SendAsync("get_state")).GetProperty("translator").GetProperty("state").GetString() != "Ready")
            {
                Require(clock.Elapsed.TotalSeconds < 4, "Preview catalog Ready"); await Task.Delay(10);
            }
            await previewClient.SendAsync("start_session", new { history_policy = "retain", config = new { source_language = "en", partial_enabled = true, partial_translation_enabled = true } });
            await previewClient.SendAsync("mock_segment", new { source = "We should take the left", kind = "partial" });
            Require((await previewClient.ReadHistoryAsync()).Records[0].StableSource.Length == 0, "first partial is unconfirmed");
            await previewClient.SendAsync("mock_segment", new { source = "We should take the left path.", kind = "partial" });
            var previewRecord = await Terminal(previewClient, 1);
            Require(previewRecord is { SourceState: "Partial", TranslationIsPreview: true, TranslationState: "Done", StableSource: "We should take the left" }, "typed stable preview");
            Require(previewRecord.TranslationSource == "We should take the left" && previewRecord.TranslationPrefix == "We should take the left", "typed preview text unit");
            await previewClient.SendAsync("mock_segment", new { source = "We should take the left path after sunset.", kind = "final" });
            var replaced = await Terminal(previewClient, 1);
            Require(replaced is { SourceState: "Final", TranslationIsPreview: false, SourceRevision: 3, TranslationState: "Done" }, "final replaces preview with current revision");
        }
        finally
        {
            await CaptionDiagnostics.DrainAsync();
            if (File.Exists(timingPath)) File.Delete(timingPath);
            stop.Cancel(); listener.Stop(); await server;
        }
    }
    private static async Task<HistoryRecord> Terminal(WorkerClient client, ulong segment)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 4)
        {
            var record = (await client.ReadHistoryAsync()).Records.Single(r => r.SegmentId == segment);
            if (record.TranslationState != "Pending") return record;
            await Task.Delay(10);
        }
        throw new Exception("Translation did not terminalize");
    }
    private static void Require(bool value, string label) { if (!value) throw new Exception(label); }
}
