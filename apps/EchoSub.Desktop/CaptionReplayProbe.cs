using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;

namespace EchoSub.Desktop;

// Opt-in diagnostic: recorded native events, production deck/overlay, virtual clock.
internal static class CaptionReplayProbe
{
    public static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string inputPath, string reportPath)
    {
        OverlayWindow? overlay = null;
        var checks = new List<object>();
        var frames = new List<object>();
        double now = 0;
        var exit = 0;
        void Require(bool passed, string name, int run)
        {
            checks.Add(new { name, passed, run, virtual_s = now });
            if (!passed) throw new IOException(name + " run " + run);
        }
        try
        {
            inputPath = Path.GetFullPath(inputPath);
            reportPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            if (new FileInfo(inputPath).Length > 32 * 1024 * 1024) throw new IOException("Replay input exceeds 32 MiB");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(inputPath));
            var runs = document.RootElement.GetProperty("runs");
            if (runs.GetArrayLength() is < 1 or > 24) throw new IOException("Replay requires 1..24 runs");
            overlay = new OverlayWindow(true, () => now);
            overlay.ResetPlacement(); overlay.Width = 420; overlay.Show();
            await Task.Delay(300);
            int runIndex = 0;
            foreach (var run in runs.EnumerateArray())
            {
                now = 0; overlay.ClearReadingLines();
                var product = "recorded-replay-" + runIndex;
                var deck = new CaptionDeck { LineReadingManaged = true };
                var events = new List<(double At, HistoryRecord Record)>();
                foreach (var item in run.GetProperty("events").EnumerateArray())
                {
                    var message = item.GetProperty("message");
                    if (!message.GetProperty("payload").TryGetProperty("record", out var recordJson)) continue;
                    var at = item.GetProperty("at_s").GetDouble();
                    if (!double.IsFinite(at) || at is < 0 or > 120) throw new IOException("Invalid replay timestamp");
                    var record = recordJson.Deserialize<HistoryRecord>() ?? throw new IOException("Missing record");
                    if (record.Source.Length > 65536 || record.Translation.Length > 65536) throw new IOException("Replay text too large");
                    events.Add((at, record with { ProductSessionId = product }));
                    if (events.Count > 4096) throw new IOException("Too many replay records");
                }
                if (events.Count == 0) throw new IOException("No recorded history events");
                events = events.OrderBy(item => item.At).ToList();
                var final = run.GetProperty("final_record").Deserialize<HistoryRecord>() ?? throw new IOException("Missing final");
                Require(final.TranslationState == "Done" && final.Translation.Length > 0, "completed final input", runIndex);
                var end = events[^1].At + 30;
                var times = Enumerable.Range(0, (int)Math.Ceiling(end * 10) + 1).Select(i => i / 10.0)
                    .Concat(events.Select(item => item.At)).Distinct().Order().ToArray();
                var changedAt = new double[2];
                var emitted = new System.Text.StringBuilder();
                int eventIndex = 0, frameIndex = 0;
                var previous = overlay.ReadingState();
                var bounds = (previous.UpperBounds, previous.LowerBounds);
                foreach (var time in times)
                {
                    now = time;
                    var hadEvent = false;
                    while (eventIndex < events.Count && events[eventIndex].At <= now)
                    {
                        var record = events[eventIndex++].Record;
                        overlay.ValidateSession(product, record.SessionId, record.Epoch, true);
                        overlay.SetCards(deck.Update([record], product, record.SessionId, record.Epoch, true, now));
                        hadEvent = true;
                    }
                    overlay.SetCards(deck.Tick(now)); overlay.AdvanceReading();
                    await Task.Delay(1);
                    var state = overlay.ReadingState();
                    Require(state.UpperBounds == bounds.UpperBounds && state.LowerBounds == bounds.LowerBounds,
                        "fixed line geometry", runIndex);
                    var oldText = new[] { previous.Upper, previous.Lower };
                    var newText = new[] { state.Upper, state.Lower };
                    for (int slot = 0; slot < 2; slot++)
                    {
                        if (newText[slot] == oldText[slot]) continue;
                        var append = oldText[slot].Length > 0 && newText[slot].StartsWith(oldText[slot], StringComparison.Ordinal);
                        if (oldText[slot].Length > 0 && !append)
                            Require(now - changedAt[slot] >= CaptionLines.HoldSeconds - 0.000001,
                                "replacement respects reading deadline slot " + slot, runIndex);
                        if (newText[slot].Length > 0)
                        {
                            emitted.Append(append ? newText[slot][oldText[slot].Length..] : newText[slot]);
                            changedAt[slot] = now;
                        }
                    }
                    if (hadEvent || state.Upper != previous.Upper || state.Lower != previous.Lower)
                    {
                        var name = $"run-{runIndex:D2}-frame-{frameIndex++:D3}";
                        var scale = overlay.RenderScaling;
                        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(overlay.Bounds.Width * scale),
                            (int)Math.Ceiling(overlay.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                        bitmap.Render(overlay);
                        bitmap.Save(Path.Combine(Path.GetDirectoryName(reportPath)!, name + ".png"));
                        object Box(Rect r) => new { x = r.X * scale, y = r.Y * scale, width = r.Width * scale, height = r.Height * scale };
                        frames.Add(new { name, run = runIndex, virtual_s = now, upper = state.Upper, lower = state.Lower,
                            upper_box = Box(state.UpperBounds), lower_box = Box(state.LowerBounds) });
                    }
                    previous = state;
                }
                var text = emitted.ToString();
                var checkedTarget = final.Translation;
                var first = text.IndexOf(checkedTarget, StringComparison.Ordinal);
                if (first < 0)
                {
                    // The display assembler may reuse an identical finished clause.
                    // Verify that literal shown prefix plus the corrected remainder.
                    var reused = CaptionAssembly.ReusablePrefix(text, final.Translation);
                    if (reused > 0)
                    {
                        checkedTarget = final.Translation[reused..];
                        first = text.IndexOf(checkedTarget, reused, StringComparison.Ordinal);
                    }
                }
                Require(first >= 0, "final target covered by displayed prefix and corrected remainder", runIndex);
                Require(first >= 0 && text.IndexOf(checkedTarget, first + checkedTarget.Length, StringComparison.Ordinal) < 0,
                    "identical final remainder does not replay completed target", runIndex);
                Require(previous.Upper.Length == 0 && previous.Lower.Length == 0, "reading eventually drains", runIndex);
                overlay.ValidateSession(product, final.SessionId, final.Epoch, false);
                runIndex++;
            }
        }
        catch (Exception error) { exit = 1; checks.Add(new { error = error.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { passed = exit == 0,
                scope = "Recorded native history through production deck and rendered overlay; virtual clock, no continuous desktop frames or live audio",
                inputPath, checks, frames }, new JsonSerializerOptions { WriteIndented = true }));
            overlay?.Close(); desktop.Shutdown(exit);
        }
    }
}
