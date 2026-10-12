using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;

namespace EchoSub.Desktop;

// Explicit screenshot command. Production controls with an owned mock worker;
// Optional existing Hy-MT2 HTTP endpoint uses real translation with synthetic ASR.
// Captures Avalonia output, not the compositor. Never captures audio or saves preferences.
internal static class UiCapture
{
    public static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string worker, string output, string? modelEndpoint = null, bool sequence = false, bool revisions = false)
    {
        MainWindow? window = null;
        var exit = 0;
        try
        {
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);
            if (modelEndpoint is not null && !MainWindow.RecoveryEndpointValid(modelEndpoint))
                throw new ArgumentException("Model capture requires a numeric loopback HTTP endpoint");
            window = new MainWindow(Path.GetFullPath(worker), translationProbe: modelEndpoint is not null);
            desktop.MainWindow = window;
            window.Show();
            for (var attempt = 0; attempt < 80 && window.SessionProbeState.State != "Idle"; attempt++)
                await Task.Delay(100);
            await Task.Delay(250);
            var tabs = window.CaptureTabs ?? throw new IOException("Capture tabs unavailable");
            var frames = new List<object>();
            object? inference = null;
            async Task Capture(string name, int tab, double offset = 0)
            {
                tabs.SelectedIndex = tab;
                await Task.Delay(150);
                var scroll = (ScrollViewer)((TabItem)tabs.SelectedItem!).Content!;
                scroll.Offset = new Vector(0, offset);
                await Task.Delay(250);
                var scale = window.RenderScaling;
                using var image = new RenderTargetBitmap(new PixelSize(
                    (int)Math.Ceiling(window.Bounds.Width * scale),
                    (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                image.Render(window);
                image.Save(Path.Combine(output, name + ".png"));
                frames.Add(new { name, width = window.Bounds.Width, height = window.Bounds.Height,
                    scale, tab, scroll_y = scroll.Offset.Y, state = window.SessionProbeStatus });
            }
            if (modelEndpoint is not null)
            {
                await window.RecoveryConfigure(modelEndpoint, "hymt2-greedy");
                var readyClock = System.Diagnostics.Stopwatch.StartNew();
                while (!window.RecoveryTranslationReady && readyClock.Elapsed.TotalSeconds < 12)
                    await Task.Delay(100);
                if (!window.RecoveryTranslationReady)
                    throw new IOException("Model catalog unavailable: " + window.RecoveryTranslationError);
                await Capture("10-model-ready", 1, 245);
                if (revisions) window.CaptureModelEnablePartials();
                window.SessionProbeClick("overlay");
                window.SessionProbeClick("start");
                var sessionClock = System.Diagnostics.Stopwatch.StartNew();
                while (window.SessionProbeState.State != "Running" && sessionClock.Elapsed.TotalSeconds < 8)
                    await Task.Delay(100);
                if (window.SessionProbeState.State != "Running") throw new IOException("Synthetic session did not start");
                const string source = "Please keep the subtitles on screen long enough to read them.";
                var inferenceClock = System.Diagnostics.Stopwatch.StartNew();
                await window.CaptureModelInputAsync(source);
                while (window.CaptureModelOutput is null && inferenceClock.Elapsed.TotalSeconds < 30)
                    await Task.Delay(50);
                var translated = window.CaptureModelOutput ?? throw new IOException("Model translation not delivered within 30 seconds");
                var elapsed = inferenceClock.Elapsed.TotalSeconds;
                await Task.Delay(150);
                var overlay = window.SessionProbeOverlay ?? throw new IOException("Overlay unavailable");
                var reading = overlay.ReadingState();
                var scale = overlay.RenderScaling;
                using (var image = new RenderTargetBitmap(new PixelSize(
                    (int)Math.Ceiling(overlay.Bounds.Width * scale), (int)Math.Ceiling(overlay.Bounds.Height * scale)),
                    new Vector(96 * scale, 96 * scale)))
                {
                    image.Render(overlay);
                    image.Save(Path.Combine(output, "11-model-overlay.png"));
                }
                inference = new { source, translated, delivery_s = elapsed, upper = reading.Upper, lower = reading.Lower,
                    profile = "hymt2-greedy", asr = "synthetic text; no audio", endpoint = modelEndpoint };
                await Capture("12-model-main", 0);
                if (sequence)
                    await UiModelSequence.CaptureAsync(window, output);
                if (revisions)
                    await UiModelRevisions.CaptureAsync(window, output);
                window.SessionProbeClick("stop");
                var stopClock = System.Diagnostics.Stopwatch.StartNew();
                while (window.SessionProbeState.State != "Idle" && stopClock.Elapsed.TotalSeconds < 8)
                    await Task.Delay(100);
            }
            await Capture("01-main", 0);
            await Capture("02-settings", 1);
            await Capture("09-translation-settings", 1, 245);
            await Capture("03-settings-bottom", 1, 10000);
            await Capture("04-history", 2);
            await Capture("05-diagnostics", 3);
            if (modelEndpoint is null)
            {
                // Static sample text only. Observe font geometry and native lock flags.
                var appearanceOverlay = new OverlayWindow();
                try
                {
                    appearanceOverlay.SetSourceVisible(true);
                    appearanceOverlay.Show();
                    foreach (var size in new[] { 18, 26, 48 })
                    {
                        appearanceOverlay.SetCaptionFontSize(size);
                        await CaptureAppearance("font-" + size);
                    }
                    if (OperatingSystem.IsWindows())
                    {
                        appearanceOverlay.SetClickThrough(true);
                        await CaptureAppearance("font-48-locked");
                        appearanceOverlay.SetClickThrough(false);
                        await CaptureAppearance("font-48-unlocked");
                    }
                    async Task CaptureAppearance(string name)
                    {
                        await Task.Delay(250);
                        var scale = appearanceOverlay.RenderScaling;
                        using var image = new RenderTargetBitmap(new PixelSize(
                            (int)Math.Ceiling(appearanceOverlay.Bounds.Width * scale),
                            (int)Math.Ceiling(appearanceOverlay.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                        image.Render(appearanceOverlay);
                        image.Save(Path.Combine(output, name + ".png"));
                        var reading = appearanceOverlay.ReadingState();
                        frames.Add(new { name, font = appearanceOverlay.ReadingState(),
                            width = appearanceOverlay.Bounds.Width, height = appearanceOverlay.Bounds.Height,
                            upper_bounds = reading.UpperBounds, lower_bounds = reading.LowerBounds,
                            native = WindowsOverlayPlatform.Inspect(appearanceOverlay), scope = "static sample; no physical click verification" });
                    }
                }
                finally { appearanceOverlay.Close(); }
            }
            if (window.FirstUseGuide is { } guide)
            {
                guide.IsExpanded = true;
                await Capture("08-first-use", 0);
                guide.IsExpanded = false;
            }
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await Capture("06-minimum-main", 0);
            await Capture("07-minimum-settings", 1);
            await File.WriteAllTextAsync(Path.Combine(output, "capture.json"), JsonSerializer.Serialize(new
            {
                scope = modelEndpoint is null ? "Production controls; mock worker; no inference; Avalonia renders, not compositor screenshots"
                    : "Production controls and real HTTP translation; synthetic ASR; Avalonia renders, not compositor screenshots",
                inference, frames
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) { exit = 1; Console.Error.WriteLine(error); }
        finally
        {
            if (window is not null)
            {
                window.Close();
                for (var attempt = 0; attempt < 100 && !window.SessionProbeState.Closed; attempt++)
                    await Task.Delay(100);
            }
            desktop.Shutdown(exit);
        }
    }
}
