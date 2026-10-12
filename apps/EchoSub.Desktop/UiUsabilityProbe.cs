using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace EchoSub.Desktop;

// Synthetic routed keys through production controls; real owned mock worker.
internal static class UiUsabilityProbe
{
    internal static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string worker, string report)
    {
        var checks = new List<object>();
        var clock = Stopwatch.StartNew();
        MainWindow? window = null;
        var exit = 0;
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed, elapsed_s = clock.Elapsed.TotalSeconds });
            if (!passed) throw new IOException(name);
        }
        async Task Wait(Func<bool> condition, string name)
        {
            var deadline = clock.Elapsed.TotalSeconds + 8;
            while (!condition() && clock.Elapsed.TotalSeconds < deadline) await Task.Delay(25);
            Check(condition(), name);
        }
        void Key(Window target, Avalonia.Input.Key key, bool shift = false)
        {
            var focused = target.FocusManager?.GetFocusedElement() as InputElement
                ?? throw new IOException("Keyboard fixture has no focused element");
            var modifiers = shift ? KeyModifiers.Shift : KeyModifiers.None;
            focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
            focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers });
        }
        string FocusName(Window target)
        {
            var focused = target.FocusManager?.GetFocusedElement();
            return focused is Button button ? button.Content?.ToString() ?? "button"
                : focused is Control control ? ControlAutomationPeer.CreatePeerForElement(control)?.GetName() ?? control.GetType().Name
                : focused?.GetType().Name ?? "none";
        }
        async Task Frame(string name)
        {
            await Task.Delay(100);
            var scale = window!.RenderScaling;
            using var image = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
                (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
            image.Render(window);
            image.Save(Path.Combine(Path.GetDirectoryName(report)!, name + ".png"));
        }
        try
        {
            report = Path.GetFullPath(report);
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            window = new MainWindow(Path.GetFullPath(worker));
            desktop.MainWindow = window;
            window.Show();
            await Wait(() => window.SessionProbeState is { State: "Idle", Busy: false, Worker: not null }, "owned mock worker connects");
            foreach (var (id, label) in new[] { ("device", "음성을 받을 출력 장치"), ("language", "음성 언어"),
                ("width", "자막 창 폭"), ("opacity", "자막 배경 불투명도"), ("address", "번역 서버 주소"),
                ("model", "번역 모델"), ("profile", "번역 입력 방식"), ("history", "최근 자막 기록"), ("session", "저장할 세션") })
                Check(ControlAutomationPeer.CreatePeerForElement(window.UsabilityControl(id))?.GetName() == label, "automation name " + id);
            Check(window.UsabilityControl("device").Focus(NavigationMethod.Tab), "device receives keyboard focus");
            foreach (var expected in new[] { "음성 언어", "자막 시작", "번역 설정", "자막 오버레이 표시", "오버레이 위치 초기화", "오버레이에 원문 표시" })
            {
                Key(window, Avalonia.Input.Key.Tab);
                await Task.Delay(50);
                Check(FocusName(window) == expected, "Tab reaches " + expected + "; actual=" + FocusName(window));
            }
            Key(window, Avalonia.Input.Key.Tab, shift: true);
            Check(FocusName(window) == "오버레이 위치 초기화", "Shift+Tab reverses navigation");
            window.CaptureTabs!.SelectedIndex = 1;
            await Task.Delay(100);
            Check(window.UsabilityControl("width").Focus(NavigationMethod.Tab), "width receives keyboard focus");
            var widthBefore = ((Slider)window.UsabilityControl("width")).Value;
            Key(window, Avalonia.Input.Key.Right);
            Check(((Slider)window.UsabilityControl("width")).Value > widthBefore, "arrow key changes caption width");
            Key(window, Avalonia.Input.Key.Tab);
            Check(FocusName(window) == "자막 배경 불투명도", "settings Tab reaches opacity");
            Key(window, Avalonia.Input.Key.Tab, shift: true);
            Check(FocusName(window) == "자막 창 폭", "settings Shift+Tab reaches width");
            window.CaptureTabs!.SelectedIndex = 2;
            await Task.Delay(100);
            Check(window.UsabilityHistoryState is { EmptyVisible: true, ListVisible: false, Count: 0, ClearEnabled: false }, "initial empty history state");
            await Frame("01-empty-history");
            window.CaptureTabs.SelectedIndex = 0;
            Check(window.UsabilityControl("start").Focus(NavigationMethod.Tab), "start receives keyboard focus");
            Key(window, Avalonia.Input.Key.Enter);
            await Wait(() => window.SessionProbeState is { State: "Running", Busy: false }, "Enter starts session through IPC");
            await window.SessionProbeTranslateAsync("A synthetic history record.", "검증용 자막 기록입니다.");
            await Wait(() => window.UsabilityHistoryState is { EmptyVisible: false, ListVisible: true, Count: > 0 }, "record arrival replaces empty notice with list");
            Check(window.UsabilityHistoryText.Contains("원문: A synthetic history record.") &&
                window.UsabilityHistoryText.Contains("번역: 검증용 자막 기록입니다.") &&
                !window.UsabilityHistoryText.Contains(window.SessionProbeState.Session!), "history uses readable labels without internal session ID");
            window.CaptureTabs.SelectedIndex = 2;
            await Frame("02-history-populated");
            window.SessionProbeClick("stop");
            await Wait(() => window.SessionProbeState is { State: "Idle", Busy: false } && window.UsabilityHistoryState.ClearEnabled, "stop enables history deletion");
            Check(window.UsabilityControl("bilingual").IsEnabled, "paused history enables bilingual export");
            Check(window.UsabilityControl("bilingual").Focus(NavigationMethod.Tab), "bilingual export receives keyboard focus");
            Check(FocusName(window) == "원문+번역 SRT 저장", "bilingual export has accessible name");
            await Frame("04-bilingual-export-ready");
            Check(window.UsabilityControl("clear").Focus(NavigationMethod.Tab), "delete receives keyboard focus");
            Key(window, Avalonia.Input.Key.Enter);
            await Wait(() => desktop.Windows.Any(candidate => candidate != window && candidate.IsVisible), "Enter opens real confirmation dialog");
            var dialog = desktop.Windows.Single(candidate => candidate != window && candidate.IsVisible);
            Check(FocusName(dialog) == "취소", "confirmation opens with safe cancel focus");
            var actions = ((StackPanel)dialog.Content!).Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
            Check(actions[0].Focus(NavigationMethod.Tab), "cancel receives dialog keyboard focus");
            Key(dialog, Avalonia.Input.Key.Tab);
            Check(FocusName(dialog) == "기록 삭제", "dialog Tab reaches delete");
            Key(dialog, Avalonia.Input.Key.Tab, shift: true);
            Check(FocusName(dialog) == "취소", "dialog Shift+Tab reaches cancel");
            Key(dialog, Avalonia.Input.Key.Enter);
            await Wait(() => !dialog.IsVisible && !window.SessionProbeState.Busy, "Enter cancels confirmation");
            Check(window.UsabilityHistoryState.Count > 0, "cancel preserves history");
            window.UsabilityControl("clear").Focus(NavigationMethod.Tab);
            Key(window, Avalonia.Input.Key.Enter);
            await Wait(() => desktop.Windows.Any(candidate => candidate != window && candidate.IsVisible), "second confirmation opens");
            dialog = desktop.Windows.Single(candidate => candidate != window && candidate.IsVisible);
            Key(dialog, Avalonia.Input.Key.Escape);
            await Wait(() => !dialog.IsVisible && !window.SessionProbeState.Busy, "Escape cancels confirmation");
            Check(window.UsabilityHistoryState.Count > 0, "Escape preserves history");
            window.UsabilityControl("clear").Focus(NavigationMethod.Tab);
            Key(window, Avalonia.Input.Key.Enter);
            await Wait(() => desktop.Windows.Any(candidate => candidate != window && candidate.IsVisible), "third confirmation opens");
            dialog = desktop.Windows.Single(candidate => candidate != window && candidate.IsVisible);
            actions = ((StackPanel)dialog.Content!).Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
            actions[1].Focus(NavigationMethod.Tab);
            Key(dialog, Avalonia.Input.Key.Enter);
            await Wait(() => !dialog.IsVisible && window.UsabilityHistoryState is { EmptyVisible: true, ListVisible: false, Count: 0, ClearEnabled: false }, "confirmed deletion restores empty notice");
            await Frame("03-history-cleared");
        }
        catch (Exception error) { exit = 1; checks.Add(new { name = "unexpected error", passed = false, error = error.ToString() }); }
        finally
        {
            foreach (var dialog in desktop.Windows.Where(candidate => candidate != window).ToArray()) dialog.Close();
            if (window is not null)
            {
                window.Close();
                for (var attempt = 0; attempt < 100 && !window.SessionProbeState.Closed; attempt++) await Task.Delay(100);
                if (!window.SessionProbeState.Closed) exit = 1;
            }
            try
            {
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
                    { passed = exit == 0, scope = "Synthetic routed keys in real Avalonia controls; real Rust IPC; no OS keyboard injection or screen reader",
                        checks, elapsed_s = clock.Elapsed.TotalSeconds }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { exit = 1; Console.Error.WriteLine(error); }
            desktop.Shutdown(exit);
        }
    }
}
