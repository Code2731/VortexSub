using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;

namespace EchoSub.Desktop;

internal static class CaptionRegressionProbe
{
    public static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string reportPath, bool scheduled = false)
    {
        reportPath = Path.GetFullPath(reportPath);
        if (File.Exists(reportPath)) { desktop.Shutdown(2); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        OverlayWindow? overlay = null;
        var frames = new List<object>();
        CaptionRegressionResult? result = null;
        string? error = null;
        int diagnosticEvents = 0;
        double now = 0;
        var exit = 1;
        try
        {
            if (scheduled) await CaptionDiagnostics.SetEnabledAsync(true, Path.Combine(Path.GetDirectoryName(reportPath)!, "timing.jsonl"));
            overlay = new OverlayWindow(true, () => now);
            overlay.ResetPlacement(); overlay.Width = 760; overlay.Show();
            await Task.Delay(300);
            var initial = overlay.ReadingState();
            if (scheduled) overlay.SetLiveCards(new(null, null));
            var driver = new CaptionRegressionDriver(overlay.ClearReadingLines,
                (cards, time) => { now = time; if (scheduled) overlay.SetLiveCards(cards); else overlay.SetCards(cards); },
                time => { now = time; overlay.AdvanceReading(); }, () =>
                {
                    var state = overlay.ReadingState(); var keys = overlay.ReadingIdentities();
                    return new(state.Upper, state.Lower, keys.Upper, keys.Lower);
                }, overlay.ObserveReading);
            result = await CaptionRegression.RunAsync(driver, renderOnly: true, capture: async (run, time, state) =>
            {
                await Task.Delay(5);
                var layout = overlay.ReadingState();
                if (layout.UpperBounds != initial.UpperBounds || layout.LowerBounds != initial.LowerBounds)
                    throw new IOException("Reading line geometry moved");
                var name = $"case-{run:D2}-frame-{frames.Count:D3}";
                var scale = overlay.RenderScaling;
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(overlay.Bounds.Width * scale),
                    (int)Math.Ceiling(overlay.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                bitmap.Render(overlay);
                bitmap.Save(Path.Combine(Path.GetDirectoryName(reportPath)!, name + ".png"));
                object Box(Rect r) => new { x = r.X * scale, y = r.Y * scale, width = r.Width * scale, height = r.Height * scale };
                frames.Add(new { name, run, virtual_s = time, upper = state.Upper, lower = state.Lower,
                    upper_box = Box(layout.UpperBounds), lower_box = Box(layout.LowerBounds) });
            });
            if (scheduled)
            {
                // Reject malformed input through the same application entry point, without changing visible rows.
                overlay.SetLiveCards(new(null, new("", "invalid", false, "")));
                await CaptionDiagnostics.DrainAsync();
                var metadata = File.ReadLines(Path.Combine(Path.GetDirectoryName(reportPath)!, "timing.jsonl"))
                    .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
                var reading = metadata.Where(row => row.GetProperty("phase").GetString() == "caption_reading").ToArray();
                diagnosticEvents = reading.Length;
                if (CaptionDiagnostics.LastError is not null || metadata.Any(row => row.GetProperty("phase").GetString() == "dropped"))
                    throw new IOException("Caption diagnostic recording was incomplete");
                foreach (var kind in new[] { "FirstLine", "Blocked", "BlockEnded", "Reused", "InputRejected" })
                    if (!reading.Any(row => row.GetProperty("kind").GetString() == kind))
                        throw new IOException("Missing scheduled diagnostic: " + kind);
                if (reading.Any(row => row.GetProperty("reader").GetString() != "scheduled") ||
                    metadata.Any(row => row.TryGetProperty("text", out _) || row.TryGetProperty("translation", out _) || row.TryGetProperty("source", out _)))
                    throw new IOException("Unexpected caption diagnostic payload");
            }
            exit = result.Passed ? 0 : 1;
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            if (scheduled) await CaptionDiagnostics.DrainAsync();
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                passed = exit == 0, suite_version = CaptionRegression.SuiteVersion, result, error, frames,
                reader = scheduled ? "scheduled-live" : "legacy", diagnostic_events = diagnosticEvents,
                scope = "Synthetic fixed-input Avalonia snapshots; virtual clock, no live audio or continuous desktop frames"
            }, CaptionRegression.JsonOptions));
            overlay?.Close(); desktop.Shutdown(exit);
        }
    }
}
