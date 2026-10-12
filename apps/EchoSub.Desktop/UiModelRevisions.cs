using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace EchoSub.Desktop;

// Explicit model/UI observation. Synthetic ASR revisions are scheduled on real time.
internal static class UiModelRevisions
{
    public static async Task CaptureAsync(MainWindow window, string output)
    {
        var overlay = window.SessionProbeOverlay ?? throw new IOException("Overlay unavailable");
        var drain = Stopwatch.StartNew();
        while (drain.Elapsed.TotalSeconds < 12)
        {
            var state = overlay.ReadingState();
            if (state.Upper.Length == 0 && state.Lower.Length == 0) break;
            await Task.Delay(50);
        }
        if (overlay.ReadingState() is var remaining && (remaining.Upper.Length > 0 || remaining.Lower.Length > 0))
            throw new IOException("Earlier caption did not drain");
        ((Slider)window.UsabilityControl("width")).Value = 420;
        (double At, string Kind, string Source)[] inputs = [
            (0, "final", "Before leaving the station, check that you have your ticket and luggage, keep the emergency exit clear, and wait for the staff to tell you when it is safe to board the train."),
            (14, "partial", "Leave the door open."),
            (14.8, "partial", "Leave the door open."),
            (15.8, "partial", "Leave the door open. Wait outside."),
            (16.6, "partial", "Leave the door open. Wait outside."),
            (17.2, "final", "Keep the door closed. Wait outside.")];
        var baseline = window.CaptureModelRecords.Select(record => record.SegmentId).ToHashSet();
        var records = new List<object>();
        var transitions = new List<object>();
        var sent = new List<object>();
        var signatures = new Dictionary<ulong, string>();
        var clock = Stopwatch.StartNew();
        var next = 0;
        var frames = 0;
        var samples = 0;
        var previousUpper = "";
        var previousLower = "";
        while (clock.Elapsed.TotalSeconds < 36)
        {
            if (next < inputs.Length && clock.Elapsed.TotalSeconds >= inputs[next].At)
            {
                var input = inputs[next++];
                sent.Add(new { at_s = clock.Elapsed.TotalSeconds, kind = input.Kind, source = input.Source });
                await window.CaptureModelInputAsync(input.Source, input.Kind);
            }
            foreach (var record in window.CaptureModelRecords)
            {
                if (baseline.Contains(record.SegmentId)) continue;
                var signature = JsonSerializer.Serialize(record);
                if (signatures.GetValueOrDefault(record.SegmentId) == signature) continue;
                signatures[record.SegmentId] = signature;
                records.Add(new { at_s = clock.Elapsed.TotalSeconds, record });
            }
            var reading = overlay.ReadingState();
            if (reading.Upper != previousUpper || reading.Lower != previousLower)
            {
                previousUpper = reading.Upper; previousLower = reading.Lower;
                var keys = overlay.ReadingIdentities();
                var runs = overlay.ReadingRuns();
                transitions.Add(new { at_s = clock.Elapsed.TotalSeconds, upper = reading.Upper, lower = reading.Lower,
                    upper_identity = keys.Upper, lower_identity = keys.Lower,
                    upper_y = reading.UpperBounds.Y, lower_y = reading.LowerBounds.Y,
                    upper_runs = runs.Upper.Length, lower_runs = runs.Lower.Length });
                if (frames < 32)
                {
                    var scale = overlay.RenderScaling;
                    using var image = new RenderTargetBitmap(new PixelSize(
                        (int)Math.Ceiling(overlay.Bounds.Width * scale), (int)Math.Ceiling(overlay.Bounds.Height * scale)),
                        new Vector(96 * scale, 96 * scale));
                    image.Render(overlay);
                    image.Save(Path.Combine(output, $"revision-{frames++:D2}.png"));
                }
            }
            samples++;
            await Task.Delay(50);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "revisions.json"), JsonSerializer.Serialize(new
        {
            scope = "Real HTTP translation; synthetic long final, repeated partials, growth and changed final; 420 DIP overlay; real UI clock; Avalonia renders, not compositor frames",
            elapsed_s = clock.Elapsed.TotalSeconds, sample_count = samples, frame_count = frames,
            sent, records, transitions
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
