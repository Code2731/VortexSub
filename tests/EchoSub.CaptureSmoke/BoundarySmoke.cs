using System.Diagnostics;
using System.Text.Json;
using EchoSub.Desktop;

internal static class BoundarySmoke
{
    public static async Task Run(string[] args)
    {
        if (args.Length != 6 || !OperatingSystem.IsWindows()) throw new ArgumentException("Windows boundary assets required");
        using var assets = JsonDocument.Parse(File.ReadAllText(args[5]));
        var entries = assets.RootElement.GetProperty("assets").EnumerateArray().ToArray();
        var vad = entries.Single(x => x.GetProperty("id").GetString() == "silero-v6");
        var ort = entries.Single(x => x.GetProperty("id").GetString() == "ort-win-x64");
        string Asset(JsonElement e) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(args[5])!, e.GetProperty("path").GetString()!));
        var options = new[] { "--session-control", "--diagnostic-capture", "--live-asr", "--diagnostic-asr", "--asr-model", args[1], "--asr-sha256", args[2],
            "--diagnostic-vad", "--vad-model", Asset(vad), "--vad-sha256", vad.GetProperty("sha256").GetString()!,
            "--vad-runtime", Asset(ort), "--vad-runtime-sha256", ort.GetProperty("sha256").GetString()! };
        var directory = Path.GetDirectoryName(args[4])!;
        var longWave = Path.Combine(directory, "repeated-speech.wav");
        LiveAsrSmoke.WriteLoop(args[3], longWave, repetitions: 4);
        var silence = Path.Combine(directory, "digital-silence.wav");
        using (var w = new BinaryWriter(File.Create(silence)))
        {
            w.Write(0x46464952u); w.Write(36 + 96000); w.Write(0x45564157u); w.Write(0x20746d66u); w.Write(16);
            w.Write((ushort)1); w.Write((ushort)1); w.Write(16000); w.Write(32000); w.Write((ushort)2); w.Write((ushort)16);
            w.Write(0x61746164u); w.Write(96000); w.Write(new byte[96000]);
        }
        var events = new List<WorkerEvent>();
        HistorySnapshot? history = null;
        JsonElement last = default;
        string phase = "model_preparation";
        double silentAudio = 0;
        bool silenceVerified = false;
        bool boundaryVerified = false;
        int silenceRecords = 0;
        JsonElement? silentState = null;
        void Save(bool passed) => File.WriteAllText(args[4], JsonSerializer.Serialize(new {
            passed, phase, input = "real_WASAPI_loopback_en10_TTS_four_repetitions_trimmed_edges",
            fixture_edge_threshold_pcm16 = 128, silent_audio_s = silentAudio,
            silence_verified = silenceVerified, silence_records = silenceRecords, silent_state = silentState,
            boundary_verified = boundaryVerified,
            events, history, last_state = last, ui_verified = false, quality_gate_passed = false,
            other_system_audio_isolated = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
        await using var client = WorkerClient.Start(args[0], arguments: options);
        async Task State()
        {
            last = await client.SendAsync("get_state");
            while (client.Events.TryRead(out var e)) if (e is not null) events.Add(e);
            Save(false);
            Require(last.GetProperty("session").GetProperty("state").GetString() != "Error", "Session failed: " + last.GetRawText());
        }
        async Task Wait(Func<bool> predicate, double seconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                await State(); if (predicate()) return; await Task.Delay(20);
            }
            throw new TimeoutException(phase);
        }
        async Task<HistorySnapshot> ObserveHistory()
        {
            // ReadHistoryAsync acknowledges snapshot seq and may discard events in flight.
            // This bounded single-page probe must observe segmentation diagnostics as well.
            var page = await client.SendAsync("get_history", new { limit = 4 });
            Require(page.GetProperty("next_offset").ValueKind == JsonValueKind.Null, "Bounded probe history page");
            var rows = JsonSerializer.Deserialize<HistoryRecord[]>(page.GetProperty("records"))!;
            while (client.Events.TryRead(out var e)) if (e is not null) events.Add(e);
            return new HistorySnapshot(page.GetProperty("history_version").GetUInt64(), page.GetProperty("last_seq").GetUInt64(), rows);
        }
        void Play(string path) => Require(Native.PlaySound(path, IntPtr.Zero, 0x20000 | 1 | 2 | 8), "Owned WAV playback");
        try
        {
            await client.SendAsync("hello", new { client = "BoundarySmoke", protocol_major = 1 });
            await Wait(() => last.GetProperty("model").GetProperty("state").GetString() == "Ready", 30);
            phase = "capture_start";
            var start = await client.SendAsync("start_session", new { history_policy = "retain", config = new { source_language = "en", partial_enabled = false } });
            var uuid = start.GetProperty("session_id").GetString()!;
            await Wait(() => last.GetProperty("session").GetProperty("state").GetString() == "Running", 12);
            phase = "digital_silence";
            var initialAudio = last.GetProperty("diagnostic_capture").GetProperty("accepted_audio_s").GetDouble();
            Play(silence);
            await Wait(() => last.GetProperty("diagnostic_capture").GetProperty("accepted_audio_s").GetDouble() - initialAudio >= 2.5, 8);
            silentAudio = last.GetProperty("diagnostic_capture").GetProperty("accepted_audio_s").GetDouble() - initialAudio;
            silentState = last.Clone();
            var initialHistory = await client.ReadHistoryAsync();
            silenceRecords = initialHistory.Records.Count;
            silenceVerified = silenceRecords == 0 && !last.GetProperty("diagnostic_asr").GetProperty("decoding").GetBoolean()
                && last.GetProperty("diagnostic_asr").GetProperty("completed_jobs").GetInt32() == 0
                && last.GetProperty("diagnostic_live_vad").GetProperty("model_calls").GetInt32() == 0;
            ulong firstSpeechId = initialHistory.Records.Select(r => r.SegmentId).DefaultIfEmpty(0UL).Max() + 1;
            phase = "long_utterance";
            events.Clear(); Play(longWave);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 35)
            {
                await State(); history = await ObserveHistory();
                Require(last.GetProperty("diagnostic_asr").GetProperty("pending_language_count").GetInt32() <= 3, "Bounded contexts");
                if (history.Records.Count(r => r.SegmentId >= firstSpeechId && r.SourceState == "Final") >= 2
                    && events.Any(e => e.Name == "capture.segmented" && e.Payload.GetProperty("continued_from").ValueKind == JsonValueKind.Number)) break;
                await Task.Delay(20);
            }
            Require(history is not null && history.Records.Count(r => r.SegmentId >= firstSpeechId && r.SourceState == "Final") >= 2, "Two real long-utterance finals");
            Require(events.Any(e => e.Name == "capture.segmented" && e.Payload.GetProperty("reason").GetString() == "ChunkLimit"), "Actual eight-second chunk boundary");
            Require(events.Any(e => e.Name == "capture.segmented" && e.Payload.GetProperty("continued_from").ValueKind == JsonValueKind.Number), "Actual continuation metadata");
            Require(events.Where(e => e.Name == "asr.completed").All(e => e.Payload.TryGetProperty("overlap_segments_removed", out _)), "Native reconciliation diagnostics");
            boundaryVerified = true;
            phase = "stop"; await client.SendAsync("stop_session", new { session_id = uuid }); Native.PlaySound(null, IntPtr.Zero, 0);
            await Wait(() => last.GetProperty("session").GetProperty("state").GetString() == "Idle", 10);
            phase = "complete"; Save(silenceVerified);
            Console.WriteLine($"Long-utterance boundary PASS; digital silence verified={silenceVerified}; audio={silentAudio:F3}s; quality unverified");
            Require(silenceVerified, "Boundary passed; digital silence was not isolated/verified (see checkpoint)");
        }
        finally { Native.PlaySound(null, IntPtr.Zero, 0); }
    }
}
