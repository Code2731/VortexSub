using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;

namespace EchoSub.Desktop;

// Opt-in validation with isolated settings, synthetic input and an owned loopback server.
internal static class UiRecoveryProbe
{
    internal static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string worker, string report)
    {
        var checks = new List<object>();
        var clock = Stopwatch.StartNew();
        MainWindow? window = null;
        var exit = 0;
        report = Path.GetFullPath(report);
        var output = Path.GetDirectoryName(report)!;
        var fixtures = Path.Combine(output, "fixtures-" + Guid.NewGuid().ToString("N"));
        var settings = Path.Combine(fixtures, "settings.json");
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed, elapsed_s = clock.Elapsed.TotalSeconds });
            if (!passed) throw new IOException(name);
        }
        async Task Wait(Func<bool> condition, string name)
        {
            var deadline = clock.Elapsed.TotalSeconds + 12;
            while (!condition() && clock.Elapsed.TotalSeconds < deadline) await Task.Delay(50);
            Check(condition(), name);
        }
        async Task Open(string path)
        {
            window = new MainWindow(Path.GetFullPath(worker), path, translationProbe: true);
            desktop.MainWindow = window;
            window.Show();
            await Wait(() => window.SessionProbeState is { State: "Idle", Busy: false, Worker: not null }, "isolated window connected");
        }
        async Task Close()
        {
            window!.Close();
            await Wait(() => window.SessionProbeState.Closed, "window and owned worker closed");
            window = null;
        }
        async Task Frame(string name)
        {
            var tabs = window!.CaptureTabs!;
            tabs.SelectedIndex = 1;
            await Task.Delay(100);
            ((ScrollViewer)((TabItem)tabs.SelectedItem!).Content!).Offset = new Vector(0, 245);
            await Task.Delay(150);
            var scale = window.RenderScaling;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
                (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
        try
        {
            Directory.CreateDirectory(fixtures);
            foreach (var endpoint in new[] { "http://127.0.0.1:1234/v1/", "http://[::1]:8080/v1", "http://127.0.0.1:80" })
                Check(MainWindow.RecoveryEndpointValid(endpoint), "valid endpoint " + endpoint);
            foreach (var endpoint in new[] { "http://localhost:1234/v1/", "https://127.0.0.1:1234/v1/", "http://127.0.0.1/v1/",
                "http://127.1:1234/v1/", "http://127.00.0.1:1234/v1/", "http://127.0.0.1:0/v1/", "http://192.168.0.1:1234/v1/", "http://127.0.0.1:1234/v2/", "http://user@127.0.0.1:1234/v1/" })
                Check(!MainWindow.RecoveryEndpointValid(endpoint), "invalid endpoint " + endpoint);
            Check(DesktopPreferences.Load(settings) == new DesktopPreferences(), "missing file uses defaults");
            new DesktopPreferences { Language = "unsupported", DeviceId = new string('x', 2049),
                OverlayWidth = 9000, CardOpacity = -1, PartialTranslation = true }.Save(settings);
            var normalized = DesktopPreferences.Load(settings);
            Check(normalized.Language == "en" && normalized.DeviceId is null && normalized.OverlayWidth == 1100 &&
                normalized.CardOpacity == 0.25 && normalized.Partial, "invalid values normalize to supported ranges");
            new DesktopPreferences().Save(settings);
            await Open(settings);
            window!.RecoverySetPreferences();
            window.RecoverySave();
            Check(window.RecoveryPreferencesStatus.Contains("저장했습니다"), "save button reports success");
            var saved = DesktopPreferences.Load(settings);
            Check(saved.Language == "ja" && saved.OverlayWidth == 900 && saved.CardOpacity == 0.5 &&
                saved.ShowSource && saved.Partial && saved.PartialTranslation && saved.CosmeticRevisions, "save button persists all selected values");
            await Close();
            var childReport = Path.Combine(fixtures, "restart.json");
            var executable = Environment.ProcessPath ?? throw new IOException("Application executable path unavailable");
            var childStart = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                childStart.ArgumentList.Add(typeof(MainWindow).Assembly.Location);
            foreach (var argument in new[] { "--ui-recovery-probe-worker", Path.GetFullPath(worker),
                "--ui-preferences-read-path", settings, "--ui-preferences-read-report", childReport }) childStart.ArgumentList.Add(argument);
            using (var child = Process.Start(childStart) ?? throw new IOException("Restart probe could not start"))
            {
                using var childDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await child.WaitForExitAsync(childDeadline.Token); }
                catch (OperationCanceledException)
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    throw;
                }
                Check(child.ExitCode == 0 && child.Id != Environment.ProcessId, "separate application process completes restore");
            }
            using (var childResult = JsonDocument.Parse(await File.ReadAllTextAsync(childReport)))
                Check(childResult.RootElement.GetProperty("preferences").Deserialize<DesktopPreferences>() == saved,
                    "saved settings restored after actual process restart");
            await Open(settings);
            Check(window!.RecoveryPreferences == saved, "new window restores saved values");
            window.RecoverySetPreferences(880);
            await Close();
            Check(DesktopPreferences.Load(settings).OverlayWidth == 880, "normal close saves changes without save button");
            Check(!Directory.EnumerateFiles(fixtures, "*.tmp").Any(), "atomic save leaves no temporary files");
            (saved with { DeviceId = "removed-device" }).Save(settings);
            await Open(settings);
            window!.RecoveryDevices(new MainWindow.EndpointChoice(null, "Default"));
            Check(window.RecoveryPreferences.DeviceId is null && window.RecoveryPreferencesStatus.Contains("기본 출력"), "removed device falls back with notice");
            await Close();
            (saved with { DeviceId = "available-device" }).Save(settings);
            await Open(settings);
            await Close();
            Check(DesktopPreferences.Load(settings).DeviceId == "available-device", "missing device enumeration preserves saved ID");
            await Open(settings);
            window!.RecoveryDevices(new MainWindow.EndpointChoice(null, "Default"), new MainWindow.EndpointChoice("available-device", "Fixture device"));
            Check(window.RecoveryPreferences.DeviceId == "available-device", "available device restored");
            await Close();
            await File.WriteAllTextAsync(settings, "{broken");
            await Open(settings);
            Check(window!.RecoveryPreferences.Language == "en" && !window.RecoveryPreferences.ShowSource &&
                window.RecoveryPreferencesStatus.Contains("읽지 못했습니다"), "corrupt file uses defaults and shows notice");
            await Close();
            var blocked = Path.Combine(fixtures, "blocked.json");
            Directory.CreateDirectory(blocked);
            await Open(blocked);
            window!.RecoverySave();
            Check(window.RecoveryPreferencesStatus.Contains("저장하지 못했습니다"), "save failure is visible and window survives");
            await Close();
            await File.WriteAllTextAsync(settings, new string('x', 16_385));
            await Open(settings);
            Check(window!.RecoveryPreferencesStatus.Contains("읽지 못했습니다"), "oversized file safely rejected");
            await Close();
            await Open(settings);
            var workerId = window!.SessionProbeState.Worker;
            await using var server = new CatalogServer();
            await window.RecoveryConfigure(server.Address);
            await Wait(() => window.RecoveryTranslationReady, "catalog config reaches Ready");
            Check(server.Requests > 0, "real worker requested loopback catalog");
            var requestsBeforeInvalidAddress = server.Requests;
            await window.RecoveryConfigure("http://localhost:1234/v1/");
            Check(window.RecoveryTranslationError == "Contract(InvalidEndpoint)", "invalid address rejected before request");
            await window.RecoveryPoll();
            Check(server.Requests == requestsBeforeInvalidAddress, "invalid address sends no HTTP request");
            Check(window.RecoveryTranslationStatus.Contains("숫자 주소") && window.RecoveryTranslationReady, "invalid address notice survives polling and keeps Ready");
            await Frame("01-invalid-address");
            var unused = new TcpListener(IPAddress.Loopback, 0);
            unused.Start();
            var closedPort = ((IPEndPoint)unused.LocalEndpoint).Port;
            unused.Stop();
            await window.RecoveryConfigure($"http://127.0.0.1:{closedPort}/v1/");
            await Wait(() => window.RecoveryTranslationError == "Connection" && window.RecoveryTranslationReady, "connection failure retains previous Ready");
            Check(window.RecoveryTranslationStatus.Contains("연결하지 못했습니다"), "connection failure gives actionable notice");
            await Frame("02-connection-failure");
            await window.RecoveryConfigure(server.Address);
            await Wait(() => window.RecoveryTranslationReady && window.RecoveryTranslationError == "오류가 없습니다.", "retry clears failure and recovers");
            server.Status = 401;
            await window.RecoveryConfigure(server.Address);
            await Wait(() => window.RecoveryTranslationError == "Status(401)" && window.RecoveryTranslationReady, "authentication failure keeps Ready");
            Check(window.RecoveryTranslationStatus.Contains("접근을 거부"), "authentication advice displayed");
            server.Status = 200;
            await window.RecoveryConfigure(server.Address, "qwen-greedy");
            await Wait(() => window.RecoveryTranslationError == "ModelProfileMismatch" && window.RecoveryTranslationReady, "profile mismatch retains Ready");
            await Frame("03-profile-mismatch");
            server.DelayMilliseconds = 10_000;
            await window.RecoveryConfigure(server.Address);
            await Wait(() => window.RecoveryTranslationError == "Deadline" && window.RecoveryTranslationReady, "catalog deadline keeps previous Ready");
            Check(window.RecoveryTranslationStatus.Contains("응답 시간이 초과"), "deadline advice displayed");
            server.DelayMilliseconds = 0;
            await window.RecoveryConfigure(server.Address);
            await Wait(() => window.RecoveryTranslationReady && window.RecoveryTranslationError == "오류가 없습니다.", "retry after deadline recovers");
            Check(window.SessionProbeState.Worker == workerId, "same worker survives all failures");
            await Frame("04-recovered");
            await Close();
        }
        catch (Exception error) { exit = 1; checks.Add(new { name = "unexpected error", passed = false, error = error.ToString() }); }
        finally
        {
            if (window is not null)
            {
                window.Close();
                for (var attempt = 0; attempt < 100 && !window.SessionProbeState.Closed; attempt++) await Task.Delay(100);
                if (!window.SessionProbeState.Closed) exit = 1;
            }
            try
            {
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
                {
                    passed = exit == 0, scope = "Real Avalonia controls and Rust IPC; isolated settings; synthetic loopback catalog; no real audio or models",
                    fixtures, checks, elapsed_s = clock.Elapsed.TotalSeconds
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { exit = 1; Console.Error.WriteLine(error); }
            desktop.Shutdown(exit);
        }
    }

    internal static async Task ReadPreferencesInNewProcessAsync(IClassicDesktopStyleApplicationLifetime desktop,
        string worker, string settings, string report)
    {
        MainWindow? window = null;
        DesktopPreferences? restored = null;
        string? errorText = null;
        var exit = 0;
        try
        {
            window = new MainWindow(Path.GetFullPath(worker), Path.GetFullPath(settings));
            desktop.MainWindow = window;
            window.Show();
            for (var attempt = 0; attempt < 80 && window.SessionProbeState.State != "Idle"; attempt++) await Task.Delay(100);
            if (window.SessionProbeState.State != "Idle") throw new IOException("Restart worker connection timed out");
            restored = window.RecoveryPreferences;
        }
        catch (Exception error) { exit = 1; errorText = error.ToString(); }
        finally
        {
            if (window is not null)
            {
                window.Close();
                for (var attempt = 0; attempt < 100 && !window.SessionProbeState.Closed; attempt++) await Task.Delay(100);
                if (!window.SessionProbeState.Closed) exit = 1;
            }
            try
            {
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
                    { preferences = restored, error = errorText, process_id = Environment.ProcessId, passed = exit == 0 }));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { exit = 1; Console.Error.WriteLine(error); }
            desktop.Shutdown(exit);
        }
    }

    private sealed class CatalogServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly List<Task> requests = [];
        private readonly Task loop;
        private int count;
        internal int Status { get; set; } = 200;
        internal int DelayMilliseconds { get; set; }
        internal int Requests => Volatile.Read(ref count);
        internal string Address { get; }
        internal CatalogServer()
        {
            listener.Start();
            Address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/";
            loop = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    requests.Add(RespondAsync(client, Status, DelayMilliseconds));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (cancellation.IsCancellationRequested) { }
        }
        private async Task RespondAsync(TcpClient client, int status, int delay)
        {
            using (client)
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token);
                    if (read == 0) return;
                    length += read;
                    if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n")) break;
                }
                Interlocked.Increment(ref count);
                await Task.Delay(delay, deadline.Token);
                var body = status == 200 ? "{\"data\":[{\"id\":\"fixture/model\"}]}" : "{}";
                var response = $"HTTP/1.1 {status} Fixture\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response), deadline.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
        }
        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel(); listener.Stop();
            await loop;
            await Task.WhenAll(requests);
            cancellation.Dispose();
        }
    }
}
