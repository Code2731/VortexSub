using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;

namespace EchoSub.Desktop;

// An opt-in observation run: real model output, synthetic ASR, real UI clock.
// Stores bounded state transitions and screenshots. Does not simulate reading time.
internal static class UiModelSequence
{
    public static async Task CaptureAsync(MainWindow window, string output)
    {
        var overlay = window.SessionProbeOverlay ?? throw new IOException("Overlay unavailable");
        // Allow the preceding single-sentence capture to leave the reading slots.
        var drain = Stopwatch.StartNew();
        while (drain.Elapsed.TotalSeconds < 12)
        {
            var state = overlay.ReadingState();
            if (state.Upper.Length == 0 && state.Lower.Length == 0) break;
            await Task.Delay(50);
        }
        string[] sources = ["The train leaves at nine.", "Please bring your ticket.",
            "Keep the door open.", "Wait here for a moment.",
            "Do not turn off the power.", "We can start when everyone is ready."];
        var clock = Stopwatch.StartNew();
        var deliveries = new List<object>();
        var firstDisplays = new List<object>();
        var deliveryAt = new Dictionary<ulong, double>();
        var transitions = new List<object>();
        var baseline = window.CaptureModelRecords.Select(record => record.SegmentId).ToHashSet();
        var observed = new HashSet<ulong>();
        var displayed = new HashSet<ulong>();
        var sampleCount = 0;
        var frameCount = 0;
        var previousUpper = "";
        var previousLower = "";
        var next = 0;
        var sentAt = new Dictionary<string, double>();
        while (clock.Elapsed.TotalSeconds < 24)
        {
            if (next < sources.Length && clock.Elapsed.TotalSeconds >= next * 0.65)
            {
                sentAt[sources[next]] = clock.Elapsed.TotalSeconds;
                await window.CaptureModelInputAsync(sources[next++]);
            }
            foreach (var record in window.CaptureModelRecords)
            {
                if (baseline.Contains(record.SegmentId) || record.TranslationState != "Done" || !observed.Add(record.SegmentId)) continue;
                deliveryAt[record.SegmentId] = clock.Elapsed.TotalSeconds;
                deliveries.Add(new { segment = record.SegmentId, source = record.Source, translated = record.Translation,
                    at_s = clock.Elapsed.TotalSeconds, delivery_s = clock.Elapsed.TotalSeconds - sentAt.GetValueOrDefault(record.Source) });
            }
            var reading = overlay.ReadingState();
            var keys = overlay.ReadingIdentities();
            void RecordDisplay(CaptionIdentity? identity, string text, string slot)
            {
                if (identity is not { } key || text.Length == 0 || !displayed.Add(key.Segment) || baseline.Contains(key.Segment)) return;
                var at = clock.Elapsed.TotalSeconds;
                firstDisplays.Add(new { segment = key.Segment, slot, at_s = at,
                    reading_wait_s = deliveryAt.TryGetValue(key.Segment, out var deliveredAt) ? (double?)(at - deliveredAt) : null });
            }
            RecordDisplay(keys.Upper, reading.Upper, "upper");
            RecordDisplay(keys.Lower, reading.Lower, "lower");
            sampleCount++;
            if (reading.Upper != previousUpper || reading.Lower != previousLower)
            {
                previousUpper = reading.Upper; previousLower = reading.Lower;
                var runs = overlay.ReadingRuns();
                transitions.Add(new { at_s = clock.Elapsed.TotalSeconds, upper = reading.Upper, lower = reading.Lower,
                    upper_segment = keys.Upper?.Segment, lower_segment = keys.Lower?.Segment,
                    upper_y = reading.UpperBounds.Y, lower_y = reading.LowerBounds.Y,
                    upper_runs = runs.Upper.Length, lower_runs = runs.Lower.Length });
                if (frameCount < 24)
                {
                    var scale = overlay.RenderScaling;
                    using var image = new RenderTargetBitmap(new PixelSize(
                        (int)Math.Ceiling(overlay.Bounds.Width * scale), (int)Math.Ceiling(overlay.Bounds.Height * scale)),
                        new Vector(96 * scale, 96 * scale));
                    image.Render(overlay);
                    image.Save(Path.Combine(output, $"sequence-{frameCount++:D2}.png"));
                }
            }
            await Task.Delay(50);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "sequence.json"), JsonSerializer.Serialize(new
        {
            scope = "Real HTTP translations; six synthetic final ASR inputs every 0.65 seconds; UI state sampled near 0.05 seconds; Avalonia renders, not compositor frames",
            elapsed_s = clock.Elapsed.TotalSeconds, sample_count = sampleCount, frame_count = frameCount,
            sent = next, translated = observed.Count, displayed_segments = displayed.Where(id => !baseline.Contains(id)).Order().ToArray(),
            deliveries, first_displays = firstDisplays, transitions
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
