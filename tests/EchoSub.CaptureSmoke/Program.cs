using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using EchoSub.Desktop;

try { await Run(args); return 0; }
catch (Exception error)
{
    var reportPath = args.FirstOrDefault() == "--dump-worker" ? null : args.Length == 7 && args[0] is "--sessions" or "--partials" or "--boundaries" ? args[5] : args.Length == 6 ? args[4] : args.Length is 3 or 4 ? args[2] : null;
    if (reportPath is not null)
    {
        JsonElement? checkpoint = File.Exists(reportPath)
            ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(reportPath)) : null;
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = false, error = error.Message, checkpoint }, new JsonSerializerOptions { WriteIndented = true }));
    }
    Console.Error.WriteLine(error);
    return 1;
}

static async Task Run(string[] args)
{
    if (args.FirstOrDefault() == "--dump-worker") { CaptureDump.Write(args); return; }
    if (args.FirstOrDefault() == "--startup") { await CaptureStartupSmoke.Run(args); return; }
    if (args.FirstOrDefault() == "--sessions") { await SessionSmoke.Run(args[1..]); return; }
    if (args.FirstOrDefault() == "--partials") { await SessionSmoke.Run(args[1..], partialEnabled: true); return; }
    if (args.FirstOrDefault() == "--boundaries") { await BoundarySmoke.Run(args[1..]); return; }
    if (args.Length == 6) { await LiveAsrSmoke.Run(args); return; }
    if (args.Length is not (3 or 4) || !OperatingSystem.IsWindows()) throw new ArgumentException("worker WAV report [rounds] required on Windows");
    var rounds = args.Length == 4 ? int.Parse(args[3]) : 3;
    Require(rounds is >= 1 and <= 100, "rounds must be 1..100");
    var opening = new List<double>();
    var startupPhases = new List<JsonElement>();
    var stopResponse = new List<double>();
    var stopJoin = new List<double>();
    double pingMax = 0;
    JsonElement endpoint = default;
    ulong packets = 0;
    double acceptedAudio = 0;
    bool playing = false;
    try
    {
        playing = Native.PlaySound(args[1], IntPtr.Zero, 0x20000 | 1 | 2 | 8);
        Require(playing, "fixture playback started");
        await using (var client = WorkerClient.Start(args[0], arguments: ["--diagnostic-capture"]))
        {
            var hello = await client.SendAsync("hello", new { client = "CaptureSmoke", protocol_major = 1 });
            Require(hello.GetProperty("capabilities").GetProperty("system_audio").GetBoolean(), "loopback capability");
            Require(!hello.GetProperty("capabilities").GetProperty("live_asr").GetBoolean(), "no fake live ASR");
            try { await client.SendAsync("start_capture", new { device_id = 123 }); throw new Exception("invalid device parameter accepted"); }
            catch (WorkerException error) when (error.Code == "INVALID_REQUEST") { }
            await client.SendAsync("start_capture", new { device_id = "missing-render-endpoint" });
            var failed = await WaitState(client, "Failed");
            Require(failed.GetProperty("error").GetString() == "DEVICE_UNAVAILABLE", "missing device fails without fallback");
            await Task.Delay(50); // terminal owner join is asynchronous
            for (int round = 0; round < rounds; round++)
            {
                var start = await client.SendAsync("start_capture");
                var epoch = start.GetProperty("epoch").GetUInt64();
                var running = await WaitState(client, "Running");
                opening.Add(running.GetProperty("opening_elapsed_s").GetDouble());
                startupPhases.Add(running.GetProperty("phase_observations").Clone());
                Require(running.GetProperty("startup_deadline_s").GetDouble() == 10, "worker startup deadline");
                endpoint = running.GetProperty("endpoint").Clone();
                var clock = Stopwatch.StartNew();
                JsonElement state;
                do
                {
                    state = await CaptureState(client);
                    Require(state.GetProperty("state").GetString() == "Running", "capture remained healthy");
                    var ping = Stopwatch.StartNew();
                    await client.SendAsync("ping", new { nonce = "during-loopback" });
                    pingMax = Math.Max(pingMax, ping.Elapsed.TotalSeconds);
                    Require(clock.Elapsed.TotalSeconds < 15, "real loopback PCM received");
                    await Task.Delay(20);
                } while (state.GetProperty("accepted_audio_s").GetDouble() < 2);
                Require(state.GetProperty("epoch").GetUInt64() == epoch, "capture epoch identity");
                packets += state.GetProperty("stats").GetProperty("packets").GetUInt64();
                acceptedAudio += state.GetProperty("accepted_audio_s").GetDouble();
                clock.Restart();
                await client.SendAsync("stop_capture");
                stopResponse.Add(clock.Elapsed.TotalSeconds);
                var stopped = await WaitState(client, "Stopped");
                stopJoin.Add(clock.Elapsed.TotalSeconds);
                var audio = stopped.GetProperty("accepted_audio_s").GetDouble();
                await client.SendAsync("stop_capture");
                await Task.Delay(100);
                Require((await CaptureState(client)).GetProperty("accepted_audio_s").GetDouble() == audio, "no PCM accepted after stop");
            }
            Require((await client.ReadHistoryAsync()).Records.Count == 0, "capture meter never makes captions");
            // Stop immediately after Start acceptance, without waiting for native ready.
            await client.SendAsync("start_capture");
            await client.SendAsync("stop_capture");
            var earlyStop = await WaitState(client, "Stopped");
            Require(!earlyStop.GetProperty("awaiting_capture_join").GetBoolean(), "early stop joined owner");
            var earlyAudio = earlyStop.GetProperty("accepted_audio_s").GetDouble();
            await Task.Delay(50);
            Require((await CaptureState(client)).GetProperty("accepted_audio_s").GetDouble() == earlyAudio, "no PCM after early stop");
            await client.SendAsync("start_capture");
            await WaitState(client, "Running");
            using var process = Process.GetProcessById(client.ProcessId);
            _ = process.Handle; // Retain the handle before a fast shutdown can remove the PID.
            await client.SendAsync("shutdown");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Require(process.ExitCode == 0, "shutdown while capturing joined normally");
        }
        // Parent stdin EOF must also stop owned WASAPI and normalization threads.
        using (var process = new Process
        {
            StartInfo = new ProcessStartInfo(args[0])
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        })
        {
            process.StartInfo.ArgumentList.Add("--diagnostic-capture");
            process.Start();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await Raw("hello", new { client = "CaptureEofSmoke", protocol_major = 1 });
                await Raw("start_capture", new { });
                var deadline = Stopwatch.StartNew();
                while ((await Raw("get_state", new { })).GetProperty("diagnostic_capture").GetProperty("state").GetString() != "Running")
                { Require(deadline.Elapsed.TotalSeconds < 12, "EOF capture start"); await Task.Delay(5); }
                process.StandardInput.Close();
                while (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is not null) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Require(process.ExitCode == 0, "parent EOF joined normally");
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } await errors; }
            async Task<JsonElement> Raw(string method, object parameters)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { v = 1, kind = "command", request_id = method, method, @params = parameters }));
                await process.StandardInput.FlushAsync();
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Require(line is not null, "raw capture response");
                    using var doc = JsonDocument.Parse(line!);
                    if (doc.RootElement.GetProperty("kind").GetString() != "response") continue;
                    Require(doc.RootElement.GetProperty("ok").GetBoolean(), "raw capture request successful");
                    return doc.RootElement.GetProperty("result").Clone();
                }
            }
        }
        var report = new
        {
            passed = true,
            input = "real_WASAPI_loopback",
            normalization = "16000_Hz_mono_512_samples",
            rounds,
            opening_elapsed_s = opening,
            opening_max_s = opening.Max(),
            startup_phase_observations = startupPhases,
            startup_deadline_s = 10,
            stop_immediately_after_start_joined = true,
            packets,
            accepted_audio_s = acceptedAudio,
            stop_response_max_s = stopResponse.Max(),
            stop_join_max_s = stopJoin.Max(),
            ping_max_s = pingMax,
            missing_device_rejected = true,
            active_shutdown = true,
            active_parent_eof = true,
            live_asr = false,
            endpoint
        };
        File.WriteAllText(args[2], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
    }
    finally { if (playing) Native.PlaySound(null, IntPtr.Zero, 0); }
}

static async Task<JsonElement> CaptureState(WorkerClient client) => (await client.SendAsync("get_state")).GetProperty("diagnostic_capture");
static async Task<JsonElement> WaitState(WorkerClient client, string expected)
{
    var clock = Stopwatch.StartNew();
    JsonElement last = default;
    while (clock.Elapsed.TotalSeconds < 12)
    {
        var state = await CaptureState(client);
        last = state.Clone();
        if (state.GetProperty("state").GetString() == expected) return state;
        if (state.GetProperty("state").GetString() == "Failed") throw new Exception("Capture failed: " + state.GetRawText());
        await Task.Delay(5);
    }
    throw new TimeoutException("capture " + expected + ": " + last.GetRawText());
}
static void Require(bool valid, string label) { if (!valid) throw new Exception("Capture smoke failed: " + label); }
static class Native
{
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PlaySound(string? path, IntPtr module, uint flags);
}
