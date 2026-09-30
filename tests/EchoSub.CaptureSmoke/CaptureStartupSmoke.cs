using System.Diagnostics;
using System.Text.Json;
using EchoSub.Desktop;

internal static class CaptureStartupSmoke
{
    public static async Task Run(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 5)
            throw new ArgumentException("--startup worker report endpoint-manifest rounds required");
        var endpoints = JsonSerializer.Deserialize<Endpoint[]>(File.ReadAllText(args[3]))
            ?? throw new ArgumentException("Missing endpoints");
        var rounds = int.Parse(args[4]);
        if (rounds is < 1 or > 20 || endpoints.Length is < 1 or > 32)
            throw new ArgumentException("Expected 1..20 rounds and 1..32 endpoints");
        var results = new List<object>();
        var allPassed = true;
        foreach (var endpoint in endpoints)
        {
            for (var round = 1; round <= rounds; round++)
            {
                // No PlaySound, models, synthetic PCM, or default-device changes.
                await Task.Delay(500);
                JsonElement? last = null;
                string? error = null;
                double pingMax = 0, cleanupSeconds = 0;
                bool forced = false;
                bool? restartBlocked = null;
                int? exitCode = null;
                CaptureDump.Result? dump = null;
                var client = WorkerClient.Start(args[1], arguments: ["--diagnostic-capture"]);
                using var process = Process.GetProcessById(client.ProcessId);
                _ = process.Handle;
                try
                {
                    var hello = await client.SendAsync("hello", new { client = "CaptureStartupSmoke", protocol_major = 1 });
                    var caps = hello.GetProperty("capabilities");
                    if (hello.GetProperty("implementation").GetString() != "wasapi-capture-diagnostic" ||
                        !caps.GetProperty("system_audio").GetBoolean() ||
                        caps.GetProperty("live_asr").GetBoolean() || caps.GetProperty("asr").GetBoolean() || caps.GetProperty("vad").GetBoolean())
                        throw new Exception("Startup probe must omit inference");
                    var initial = await client.SendAsync("get_state");
                    if (initial.GetProperty("model").GetProperty("state").GetString() != "NotInstalled" ||
                        initial.GetProperty("diagnostic_asr").GetProperty("enabled").GetBoolean())
                        throw new Exception("Startup probe unexpectedly loaded a native model");
                    await client.SendAsync("start_capture", new { device_id = endpoint.Id });
                    var clock = Stopwatch.StartNew();
                    while (clock.Elapsed.TotalSeconds < 12)
                    {
                        last = (await client.SendAsync("get_state")).GetProperty("diagnostic_capture").Clone();
                        var ping = Stopwatch.StartNew();
                        await client.SendAsync("ping", new { nonce = "startup" });
                        pingMax = Math.Max(pingMax, ping.Elapsed.TotalSeconds);
                        var state = last.Value.GetProperty("state").GetString();
                        if (state == "Running")
                        {
                            // Record received PCM; no packets does not prove digital silence.
                            await Task.Delay(300);
                            last = (await client.SendAsync("get_state")).GetProperty("diagnostic_capture").Clone();
                            break;
                        }
                        if (state == "Failed")
                        {
                            error = last.Value.GetProperty("error").GetString();
                            if (last.Value.GetProperty("awaiting_capture_join").GetBoolean())
                            {
                                try { await client.SendAsync("start_capture", new { device_id = endpoint.Id }); restartBlocked = false; }
                                catch (WorkerException failure) when (failure.Code == "INVALID_STATE") { restartBlocked = true; }
                            }
                            break;
                        }
                        await Task.Delay(20);
                    }
                    if (last?.GetProperty("state").GetString() != "Running" && error is null)
                        error = last?.GetProperty("error").GetString() ?? "CLIENT_START_TIMEOUT";
                    if ((await client.ReadHistoryAsync()).Records.Count != 0)
                        throw new Exception("PCM startup must not produce history");
                }
                catch (Exception failure) { error ??= failure.Message; }
                finally
                {
                    if (error is not null && !process.HasExited &&
                        last?.GetProperty("awaiting_capture_join").GetBoolean() == true)
                    {
                        var dumpPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2]))!, $"case-{results.Count + 1}-worker.dmp");
                        dump = await CaptureDump.Collect(process, dumpPath);
                    }
                    var cleanup = Stopwatch.StartNew();
                    try
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        if (!process.HasExited)
                        {
                            await client.SendAsync("shutdown", cancellationToken: deadline.Token);
                            await process.WaitForExitAsync(deadline.Token);
                        }
                    }
                    catch (Exception failure) when (failure is OperationCanceledException or IOException or WorkerException)
                    {
                        if (!process.HasExited)
                        {
                            forced = true;
                            process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        }
                    }
                    await client.DisposeAsync();
                    cleanupSeconds = cleanup.Elapsed.TotalSeconds;
                    exitCode = process.ExitCode;
                }
                var passed = error is null && !forced && exitCode == 0;
                allPassed &= passed;
                results.Add(new
                {
                    endpoint = endpoint.Name, device_id = endpoint.Id, round, passed, error,
                    capture = last, dump, ping_max_s = pingMax, restart_blocked_before_join = restartBlocked,
                    forced_termination = forced, cleanup_s = cleanupSeconds, exit_code = exitCode
                });
                WriteReport(); // Preserve every completed case even when a later one fails.
                Console.WriteLine(JsonSerializer.Serialize(new { endpoint = endpoint.Name, round, passed, error, forced, cleanup_s = cleanupSeconds }));
            }
        }
        if (!allPassed) throw new Exception("Capture startup stability failed; see report " + args[2]);

        void WriteReport() => File.WriteAllText(args[2], JsonSerializer.Serialize(new
        {
            passed = allPassed && results.Count == endpoints.Length * rounds,
            completed_cases = results.Count, expected_cases = endpoints.Length * rounds,
            inference_enabled = false, owned_playback = false, other_system_audio_isolated = false,
            worker_startup_deadline_s = 10, client_observation_deadline_s = 12,
            cleanup_deadline_s = 5, process_idle_delay_s = 0.5, results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal sealed record Endpoint(string? Id, string Name);
}
