using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;

namespace EchoSub.Desktop;

// Explicit screenshot command. Production controls with an owned mock worker;
// no real audio capture or inference. Captures Avalonia output, not the compositor.
internal static class UiCapture
{
    public static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string worker, string output)
    {
        MainWindow? window = null;
        var exit = 0;
        try
        {
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);
            window = new MainWindow(Path.GetFullPath(worker));
            desktop.MainWindow = window;
            window.Show();
            for (var attempt = 0; attempt < 80 && window.SessionProbeState.State != "Idle"; attempt++)
                await Task.Delay(100);
            await Task.Delay(250);
            var tabs = window.CaptureTabs ?? throw new IOException("Capture tabs unavailable");
            var frames = new List<object>();
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
            await Capture("01-main", 0);
            await Capture("02-settings", 1);
            await Capture("09-translation-settings", 1, 245);
            await Capture("03-settings-bottom", 1, 10000);
            await Capture("04-history", 2);
            await Capture("05-diagnostics", 3);
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
                scope = "Current production MainWindow controls; explicit mock worker; saved Avalonia renders, not OS/compositor screenshots",
                frames
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
