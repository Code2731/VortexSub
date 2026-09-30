using System.Diagnostics;
using System.Text.Json;
using EchoSub.Desktop;

internal static class SessionSmoke
{
    public static async Task Run(string[] args)
    {
        if (args.Length != 6 || !OperatingSystem.IsWindows()) throw new ArgumentException("Windows live session assets required");
        using var assets = JsonDocument.Parse(File.ReadAllText(args[5]));
        var entries = assets.RootElement.GetProperty("assets").EnumerateArray().ToArray();
        var vad = entries.Single(x => x.GetProperty("id").GetString() == "silero-v6");
        var runtime = entries.Single(x => x.GetProperty("id").GetString() == "ort-win-x64");
        string Asset(JsonElement e) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(args[5])!, e.GetProperty("path").GetString()!));
        var options = new[] { "--session-control", "--diagnostic-capture", "--live-asr", "--diagnostic-asr", "--asr-model", args[1], "--asr-sha256", args[2],
            "--diagnostic-vad", "--vad-model", Asset(vad), "--vad-sha256", vad.GetProperty("sha256").GetString()!,
            "--vad-runtime", Asset(runtime), "--vad-runtime-sha256", runtime.GetProperty("sha256").GetString()! };
        var wave = Path.Combine(Path.GetDirectoryName(args[4])!, "speech-with-silence.wav");
        LiveAsrSmoke.WriteLoop(args[3], wave);
        double pauseResponse = 0, stopResponse = 0, idleAfterStop = 0;
        var finals = new List<HistoryRecord>();
        string phase = "model_preparation";
        bool pauseNativeObserved = false, resumeBeforeNativeReturn = false;
        HistoryRecord? interrupted = null;
        JsonElement last = default;
        void Save(bool passed) => File.WriteAllText(args[4], JsonSerializer.Serialize(new
        {
            passed, phase, input = "real_WASAPI_loopback_synthetic_TTS", ui_verified = false,
            other_system_audio_isolated = false, quality_gate_passed = false,
            pause_response_s = pauseResponse, stop_response_s = stopResponse,
            pause_native_observed = pauseNativeObserved, resume_before_native_return = resumeBeforeNativeReturn,
            idle_after_stop_s = idleAfterStop, finals, last_state = last
        }, new JsonSerializerOptions { WriteIndented = true }));
        await using var client = WorkerClient.Start(args[0], arguments: options);
        async Task<JsonElement> State() { last = await client.SendAsync("get_state"); return last; }
        async Task Wait(Func<JsonElement, bool> predicate, double seconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                var state = await State();
                Save(false);
                if (state.GetProperty("session").GetProperty("state").GetString() == "Error")
                    throw new Exception("Session capture failure: " + state.GetProperty("diagnostic_capture").GetRawText());
                if (predicate(state)) return;
                await Task.Delay(20);
            }
            throw new TimeoutException(phase);
        }
        static bool Is(JsonElement s, string name) => s.GetProperty("session").GetProperty("state").GetString() == name;
        async Task<HistoryRecord> Final(ulong session, ulong epoch)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 30)
            {
                await State(); Save(false);
                if (Is(last, "Error")) throw new Exception("Session failed: " + last.GetRawText());
                var history = await client.ReadHistoryAsync();
                var record = history.Records.LastOrDefault(r => r.SessionId == session && r.Epoch == epoch && r.SourceState == "Final");
                if (record is not null) return record;
                await Task.Delay(20);
            }
            throw new TimeoutException(phase + " final");
        }
        void Play() { if (!Native.PlaySound(wave, IntPtr.Zero, 0x20000 | 1 | 2 | 8)) throw new Exception("Fixture playback failed"); }
        static void Require(bool valid, string message) { if (!valid) throw new Exception(message); }
        try
        {
            var hello = await client.SendAsync("hello", new { client = "SessionSmoke", protocol_major = 1 });
            Require(hello.GetProperty("capabilities").GetProperty("session_control").GetBoolean(), "Session capability");
            await Wait(s => s.GetProperty("model").GetProperty("state").GetString() == "Ready", 30);
            var config = new { history_policy = "retain", config = new { source_language = "en" } };
            phase = "first_session";
            var start = await client.SendAsync("start_session", config);
            var id = start.GetProperty("session_id").GetString()!;
            Require(Guid.TryParseExact(id, "D", out _), "UUID");
            await Wait(s => Is(s, "Running"), 12);
            var internalId = last.GetProperty("session").GetProperty("internal_session_id").GetUInt64();
            Play();
            finals.Add(await Final(internalId, start.GetProperty("epoch").GetUInt64()));
            Require(finals[0].ProductSessionId == id && finals[0].SessionAudioStartSeconds >= 0, "UUID/session audio metadata");
            phase = "pause_during_native";
            await Wait(s => s.GetProperty("diagnostic_asr").GetProperty("native_running").GetBoolean(), 30);
            interrupted = (await client.ReadHistoryAsync()).Records.LastOrDefault(r => r.SessionId == internalId && r.SourceState == "FinalPending");
            Require(interrupted is not null, "Native source identified before Pause");
            pauseNativeObserved = true;
            var clock = Stopwatch.StartNew();
            await client.SendAsync("pause_session", new { session_id = id });
            pauseResponse = clock.Elapsed.TotalSeconds;
            Native.PlaySound(null, IntPtr.Zero, 0);
            await Wait(s => Is(s, "Paused") && !s.GetProperty("diagnostic_capture").GetProperty("awaiting_capture_join").GetBoolean()
                && !s.GetProperty("diagnostic_live_vad").GetProperty("awaiting_join").GetBoolean(), 10);
            resumeBeforeNativeReturn = last.GetProperty("diagnostic_asr").GetProperty("decoding").GetBoolean();
            var resumed = await client.SendAsync("resume_session", new { session_id = id });
            await Wait(s => Is(s, "Running"), 12);
            Play();
            finals.Add(await Final(internalId, resumed.GetProperty("epoch").GetUInt64()));
            Require(finals[1].SegmentId > finals[0].SegmentId && finals[1].Epoch > finals[0].Epoch, "Resume identity monotonic");
            var afterResume = await client.ReadHistoryAsync();
            Require(afterResume.Records.Any(r => r.SessionId == interrupted!.SessionId && r.Epoch == interrupted.Epoch && r.SegmentId == interrupted.SegmentId && r.SourceState == "Discarded"), "Paused native source discarded after Resume");
            Require(finals[1].ProductSessionId == id && finals[1].SessionAudioStartSeconds > finals[0].SessionAudioStartSeconds, "Same UUID/session timeline after Resume");
            phase = "stop_during_native";
            await Wait(s => s.GetProperty("diagnostic_asr").GetProperty("native_running").GetBoolean(), 30);
            clock.Restart();
            await client.SendAsync("stop_session", new { session_id = id });
            stopResponse = clock.Elapsed.TotalSeconds;
            Native.PlaySound(null, IntPtr.Zero, 0);
            await Wait(s => Is(s, "Idle"), 10);
            idleAfterStop = clock.Elapsed.TotalSeconds;
            Require(!last.GetProperty("diagnostic_asr").GetProperty("decoding").GetBoolean(), "Idle waits for native return");
            var retained = await client.ReadHistoryAsync();
            Require(finals.All(f => retained.Records.Any(r => r.SessionId == f.SessionId && r.SegmentId == f.SegmentId && r.Source == f.Source)), "Final history retained");
            phase = "new_session";
            var next = await client.SendAsync("start_session", config);
            Require(next.GetProperty("session_id").GetString() != id, "Fresh UUID");
            try { await client.SendAsync("pause_session", new { session_id = id }); throw new Exception("Old UUID accepted"); }
            catch (WorkerException e) when (e.Code == "STALE_SESSION") { }
            await Wait(s => Is(s, "Running"), 12);
            Play();
            var nextId = last.GetProperty("session").GetProperty("internal_session_id").GetUInt64();
            finals.Add(await Final(nextId, next.GetProperty("epoch").GetUInt64()));
            Require(finals[2].ProductSessionId == next.GetProperty("session_id").GetString(), "New final has new UUID");
            Require((await client.ReadHistoryAsync()).Records.Any(r => r.ProductSessionId == id && r.Source == finals[0].Source), "Old UUID retained after new session");
            Require(nextId > internalId, "Fresh internal identity");
            await client.SendAsync("stop_session", new { session_id = next.GetProperty("session_id").GetString() });
            Native.PlaySound(null, IntPtr.Zero, 0);
            await Wait(s => Is(s, "Idle"), 10);
            phase = "export_real_history";
            var srtPath = Path.Combine(Path.GetDirectoryName(args[4])!, "current-session.srt");
            var txtPath = Path.Combine(Path.GetDirectoryName(args[4])!, "first-session.txt");
            var srt = await client.SendAsync("export_history", new { session_id = next.GetProperty("session_id").GetString(), format = "srt", path = srtPath, overwrite = false });
            var txt = await client.SendAsync("export_history", new { session_id = id, format = "txt", path = txtPath, overwrite = false });
            Require(srt.GetProperty("cue_count").GetInt32() >= 1 && File.ReadAllText(srtPath).Contains(" --> "), "Real source SRT export");
            Require(txt.GetProperty("record_count").GetInt32() >= 2 && File.ReadAllText(txtPath).Contains("Started UTC:"), "Retained real history/UTC TXT export");
            phase = "clear_real_history";
            var beforeClear = await client.ReadHistoryAsync();
            var savedSrt = File.ReadAllText(srtPath);
            var savedTxt = File.ReadAllText(txtPath);
            var cleared = await client.SendAsync("clear_history", new { session_id = id });
            Require(cleared.GetProperty("removed_count").GetInt32() == beforeClear.Records.Count(r => r.ProductSessionId == id), "Clear retained real session count");
            var afterClear = await client.ReadHistoryAsync();
            Require(afterClear.Records.Count > 0 && afterClear.Records.All(r => r.ProductSessionId != id), "Other session remains after clear");
            Require(File.ReadAllText(srtPath) == savedSrt && File.ReadAllText(txtPath) == savedTxt, "Exported files survive clear");
            phase = "complete";
            Save(true);
            Console.WriteLine($"PASS: UUID live sessions; finals={finals.Count}; pause={pauseResponse:F6}s; stop={stopResponse:F6}s; idle={idleAfterStop:F6}s");
        }
        finally { Native.PlaySound(null, IntPtr.Zero, 0); }
    }
}
