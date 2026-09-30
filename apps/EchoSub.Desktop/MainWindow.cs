using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EchoSub.Desktop;

public sealed class MainWindow : Window
{
    private readonly TextBlock status = new() { Text = "Worker 연결 준비 중" };
    private readonly TextBlock details = new() { Text = "실제 오디오 캡처·전사는 아직 없습니다.", TextWrapping = TextWrapping.Wrap };
    private readonly Button connectButton = new() { Content = "다시 연결" };
    private readonly Button pingButton = new() { Content = "Ping" };
    private readonly Button stateButton = new() { Content = "상태 조회" };
    private readonly Button stopButton = new() { Content = "Worker 종료" };
    private readonly Button overlayButton = new() { Content = "샘플 오버레이 표시" };
    private OverlayWindow? overlay;
    private WorkerClient? client;
    private bool closing;

    public MainWindow()
    {
        Title = "EchoSub · IPC scaffold";
        Width = 520;
        Height = 430;
        MinWidth = 420;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var resetOverlay = new Button { Content = "오버레이 위치 초기화" };
        var overlayWidth = new Slider { Minimum = 420, Maximum = 1100, Value = 760, Width = 240 };
        var cardOpacity = new Slider { Minimum = 0.25, Maximum = 1, Value = 0.75, Width = 240 };

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "EchoSub", FontSize = 22 },
                new TextBlock { Text = "MOCK · 시스템 오디오 캡처와 자막은 구현 전입니다." },
                status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { connectButton, pingButton, stateButton, stopButton }
                },
                new ScrollViewer { Height = 80, Content = details },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { overlayButton, resetOverlay }
                },
                new TextBlock { Text = "샘플 오버레이 폭 / 배경 불투명도" },
                overlayWidth,
                cardOpacity
            }
        };

        connectButton.Click += async (_, _) => await ConnectAsync();
        pingButton.Click += async (_, _) => await CallAsync("ping", new { nonce = "UI-핑" });
        stateButton.Click += async (_, _) => await CallAsync("get_state");
        stopButton.Click += async (_, _) => await DisconnectAsync();
        overlayButton.Click += (_, _) =>
        {
            if (overlay?.IsVisible == true)
            {
                overlay.Hide();
                overlayButton.Content = "샘플 오버레이 표시";
                return;
            }
            if (overlay is null)
            {
                overlay = new OverlayWindow();
                overlay.Closed += (_, _) => { overlay = null; overlayButton.Content = "샘플 오버레이 표시"; };
                overlay.ResetPlacement(this);
            }
            overlay.Width = overlayWidth.Value;
            overlay.SetCardOpacity(cardOpacity.Value);
            overlay.Show();
            overlayButton.Content = "샘플 오버레이 숨기기";
        };
        resetOverlay.Click += (_, _) => overlay?.ResetPlacement(this);
        overlayWidth.ValueChanged += (_, _) => { if (overlay is not null) overlay.Width = overlayWidth.Value; };
        cardOpacity.ValueChanged += (_, _) => overlay?.SetCardOpacity(cardOpacity.Value);
        Opened += async (_, _) =>
        {
            StartupDiagnostics.Write($"Main window opened; visible={IsVisible}; position={Position}; bounds={Bounds}; scale={RenderScaling}; native={WindowsOverlayPlatform.Inspect(this)}");
            await ConnectAsync();
        };
        Closing += async (_, args) =>
        {
            if (closing) return;
            args.Cancel = true;
            closing = true;
            overlay?.Close();
            await DisconnectAsync();
            Close();
        };
        UpdateButtons();
    }

    private async Task ConnectAsync()
    {
        if (client is not null) return;
        try
        {
            var path = Environment.GetEnvironmentVariable("ECHOSUB_WORKER_PATH") ??
                Path.Combine(AppContext.BaseDirectory,
                    OperatingSystem.IsWindows() ? "echosub-worker.exe" : "echosub-worker");
            client = WorkerClient.Start(path);
            client.Disconnected += OnDisconnected;
            var hello = await client.SendAsync("hello", new
            {
                client = "EchoSub.Desktop",
                protocol_major = 1
            });
            status.Text = "Worker 연결됨 · MOCK";
            StartupDiagnostics.Write("Worker connected");
            details.Text = hello.ToString();
        }
        catch (Exception error)
        {
            StartupDiagnostics.Write($"Worker connection failed: {error.Message}");
            await DisconnectAsync();
            status.Text = "Worker 연결 실패";
            details.Text = error.Message;
        }
        UpdateButtons();
    }

    private async Task CallAsync(string method, object? parameters = null)
    {
        if (client is null) return;
        try
        {
            var result = await client.SendAsync(method, parameters);
            details.Text = result.ToString();
        }
        catch (Exception error)
        {
            status.Text = "Worker 통신 오류";
            details.Text = error.Message;
        }
    }

    private async Task DisconnectAsync()
    {
        var oldClient = client;
        client = null;
        UpdateButtons();
        if (oldClient is not null)
        {
            oldClient.Disconnected -= OnDisconnected;
            await oldClient.DisposeAsync();
        }
        status.Text = "Worker 종료됨";
    }

    private void OnDisconnected()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            var disconnectedClient = client;
            if (disconnectedClient is null) return;
            client = null;
            disconnectedClient.Disconnected -= OnDisconnected;
            await disconnectedClient.DisposeAsync();
            status.Text = "Worker 연결 끊김 · 다시 연결 가능";
            UpdateButtons();
        });
    }

    private void UpdateButtons()
    {
        var running = client?.IsRunning == true;
        connectButton.IsEnabled = !running;
        pingButton.IsEnabled = running;
        stateButton.IsEnabled = running;
        stopButton.IsEnabled = running;
    }
}
