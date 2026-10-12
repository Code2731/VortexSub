using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;

namespace EchoSub.Desktop;

internal static class NaturalSessionObservation
{
    private const string SourceUrl = "https://www.youtube.com/watch?v=lNGFAI7R0PE";
    private const int SourceStartSeconds = 600;
    private const int WarmupSeconds = 30;

    public static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string reportPath, int seconds)
    {
        MainWindow? window = null;
        var exit = 0;
        var ownsReport = false;
        var measured = new Stopwatch();
        var execution = Stopwatch.StartNew();
        var startedUtc = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        var phase = "Connecting";
        string? failure = null;
        string? warmupSessionId = null;
        string? measurementSessionId = null;
        DateTimeOffset? captureReadyUtc = null;
        DateTimeOffset? measurementStartedUtc = null;
        var samples = new List<object>();
        var playback = new List<PlaybackMarker>();
        var report = Path.GetFullPath(reportPath);

        async Task Save(string state)
        {
            if (!ownsReport) return;
            var json = JsonSerializer.Serialize(new
            {
                scope = "Actual Windows loopback/VAD/Whisper/HTTP/UI observation; no synthetic input",
                run_id = runId, status = state, phase, requested_s = seconds,
                measured_s = measured.Elapsed.TotalSeconds, execution_s = execution.Elapsed.TotalSeconds,
                started_utc = startedUtc, updated_utc = DateTimeOffset.UtcNow,
                capture_ready_utc = captureReadyUtc, measurement_started_utc = measurementStartedUtc,
                source_url = SourceUrl, source_start_s = SourceStartSeconds,
                source_end_s = SourceStartSeconds + seconds, warmup_s = WarmupSeconds,
                warmup_session_id = warmupSessionId, measurement_session_id = measurementSessionId,
                timing_log = Environment.GetEnvironmentVariable("ECHOSUB_CAPTION_TIMING_LOG"),
                failure, playback, samples,
                note = "Playback markers are controller declarations, not proof of uninterrupted playback or audio alignment. Retained counts are session-scoped, not total omissions. Memory excludes server/GPU. No quality or physical render verdict."
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(report + ".tmp", json);
            File.Move(report + ".tmp", report, overwrite: true);
        }

        async Task WaitIdle(int limitSeconds)
        {
            var wait = Stopwatch.StartNew();
            while ((window!.NaturalState.State != "Idle" || window.NaturalState.Busy) && wait.Elapsed.TotalSeconds < limitSeconds)
            {
                if (window.NaturalState.Closed) throw new IOException("Observation window closed");
                await Task.Delay(200);
            }
            if (window.NaturalState.State != "Idle" || window.NaturalState.Busy)
                throw new IOException("Native worker did not become Idle");
        }

        void RequireRunning()
        {
            if (window!.NaturalState.Closed || window.NaturalState.State != "Running" || !window.NaturalCaptureRunning)
                throw new IOException("Actual session/capture is not Running");
        }

        async Task StartCapture(string label)
        {
            phase = "Starting" + label;
            captureReadyUtc = null;
            await Save("Preparing");
            window!.NaturalClick("start");
            var wait = Stopwatch.StartNew();
            while ((window.NaturalState.State != "Running" || !window.NaturalCaptureRunning || window.NaturalState.Busy) && wait.Elapsed.TotalSeconds < 30)
            {
                samples.Add(new { phase, at_execution_s = execution.Elapsed.TotalSeconds, state = window.NaturalSnapshot() });
                if (window.NaturalState.State == "Error" || window.NaturalState.Closed)
                    throw new IOException("Actual capture failed before " + label);
                await Save("Preparing");
                await Task.Delay(1000);
            }
            RequireRunning();
            if (window.NaturalState.Busy) throw new IOException("Capture start command did not finish");
            captureReadyUtc = DateTimeOffset.UtcNow;
        }

        async Task AwaitPlayback(string label)
        {
            phase = "Await" + label + "Playback";
            var markerPath = report + "." + label.ToLowerInvariant() + ".playback.json";
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed.TotalSeconds < 120)
            {
                RequireRunning();
                await Save("AwaitPlayback");
                if (File.Exists(markerPath))
                {
                    if (new FileInfo(markerPath).Length > 8192) throw new IOException("Playback marker exceeds 8192 bytes");
                    var marker = JsonSerializer.Deserialize<PlaybackMarker>(await File.ReadAllTextAsync(markerPath))
                        ?? throw new IOException("Empty playback marker");
                    if (marker.RunId != runId || marker.Phase != label || marker.SourceUrl != SourceUrl ||
                        !double.IsFinite(marker.PositionSeconds) || marker.PositionSeconds < 0 ||
                        marker.ObservedUtc < captureReadyUtc!.Value || marker.ObservedUtc > DateTimeOffset.UtcNow.AddSeconds(2) ||
                        DateTimeOffset.UtcNow - marker.ObservedUtc > TimeSpan.FromSeconds(10) ||
                        (label == "Measurement" && Math.Abs(marker.PositionSeconds - SourceStartSeconds) > 5))
                        throw new IOException("Playback marker identity, time, source or position mismatch");
                    playback.Add(marker);
                    return;
                }
                await Task.Delay(1000);
            }
            throw new IOException("Playback marker did not arrive within 120 seconds: " + label);
        }

        async Task Observe(int duration, bool measurement)
        {
            phase = measurement ? "Observing" : "WarmingUp";
            var interval = Stopwatch.StartNew();
            var audioAtStart = window!.NaturalAcceptedAudioSeconds;
            if (measurement)
            {
                measurementStartedUtc = DateTimeOffset.UtcNow;
                measured.Start();
            }
            while (interval.Elapsed.TotalSeconds < duration)
            {
                RequireRunning();
                samples.Add(new { phase, at_s = interval.Elapsed.TotalSeconds, state = window!.NaturalSnapshot() });
                await Save(measurement ? "Running" : "WarmingUp");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, Math.Max(0, duration - interval.Elapsed.TotalSeconds))));
            }
            RequireRunning();
            if (measurement) measured.Stop();
            samples.Add(new { phase, at_s = interval.Elapsed.TotalSeconds, state = window!.NaturalSnapshot() });
            if (audioAtStart is null || !(window.NaturalAcceptedAudioSeconds > audioAtStart) || window.NaturalTranslatedCount == 0)
                throw new IOException("No accepted audio/current-session translation during " + phase);
        }

        async Task StopCapture()
        {
            phase = "Stopping";
            await Save("Stopping");
            window!.NaturalClick("stop");
            await WaitIdle(30);
        }

        try
        {
            if (seconds is < 30 or > 7200) throw new ArgumentException("Observation duration must be 30..7200 seconds");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            // Preserve evidence left by completed or interrupted runs.
            using (new FileStream(report, FileMode.CreateNew, FileAccess.Write)) { }
            ownsReport = true;
            window = new MainWindow(null, naturalObservation: true);
            desktop.MainWindow = window;
            window.Show();
            await Save("Preparing");
            await WaitIdle(120);
            phase = "Configuring";
            await window.NaturalConfigureAsync();
            var ready = Stopwatch.StartNew();
            while (!window.NaturalState.Ready && ready.Elapsed.TotalSeconds < 30) await Task.Delay(200);
            if (!window.NaturalState.Ready) throw new IOException("Native model/translation did not become ready");
            window.NaturalClick("overlay");
            await StartCapture("Warmup");
            warmupSessionId = window.NaturalSessionId ?? throw new IOException("Warmup session identity missing");
            await AwaitPlayback("Warmup");
            await Observe(WarmupSeconds, measurement: false);
            await StopCapture();
            await StartCapture("Measurement");
            measurementSessionId = window.NaturalSessionId;
            if (measurementSessionId is null || measurementSessionId == warmupSessionId)
                throw new IOException("Measurement requires a new session identity");
            await AwaitPlayback("Measurement");
            await Observe(seconds, measurement: true);
            await StopCapture();
            phase = "CompletedObservation";
        }
        catch (Exception error) { exit = 1; measured.Stop(); failure = error.Message; Console.Error.WriteLine(error); }
        finally
        {
            try
            {
                if (window is not null)
                {
                    window.Close();
                    for (var attempt = 0; attempt < 100 && !window.NaturalState.Closed; attempt++) await Task.Delay(100);
                    if (!window.NaturalState.Closed) { exit = 1; failure ??= "Desktop cleanup did not finish within 10 seconds"; }
                }
                await Save(exit == 0 ? "ObservationEnded" : "Incomplete");
            }
            catch (Exception error) { exit = 1; Console.Error.WriteLine("Observation cleanup/report failed: " + error.Message); }
            finally { desktop.Shutdown(exit); }
        }
    }

    internal sealed record PlaybackMarker(string RunId, string Phase, string SourceUrl, double PositionSeconds, DateTimeOffset ObservedUtc);
}
