using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;

namespace EchoSub.Desktop;

internal static class OverlayProbe
{
    public static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string reportPath)
    {
        OverlayWindow? overlay = null;
        var exitCode = 0;
        try
        {
            reportPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            var foregroundBefore = WindowsOverlayPlatform.ForegroundWindow();
            overlay = new OverlayWindow();
            overlay.ResetPlacement();
            overlay.Show();
            await Task.Delay(700);
            var shown = Snapshot(overlay);
            var foregroundAfterShow = WindowsOverlayPlatform.ForegroundWindow();
            overlay.Width = 620;
            overlay.Height = 220;
            overlay.Position = new PixelPoint(overlay.Position.X + 24, overlay.Position.Y - 24);
            overlay.SetCardOpacity(0.55);
            await Task.Delay(300);
            var adjusted = Snapshot(overlay);
            var geometryPassed = Math.Abs(overlay.Bounds.Width - 620) < 1 &&
                Math.Abs(overlay.Bounds.Height - 220) < 1;
            var imagePath = Path.ChangeExtension(reportPath, ".png");
            var scale = overlay.RenderScaling;
            using (var bitmap = new RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(overlay.Bounds.Width * scale), (int)Math.Ceiling(overlay.Bounds.Height * scale)),
                new Vector(96 * scale, 96 * scale)))
            {
                bitmap.Render(overlay);
                bitmap.Save(imagePath);
            }
            overlay.Hide();
            var hidden = Snapshot(overlay);
            var hidePassed = !overlay.IsVisible &&
                (!OperatingSystem.IsWindows() || !WindowsOverlayPlatform.Inspect(overlay).Visible);
            overlay.Show();
            await Task.Delay(300);
            var reshown = Snapshot(overlay);
            var foregroundAfterReshow = WindowsOverlayPlatform.ForegroundWindow();
            bool? focusPreserved = foregroundBefore == 0 ? null :
                foregroundBefore == foregroundAfterShow && foregroundBefore == foregroundAfterReshow;
            var native = WindowsOverlayPlatform.Inspect(overlay);
            var propertiesPassed = !OperatingSystem.IsWindows() ||
                (native.Available && native.Visible && native.Topmost && native.NoActivate && native.ToolWindow);
            var displayPassed = geometryPassed && hidePassed && overlay.IsVisible &&
                overlay.ActualTransparencyLevel == WindowTransparencyLevel.Transparent;
            if (!propertiesPassed || !displayPassed || focusPreserved == false) exitCode = 1;
            var report = new
            {
                task = "T00-04.3",
                os = Environment.OSVersion.ToString(),
                framework = "Avalonia 12.0.1",
                mock = true,
                foreground_before = $"0x{foregroundBefore:X}",
                foreground_after_show = $"0x{foregroundAfterShow:X}",
                foreground_after_reshow = $"0x{foregroundAfterReshow:X}",
                focus_preserved = focusPreserved,
                focus_result = focusPreserved is null ? "BLOCKED: foreground HWND unavailable in this session/platform" : focusPreserved.Value ? "PASS" : "FAIL",
                native_properties_result = !OperatingSystem.IsWindows() ? "SKIPPED: no native platform adapter" : propertiesPassed ? "PASS" : "FAIL",
                geometry_visibility_transparency_result = displayPassed ? "PASS" : "FAIL",
                shown, adjusted, hidden, reshown,
                rendered_frame = imagePath,
                unverified = new[] { "physical drag/resize", "borderless game composition", "other-app typing/mouse interaction", "multi-monitor/DPI changes", "macOS Spaces" }
            };
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            exitCode = 1;
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { error = error.ToString() }));
        }
        finally
        {
            overlay?.Close();
            desktop.Shutdown(exitCode);
        }
    }

    private static object Snapshot(OverlayWindow window) => new
    {
        visible = window.IsVisible,
        width = window.Bounds.Width,
        height = window.Bounds.Height,
        x = window.Position.X,
        y = window.Position.Y,
        scale = window.RenderScaling,
        transparency = window.ActualTransparencyLevel.ToString(),
        native = WindowsOverlayPlatform.Inspect(window)
    };
}
