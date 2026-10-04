using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;

namespace EchoSub.Desktop;

// Real UI dispatcher/timers and owned Rust process, explicitly synthetic model input.
internal static class CaptionSessionProbe
{
    public static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string workerPath, string reportPath)
    {
        MainWindow? window = null;
        var clock = Stopwatch.StartNew();
        var checks = new List<object>();
        var frames = new List<object>();
        string? workerHash = null, appHash = null;
        var exit = 0;
        try
        {
            workerPath = Path.GetFullPath(workerPath);
            reportPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            async Task<string> Hash(string path)
            {
                await using var input = File.OpenRead(path);
                return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input)).ToLowerInvariant();
            }
            workerHash = await Hash(workerPath);
            appHash = await Hash(typeof(MainWindow).Assembly.Location);
            window = new MainWindow(workerPath);
            window.Show();
            void Require(bool condition, string name)
            {
                checks.Add(new { name, passed = condition, elapsed_s = clock.Elapsed.TotalSeconds });
                if (!condition) throw new IOException(name + " · " + window.SessionProbeStatus);
            }
            async Task Wait(Func<bool> condition, string name)
            {
                var deadline = clock.Elapsed.TotalSeconds + 8;
                while (!condition() && clock.Elapsed.TotalSeconds < deadline) await Task.Delay(25);
                Require(condition(), name);
            }
            bool Empty() => window.SessionProbeOverlay?.ReadingState() is { Upper.Length: 0, Lower.Length: 0 };
            bool Has(string text) => window.SessionProbeOverlay?.ReadingState().Upper == text;
            async Task Frame(string name)
            {
                await Task.Delay(75);
                var overlay = window.SessionProbeOverlay ?? throw new IOException("Fixture overlay missing");
                var scale = overlay.RenderScaling;
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(overlay.Bounds.Width * scale),
                    (int)Math.Ceiling(overlay.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                bitmap.Render(overlay);
                bitmap.Save(Path.Combine(Path.GetDirectoryName(reportPath)!, name + ".png"));
                var state = overlay.ReadingState();
                object Box(Rect r) => new { x = r.X * scale, y = r.Y * scale, width = r.Width * scale, height = r.Height * scale };
                frames.Add(new { name, elapsed_s = clock.Elapsed.TotalSeconds, upper = state.Upper, lower = state.Lower,
                    upper_box = Box(state.UpperBounds), lower_box = Box(state.LowerBounds), window.SessionProbeState.Session });
            }
            await Wait(() => window.SessionProbeState is { State: "Idle", Busy: false, Worker: not null }, "main window connects owned mock worker");
            window.SessionProbeClick("overlay");
            await Wait(() => window.SessionProbeOverlay?.IsVisible == true, "real overlay opens");
            window.SessionProbeClick("start");
            await Wait(() => window.SessionProbeState is { State: "Running", Busy: false }, "start button reaches running through IPC refresh");
            var firstSession = window.SessionProbeState.Session;
            await window.SessionProbeTranslateAsync("First fixture", "문 앞에서 기다려.");
            await Wait(() => Has("문 앞에서 기다려."), "worker history reaches real overlay through normal refresh");
            var firstEpoch = window.SessionProbeCards.Current?.Identity?.Epoch;
            await Frame("01-running");
            var firstGeometry = window.SessionProbeOverlay!.ReadingState();
            await window.SessionProbeTranslateAsync("Pending fixture", "오른쪽으로 가세요.");
            await Wait(() => window.SessionProbeCards.Current?.Translation == "오른쪽으로 가세요.", "pending next caption admitted by main window");
            await Task.Delay(250);
            Require(Has("문 앞에서 기다려."), "pending next caption retains unread line in real time");
            await Frame("02-held");
            window.SessionProbeClick("pause");
            Require(Empty(), "pause button clears pending and rendered lines synchronously");
            await Wait(() => window.SessionProbeState is { State: "Paused", Busy: false }, "pause state confirmed through IPC");
            await Frame("03-paused");
            window.SessionProbeClick("resume");
            await Wait(() => window.SessionProbeState is { State: "Running", Busy: false }, "resume button reaches fresh epoch");
            Require(window.SessionProbeState.Session == firstSession && Empty(), "resume retains UUID without restoring old epoch captions");
            await window.SessionProbeTranslateAsync("Resumed fixture", "재개한 자막.");
            await Wait(() => Has("재개한 자막."), "new epoch text appears through event refresh");
            var appeared = Stopwatch.StartNew();
            Require(window.SessionProbeCards.Current?.Identity?.Epoch > firstEpoch, "resume history has strictly newer epoch");
            await Frame("04-resumed");
            var expired = false;
            while (appeared.Elapsed.TotalSeconds < 3.1)
            {
                await Task.Delay(25);
                var state = window.SessionProbeOverlay.ReadingState();
                Require(state.UpperBounds == firstGeometry.UpperBounds && state.LowerBounds == firstGeometry.LowerBounds,
                    "real dispatcher tick retains slot geometry");
                if (state.Upper.Length == 0)
                {
                    Require(appeared.Elapsed.TotalSeconds >= CaptionLines.HoldSeconds - .05,
                        "production timer does not expire line before reading protection");
                    expired = true; break;
                }
            }
            Require(expired && Empty(), "production reading timer drains once without manual advance");
            await Task.Delay(300);
            Require(Empty(), "periodic history refresh does not resurrect expired line");
            await Frame("05-drained");
            await window.SessionProbeTranslateAsync("Burst first", "첫 문장.");
            await Wait(() => Has("첫 문장."), "burst first sentence reaches real overlay");
            await window.SessionProbeTranslateAsync("Burst middle", "중간 문장.");
            await window.SessionProbeTranslateAsync("Burst last", "마지막 문장.");
            await Wait(() => window.SessionProbeCards.Current?.Translation == "마지막 문장.", "burst last result reaches UI while first is protected");
            Require(Has("첫 문장."), "rapid distinct sentences preserve first reading line");
            await Frame("burst-first");
            await Wait(() => window.SessionProbeOverlay?.ReadingState() is { Upper.Length: 0, Lower: "중간 문장." },
                "middle sentence stays in lower slot after first reading deadline");
            await Frame("burst-middle");
            await Wait(() => Has("마지막 문장."), "last sentence follows middle by real reading timer");
            await Frame("burst-last");
            await Wait(Empty, "burst finishes without replaying earlier snapshot records");
            await Frame("burst-drained");
            await window.SessionProbeTranslateAsync("Before stop", "종료 전 자막.");
            await Wait(() => Has("종료 전 자막."), "caption before stop visible");
            await window.SessionProbeTranslateAsync("Pending stop", "종료 대기 자막.");
            await Wait(() => window.SessionProbeCards.Current?.Translation == "종료 대기 자막.", "pending stop caption admitted before command");
            Require(Has("종료 전 자막."), "stop tests pending caption while original line is protected");
            window.SessionProbeClick("stop");
            Require(Empty(), "stop button immediately clears latest pending input");
            await Wait(() => window.SessionProbeState is { State: "Idle", Busy: false }, "stop completes owners and returns idle");
            window.SessionProbeClick("start");
            await Wait(() => window.SessionProbeState is { State: "Running", Busy: false }, "second session starts");
            Require(window.SessionProbeState.Session != firstSession && Empty(), "new UUID filters retained old-session history");
            await window.SessionProbeTranslateAsync("Second fixture", "새 세션 자막.");
            await Wait(() => Has("새 세션 자막."), "second session displays only fresh result");
            await Frame("06-restarted");
            await window.SessionProbeTranslateAsync("Pending death", "표시되면 안 되는 자막.");
            await Wait(() => window.SessionProbeCards.Current?.Translation == "표시되면 안 되는 자막.", "pending death caption admitted before process exit");
            Require(Has("새 세션 자막."), "worker death tests pending caption while original line is protected");
            var oldPid = window.SessionProbeState.Worker;
            window.SessionProbeKillWorker();
            await Wait(() => window.SessionProbeState is { Worker: null, Busy: false } && Empty(), "owned worker death clears visible and pending captions");
            await Frame("07-worker-death");
            window.SessionProbeClick("connect");
            await Wait(() => window.SessionProbeState is { State: "Idle", Busy: false, Worker: not null }, "reconnect button owns replacement worker");
            Require(window.SessionProbeState.Worker != oldPid && Empty(), "replacement process cannot inherit old deck");
            window.SessionProbeClick("start");
            await Wait(() => window.SessionProbeState is { State: "Running", Busy: false }, "replacement worker starts session");
            await window.SessionProbeTranslateAsync("Recovered fixture", "연결 복구 자막.");
            await Wait(() => Has("연결 복구 자막."), "fresh caption after worker recovery");
            await Frame("08-recovered");
            window.SessionProbeClick("disconnect");
            Require(Empty(), "disconnect button immediately clears overlay");
            await Wait(() => window.SessionProbeState is { Worker: null, Busy: false }, "disconnect disposes owned worker");
            await Task.Delay(2800);
            Require(Empty(), "no pending caption resurrects after disconnected reading deadline");
            await Frame("09-disconnected");
        }
        catch (Exception error) { exit = 1; checks.Add(new { error = error.ToString(), elapsed_s = clock.Elapsed.TotalSeconds }); }
        finally
        {
            if (window is not null)
            {
                window.Close();
                var cleanup = Stopwatch.StartNew();
                while (!window.SessionProbeState.Closed && cleanup.Elapsed.TotalSeconds < 10) await Task.Delay(25);
                if (!window.SessionProbeState.Closed) { exit = 1; checks.Add(new { error = "Fixture UI cleanup timed out" }); }
            }
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { passed = exit == 0,
                scope = "Real MainWindow buttons, Rust IPC/history and production dispatcher/overlay timers; explicit mock ASR/translation/capture readiness; saved renders, not continuous physical frames",
                worker_sha256 = workerHash, app_sha256 = appHash,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                started_at_utc = DateTimeOffset.UtcNow - clock.Elapsed,
                elapsed_s = clock.Elapsed.TotalSeconds, checks, frames }, new JsonSerializerOptions { WriteIndented = true }));
            desktop.Shutdown(exit);
        }
    }
}
