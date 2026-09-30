using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EchoSub.Desktop;

internal static class WorkerTranslationSmoke
{
    public static async Task Run(string[] args)
    {
        var options = new[] { "--diagnostic-asr", "--asr-model", args[2], "--asr-sha256", args[3], "--asr-backend", "cpu", "--diagnostic-translation" };
        using var manifest = JsonDocument.Parse(File.ReadAllText(args[4]));
        var fixtures = manifest.RootElement.GetProperty("fixtures").EnumerateArray().Where(f => f.GetProperty("kind").GetString() != "silence").ToArray();
        var selected = fixtures.Where(f => f.GetProperty("language").GetString() == "en").Take(3)
            .Concat(fixtures.Where(f => f.GetProperty("language").GetString() == "ko").Take(1)).ToArray();
        Require(selected.Length == 4, "Expected three English and one Korean speech fixtures");
        await using var client = WorkerClient.Start(args[1], arguments: options);
        var hello = await client.SendAsync("hello", new { protocol_major = 1, client = "WorkerTranslationSmoke" });
        Require(hello.GetProperty("capabilities").GetProperty("translation").GetBoolean(), "HTTP capability");
        await client.SendAsync("configure_translation", new { endpoint = args[5], model_id = args[6] });
        await WaitState(client, state => state.GetProperty("model").GetProperty("state").GetString() == "Ready"
            && state.GetProperty("translator").GetProperty("state").GetString() == "Ready");
        var runs = new List<object>();
        double pingMax = 0;
        foreach (var fixture in selected)
        {
            var clock = Stopwatch.StartNew();
            var accepted = await client.SendAsync("transcribe_fixture", new { path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(args[4])!, fixture.GetProperty("path").GetString()!)),
                sha256 = fixture.GetProperty("sha256").GetString(), language = fixture.GetProperty("language").GetString() });
            var segment = accepted.GetProperty("segment_id").GetUInt64();
            HistoryRecord? record = null;
            while (clock.Elapsed.TotalSeconds < 15)
            {
                var ping = Stopwatch.StartNew(); await client.SendAsync("ping", new { nonce = "translation" }); pingMax = Math.Max(pingMax, ping.Elapsed.TotalSeconds);
                record = (await client.ReadHistoryAsync()).Records.SingleOrDefault(r => r.SegmentId == segment);
                if (record is { SourceState: "Final", TranslationState: "Done" or "Bypassed" }) break;
                if (record?.TranslationState is "Failed" or "Skipped") throw new Exception("Unexpected translation terminal failure");
                await Task.Delay(10);
            }
            Require(record is { SourceState: "Final", TranslationState: "Done" or "Bypassed" }, "Native source and translation terminal");
            var bypass = fixture.GetProperty("language").GetString() == "ko";
            Require(record!.Source.Length > 0 && record.AppliedSourceRevision == record.SourceRevision, "Native final identity retained");
            Require(bypass ? record.TranslationState == "Bypassed" && record.TranslationRequestId is null
                : record.Translation.Length > 0 && record.TranslationState == "Done" && record.TranslationRequestId is > 0, "Language-specific HTTP/bypass");
            runs.Add(new { id = fixture.GetProperty("id").GetString(), file_to_terminal_s = clock.Elapsed.TotalSeconds, record });
            Save(false);
        }
        var state = await client.SendAsync("get_state");
        Require(state.GetProperty("translator").GetProperty("completed_jobs").GetInt32() == 3, "Korean bypass makes no translation job");
        await client.SendAsync("disable_translation");
        Require((await client.SendAsync("get_state")).GetProperty("translator").GetProperty("state").GetString() == "Unavailable", "Disable when quiescent");
        Save(true);
        void Save(bool passed) => File.WriteAllText(args[7], JsonSerializer.Serialize(new { task = "T03-01c", passed, quality_gate_passed = false,
            capture = false, worker_integrated = true, ui_integrated = false, asr_backend = "cpu", translation_model = args[6], asr_sha256 = args[3],
            fixture_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[4]))).ToLowerInvariant(), finals = runs.Count,
            http_completed = Math.Min(runs.Count, 3), bypassed = Math.Max(0, runs.Count - 3), ping_max_s = pingMax, runs }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static async Task WaitState(WorkerClient client, Func<JsonElement, bool> predicate)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 15)
        {
            var state = await client.SendAsync("get_state");
            if (predicate(state)) return;
            if (state.GetProperty("model").GetProperty("state").GetString() == "Failed" || state.GetProperty("translator").GetProperty("state").GetString() == "Failed")
                throw new Exception("Native model or translator preparation failed");
            await Task.Delay(10);
        }
        throw new Exception("Preparation did not complete");
    }
    private static void Require(bool condition, string label) { if (!condition) throw new Exception(label); }
}
