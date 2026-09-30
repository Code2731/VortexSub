using System.Diagnostics;
using System.Text.Json;
using EchoSub.Desktop;

internal static class LiveAsrSmoke
{
    public static async Task Run(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new Exception("Windows required");
        using var assets = JsonDocument.Parse(File.ReadAllText(args[5]));
        var entries = assets.RootElement.GetProperty("assets").EnumerateArray().ToArray();
        var model = entries.Single(x => x.GetProperty("id").GetString() == "silero-v6");
        var runtime = entries.Single(x => x.GetProperty("id").GetString() == "ort-win-x64");
        var root = Path.GetDirectoryName(args[5])!;
        string Asset(JsonElement e) => Path.GetFullPath(Path.Combine(root, e.GetProperty("path").GetString()!));
        var options = new[] { "--diagnostic-capture", "--live-asr", "--diagnostic-asr", "--asr-model", args[1], "--asr-sha256", args[2],
            "--diagnostic-vad", "--vad-model", Asset(model), "--vad-sha256", model.GetProperty("sha256").GetString()!,
            "--vad-runtime", Asset(runtime), "--vad-runtime-sha256", runtime.GetProperty("sha256").GetString()! };
        // Actual 0.5 s lead-in and 1 s tail silence avoid starting midway through speech.
        var loopPath = Path.Combine(Path.GetDirectoryName(args[4])!, "speech-with-silence.wav");
        WriteLoop(args[3], loopPath);
        double pingMax = 0, stopResponseMax = 0, stopJoinMax = 0, nativeReturnMax = 0;
        int finals = 0, resets = 0;
        double previousEnd = 0, gapMinimum = double.MaxValue;
        var ranges = new List<object>();
        JsonElement finalState = default;
        try
        {
            await using var client = WorkerClient.Start(args[0], arguments: options);
            var hello = await client.SendAsync("hello", new { client = "LiveAsrSmoke", protocol_major = 1 });
            Require(hello.GetProperty("capabilities").GetProperty("live_asr").GetBoolean(), "live capability");
            Require(!hello.GetProperty("capabilities").GetProperty("fixture_asr").GetBoolean(), "file input excluded");
            await Wait(async () => (await State()).GetProperty("model").GetProperty("state").GetString() == "Ready", 30, "Whisper ready");
            try { await client.SendAsync("start_capture"); throw new Exception("missing language accepted"); }
            catch (WorkerException e) when (e.Code == "INVALID_REQUEST") { }
            try { await client.SendAsync("transcribe_fixture"); throw new Exception("file input accepted"); }
            catch (WorkerException e) when (e.Code == "UNSUPPORTED_CAPABILITY") { }
            // Two complete rounds establish actual final history and forward gaps.
            for (int round = 0; round < 2; round++)
            {
                Native.PlaySound(null, IntPtr.Zero, 0);
                var accepted = await client.SendAsync("start_capture", new { language = "en" });
                var epoch = accepted.GetProperty("epoch").GetUInt64();
                await WaitCaptureRunning(State);
                Require(Native.PlaySound(loopPath, IntPtr.Zero, 0x20000 | 1 | 2 | 8), "play whole fixture after capture ready");
                await Wait(async () =>
                {
                    var s = await State(); Healthy(s);
                    var h = await client.ReadHistoryAsync();
                    return h.Records.Any(r => r.Epoch == epoch && r.SourceState == "Final");
                }, 30, "real source final");
                var history = await client.ReadHistoryAsync();
                var record = history.Records.First(r => r.Epoch == epoch && r.SourceState == "Final");
                Require(!string.IsNullOrWhiteSpace(record.Source) && record.TranslationState == "None", "real untransformed source");
                Require(record.AudioStartSeconds >= previousEnd && record.AudioEndSample - record.AudioStartSample <= 128000, $"session clock and PCM bounds (epoch={epoch}, start={record.AudioStartSeconds}, end={record.AudioEndSeconds}, previous={previousEnd})");
                ranges.Add(new { epoch, audio_start_sample = record.AudioStartSample, audio_end_sample = record.AudioEndSample, audio_start_s = record.AudioStartSeconds, audio_end_s = record.AudioEndSeconds, duration_s = (record.AudioEndSample - record.AudioStartSample) / 16000.0, reached_chunk_cap = record.AudioEndSample - record.AudioStartSample == 128000 });
                finals++;
                var state = Capture(await State());
                if (round > 0) { var gap = state.GetProperty("gap_before_s").GetDouble(); Require(gap >= 0.2, "restart gap retained"); gapMinimum = Math.Min(gapMinimum, gap); }
                previousEnd = state.GetProperty("audio_end_s").GetDouble();
                await Stop();
                await Task.Delay(250);
            }
            // Stop while native full owns its PCM; old completion must never apply.
            for (int round = 0; round < 3; round++)
            {
                var accepted = await client.SendAsync("start_capture", new { language = "en" });
                var epoch = accepted.GetProperty("epoch").GetUInt64();
                await Wait(async () => { var s = await State(); Healthy(s); return s.GetProperty("diagnostic_asr").GetProperty("native_running").GetBoolean(); }, 30, "native running");
                await Stop();
                // Restart input while the old full may still hold its reservation.
                if (round == 0)
                {
                    Require((await State()).GetProperty("diagnostic_asr").GetProperty("decoding").GetBoolean(), "old full reservation retained during restart");
                    await client.SendAsync("start_capture", new { language = "en" });
                }
                var returnClock = Stopwatch.StartNew();
                await Wait(async () => !(await State()).GetProperty("diagnostic_asr").GetProperty("decoding").GetBoolean(), 10, "cancelled full returned");
                nativeReturnMax = Math.Max(nativeReturnMax, returnClock.Elapsed.TotalSeconds);
                var history = await client.ReadHistoryAsync();
                Require(history.Records.Any(r => r.Epoch == epoch && r.SourceState == "Discarded"), "inflight source discarded");
                Require(!history.Records.Any(r => r.Epoch == epoch && r.SourceState == "Final"), "old native completion never applied");
                if (round == 0) await Stop();
                resets++;
            }
            // Observe after stopping owned playback; other system audio is not isolated.
            Native.PlaySound(null, IntPtr.Zero, 0);
            await client.SendAsync("start_capture", new { language = "en" });
            await WaitCaptureRunning(State);
            var before = (await State()).GetProperty("diagnostic_asr").GetProperty("completed_jobs").GetUInt64();
            await Task.Delay(600);
            Require((await State()).GetProperty("diagnostic_asr").GetProperty("completed_jobs").GetUInt64() == before, "no additional ASR after owned playback");
            finalState = await State();
            using var process = Process.GetProcessById(client.ProcessId);
            _ = process.Handle;
            await client.SendAsync("shutdown");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(process.ExitCode == 0, "active live shutdown joined");

            async Task<JsonElement> State()
            {
                var ping = Stopwatch.StartNew(); await client.SendAsync("ping", new { nonce = "live" }); pingMax = Math.Max(pingMax, ping.Elapsed.TotalSeconds);
                return await client.SendAsync("get_state");
            }
            async Task Stop()
            {
                var clock = Stopwatch.StartNew(); await client.SendAsync("stop_capture"); stopResponseMax = Math.Max(stopResponseMax, clock.Elapsed.TotalSeconds);
                await Wait(async () => { var s = await State(); return Capture(s).GetProperty("state").GetString() == "Stopped" && !s.GetProperty("diagnostic_live_vad").GetProperty("awaiting_join").GetBoolean(); }, 5, "capture/VAD joined");
                stopJoinMax = Math.Max(stopJoinMax, clock.Elapsed.TotalSeconds);
                var audio = Capture(await State()).GetProperty("accepted_audio_s").GetDouble(); await Task.Delay(50);
                Require(Capture(await State()).GetProperty("accepted_audio_s").GetDouble() == audio, "no PCM after Stop");
            }
        }
        finally { Native.PlaySound(null, IntPtr.Zero, 0); }
        // A bad VAD hash fails the capture and keeps control available.
        var bad = options.ToArray(); bad[Array.IndexOf(bad, "--vad-sha256") + 1] = new string('0', 64);
        await using (var client = WorkerClient.Start(args[0], arguments: bad))
        {
            await client.SendAsync("hello", new { client = "LiveVadHashSmoke", protocol_major = 1 });
            await Wait(async () => (await client.SendAsync("get_state")).GetProperty("model").GetProperty("state").GetString() == "Ready", 30, "bad VAD Whisper ready");
            await client.SendAsync("start_capture", new { language = "en" });
            await Wait(async () => Capture(await client.SendAsync("get_state")).GetProperty("state").GetString() == "Failed", 5, "bad VAD hash fails stream");
            Require(Capture(await client.SendAsync("get_state")).GetProperty("error").GetString() == "LIVE_VAD_MODEL_FAILED", "VAD load fault preserved");
            Require((await client.ReadHistoryAsync()).Records.Count == 0, "invalid VAD produces no captions");
        }
        // Parent stdin EOF owns and joins the same live resources as shutdown.
        File.WriteAllText(args[4], JsonSerializer.Serialize(new
        {
            passed = false, stage = "before_parent_eof", finals, cancel_restart_rounds = resets,
            ranges, stop_response_max_s = stopResponseMax, stop_join_max_s = stopJoinMax,
            native_return_after_join_max_s = nativeReturnMax, vad_hash_rejected = true,
            active_shutdown = true, quality_gate_passed = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        using (var process = new Process { StartInfo = new ProcessStartInfo(args[0]) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } })
        {
            foreach (var option in options) process.StartInfo.ArgumentList.Add(option);
            process.Start(); var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await Raw("hello", new { client = "LiveEofSmoke", protocol_major = 1 });
                await Wait(async () => (await Raw("get_state", new { })).GetProperty("model").GetProperty("state").GetString() == "Ready", 30, "EOF model ready");
                await Raw("start_capture", new { language = "en" });
                await WaitCaptureRunning(() => Raw("get_state", new { }));
                process.StandardInput.Close();
                while (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) is not null) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Require(process.ExitCode == 0, "parent EOF joined live owners");
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } await errors; }
            async Task<JsonElement> Raw(string method, object parameters)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { v = 1, kind = "command", request_id = method, method, @params = parameters })); await process.StandardInput.FlushAsync();
                while (true) { var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)); Require(line is not null, "EOF raw response"); using var d = JsonDocument.Parse(line!); if (d.RootElement.GetProperty("kind").GetString() != "response") continue; Require(d.RootElement.GetProperty("ok").GetBoolean(), "EOF raw command"); return d.RootElement.GetProperty("result").Clone(); }
            }
        }
        var report = new
        {
            passed = true,
            input = "real_WASAPI_loopback_synthetic_TTS",
            live_asr = true,
            partial = false,
            translation = false,
            finals,
            cancel_restart_rounds = resets,
            restart_gap_min_s = gapMinimum,
            ping_max_s = pingMax,
            stop_response_max_s = stopResponseMax,
            stop_join_max_s = stopJoinMax,
            native_return_after_join_max_s = nativeReturnMax,
            ranges,
            whole_fixture_started_after_capture_ready = true,
            fixture_leading_silence_s = 0.5,
            fixture_trailing_silence_s = 1.0,
            other_system_audio_isolated = false,
            vad_hash_rejected = true,
            active_shutdown = true,
            active_parent_eof = true,
            restart_before_native_return = true,
            quality_gate_passed = false,
            final_state = finalState
        };
        File.WriteAllText(args[4], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
    }
    static JsonElement Capture(JsonElement state) => state.GetProperty("diagnostic_capture");
    static void Healthy(JsonElement s) { if (Capture(s).GetProperty("state").GetString() == "Failed") throw new Exception("Live capture fault: " + Capture(s).GetRawText()); }
    static async Task WaitCaptureRunning(Func<Task<JsonElement>> state)
    {
        var clock = Stopwatch.StartNew();
        JsonElement last = default;
        while (clock.Elapsed.TotalSeconds < 12)
        {
            var current = await state();
            Healthy(current);
            last = Capture(current).Clone();
            if (last.GetProperty("state").GetString() == "Running") return;
            await Task.Delay(10);
        }
        throw new TimeoutException("live capture Running: " + last.GetRawText());
    }
    static async Task Wait(Func<Task<bool>> predicate, double seconds, string label)
    {
        var c = Stopwatch.StartNew(); while (c.Elapsed.TotalSeconds < seconds) { if (await predicate()) return; await Task.Delay(10); }
        throw new TimeoutException(label);
    }
    static void Require(bool valid, string label) { if (!valid) throw new Exception("Live smoke failed: " + label); }
    internal static void WriteLoop(string source, string target)
    {
        using var reader = new BinaryReader(File.OpenRead(source));
        Require(reader.ReadUInt32() == 0x46464952, "RIFF"); reader.ReadUInt32(); Require(reader.ReadUInt32() == 0x45564157, "WAVE");
        byte[]? format = null, pcm = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = reader.ReadUInt32(); var n = reader.ReadInt32(); Require(n >= 0 && n <= 1024 * 1024, "fixture chunk bound"); var b = reader.ReadBytes(n);
            if (id == 0x20746d66) format = b; if (id == 0x61746164) pcm = b; if ((n & 1) != 0) reader.ReadByte();
        }
        Require(format is not null && pcm is not null, "fixture format/data");
        Require(BitConverter.ToUInt16(format!, 0) == 1 && BitConverter.ToUInt16(format!, 2) == 1 && BitConverter.ToUInt32(format!, 4) == 16000 && BitConverter.ToUInt16(format!, 14) == 16, "PCM16 mono16k fixture");
        using var w = new BinaryWriter(File.Create(target)); var length = pcm!.Length + 48000;
        w.Write(0x46464952u); w.Write(36 + length); w.Write(0x45564157u); w.Write(0x20746d66u); w.Write(16); w.Write(format![..16]); w.Write(0x61746164u); w.Write(length); w.Write(new byte[16000]); w.Write(pcm); w.Write(new byte[32000]);
    }
}
