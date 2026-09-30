using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EchoSub.Desktop;

public sealed class MainWindow : Window
{
    private readonly bool live = Environment.GetEnvironmentVariable("ECHOSUB_LIVE_UI") == "1";
    private readonly TextBlock status = new() { Text = "Worker 연결 준비 중" };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button connectButton = new() { Content = "다시 연결" };
    private readonly Button pingButton = new() { Content = "Ping" };
    private readonly Button stateButton = new() { Content = "상태 조회" };
    private readonly Button stopButton = new() { Content = "Worker 종료" };
    private readonly Button startCaptureButton = new() { Content = "캡처 시작" };
    private readonly Button stopCaptureButton = new() { Content = "캡처 정지" };
    private readonly Button overlayButton = new();
    private readonly ComboBox language = new() { ItemsSource = new[] { "en", "ja", "ko" }, SelectedIndex = 0, Width = 80 };
    private readonly ComboBox endpoint = new() { Width = 450 };
    private readonly ListBox history = new() { Height = 190 };
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(0.5) };
    private CancellationTokenSource? refreshCancellation;
    private OverlayWindow? overlay;
    private WorkerClient? client;
    private Action? disconnectedHandler;
    private HistorySnapshot? snapshot;
    private string? latestSource;
    private (ulong Session, ulong Epoch, ulong Segment, ulong Revision)? displayedKey;
    private long sourceSince;
    private bool closing, closeReady, modelReady, captureStartable, captureNeedsStop;
    private int pendingActions;

    public MainWindow()
    {
        Title = live ? "EchoSub · 실제 원문 진단" : "EchoSub · MOCK";
        Width = live ? 820 : 560;
        Height = live ? 760 : 460;
        MinWidth = live ? 720 : 500;
        MinHeight = live ? 700 : 430;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        overlayButton.Content = OverlayLabel(false);
        endpoint.ItemsSource = new[] { new EndpointChoice(null, "기본 출력 장치 · 시작 시 선택") };
        endpoint.SelectedIndex = 0;
        var resetOverlay = new Button { Content = "오버레이 위치 초기화" };
        var overlayWidth = new Slider { Minimum = 420, Maximum = 1100, Value = 760, Width = 240 };
        var cardOpacity = new Slider { Minimum = 0.25, Maximum = 1, Value = 0.75, Width = 240 };
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "EchoSub", FontSize = 22 },
                    new TextBlock
                    {
                        Text = live ? "실제 원문 진단 · 번역/partial 없음 · 게임/자연 음성 품질 미검증" : "MOCK · 실제 캡처와 전사는 실행하지 않습니다.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    status,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { connectButton, pingButton, stateButton, stopButton } },
                    new StackPanel
                    {
                        IsVisible = live, Spacing = 8,
                        Children =
                        {
                            new TextBlock { Text = "출력 장치 / 원문 언어 · 실행 중 변경하려면 먼저 정지하세요." },
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { endpoint, language } },
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { startCaptureButton, stopCaptureButton } }
                        }
                    },
                    new ScrollViewer { Height = live ? 90 : 80, Content = details },
                    new TextBlock { IsVisible = live, Text = "최근 100개 구간 · 오디오 시간은 worker 시작 기준 초 · 텍스트 자동 저장 없음" },
                    history,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { overlayButton, resetOverlay } },
                    new TextBlock { Text = "오버레이 폭 / 배경 불투명도" },
                    overlayWidth, cardOpacity
                }
            }
        };
        history.IsVisible = live;
        history.ItemTemplate = new FuncDataTemplate<object>((item, _) => new TextBlock
        {
            Text = item?.ToString(), TextWrapping = TextWrapping.Wrap
        });
        connectButton.Click += async (_, _) => await ExecuteAsync(ConnectCoreAsync);
        pingButton.Click += async (_, _) => await ExecuteAsync(async () =>
        {
            if (client is not null) details.Text = (await client.SendAsync("ping", new { nonce = "UI-핑" })).ToString();
        });
        stateButton.Click += async (_, _) => await PollAsync();
        stopButton.Click += async (_, _) =>
        {
            ClearSource();
            await ExecuteAsync(DisconnectCoreAsync);
        };
        startCaptureButton.Click += async (_, _) => await ExecuteAsync(async () =>
        {
            if (client is null || !captureStartable || !modelReady) return;
            ClearSource();
            await client.SendAsync("start_capture", new { language = language.SelectedItem as string, device_id = (endpoint.SelectedItem as EndpointChoice)?.Id });
            captureStartable = false;
            captureNeedsStop = true;
            status.Text = "캡처 시작 요청 수락 · 상태 확인 중";
        });
        stopCaptureButton.Click += async (_, _) =>
        {
            ClearSource();
            await ExecuteAsync(async () =>
            {
                if (client is null) return;
                await client.SendAsync("stop_capture");
                captureStartable = captureNeedsStop = false;
                status.Text = "캡처 정지 요청 수락 · 소유 스레드 정리 확인 중";
            });
        };
        overlayButton.Click += (_, _) =>
        {
            if (overlay?.IsVisible == true) { overlay.Hide(); overlayButton.Content = OverlayLabel(false); return; }
            if (overlay is null)
            {
                overlay = new OverlayWindow(live);
                overlay.Closed += (_, _) => { overlay = null; overlayButton.Content = OverlayLabel(false); };
                overlay.ResetPlacement(this);
            }
            if (live) overlay.SetSource(latestSource);
            overlay.Width = overlayWidth.Value;
            overlay.SetCardOpacity(cardOpacity.Value);
            overlay.Show();
            overlayButton.Content = OverlayLabel(true);
        };
        resetOverlay.Click += (_, _) => overlay?.ResetPlacement(this);
        overlayWidth.ValueChanged += (_, _) => { if (overlay is not null) overlay.Width = overlayWidth.Value; };
        cardOpacity.ValueChanged += (_, _) => overlay?.SetCardOpacity(cardOpacity.Value);
        timer.Tick += async (_, _) =>
        {
            ExpireSource();
            if (pendingActions == 0 && !closing && client is not null) await PollAsync();
        };
        Opened += async (_, _) =>
        {
            StartupDiagnostics.Write($"Main window opened; live={live}; visible={IsVisible}; native={WindowsOverlayPlatform.Inspect(this)}");
            await ExecuteAsync(ConnectCoreAsync);
        };
        Closing += async (_, args) =>
        {
            if (closeReady) return;
            args.Cancel = true;
            if (closing) return;
            closing = true;
            timer.Stop();
            ClearSource();
            overlay?.Close();
            await ExecuteAsync(DisconnectCoreAsync);
            closeReady = true;
            Close();
        };
        UpdateButtons();
    }

    private string OverlayLabel(bool shown) => (live ? "원문 오버레이 " : "샘플 오버레이 ") + (shown ? "숨기기" : "표시");

    private async Task ExecuteAsync(Func<Task> action)
    {
        // User commands cancel background reads before waiting for their ownership gate.
        pendingActions++;
        refreshCancellation?.Cancel();
        UpdateButtons();
        await operationGate.WaitAsync();
        try { await action(); }
        catch (Exception error) { ReportError(error); }
        finally { pendingActions--; UpdateButtons(); operationGate.Release(); }
    }

    private async Task PollAsync()
    {
        // Skip ticks rather than accumulating refresh work behind a user command.
        if (pendingActions != 0 || closing || client is null) return;
        if (!await operationGate.WaitAsync(0)) return;
        using var cancellation = new CancellationTokenSource();
        refreshCancellation = cancellation;
        try { await RefreshCoreAsync(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { ReportError(error); }
        finally
        {
            refreshCancellation = null;
            UpdateButtons();
            operationGate.Release();
        }
    }

    private void ReportError(Exception error)
    {
        ClearSource();
        modelReady = captureStartable = false;
        status.Text = "Worker 통신/설정 오류 · 종료 후 다시 연결 가능";
        details.Text = error.Message;
        StartupDiagnostics.Write($"Worker operation failed: {error.GetType().Name}");
    }

    private async Task ConnectCoreAsync()
    {
        if (client is not null || closing) return;
        try
        {
            var path = Environment.GetEnvironmentVariable("ECHOSUB_WORKER_PATH") ??
                Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "echosub-worker.exe" : "echosub-worker");
            var arguments = live ? JsonSerializer.Deserialize<string[]>(Environment.GetEnvironmentVariable("ECHOSUB_WORKER_ARGUMENTS") ?? "[]") : [];
            if (live && (arguments is null || !arguments.Contains("--live-asr")))
                throw new IOException("실제 원문 진단은 scripts/run.ps1 -Live로 실행하세요.");
            if (live && Environment.GetEnvironmentVariable("ECHOSUB_ENDPOINTS") is { } choices)
            {
                endpoint.ItemsSource = (JsonSerializer.Deserialize<EndpointChoice[]>(choices) ?? throw new IOException("Invalid endpoint list"))
                    .Select(choice => choice.Id is null ? choice with { Name = "기본 출력 장치 · 시작 시 선택" } : choice).ToArray();
                endpoint.SelectedIndex = 0;
            }
            var connected = WorkerClient.Start(path, arguments: arguments);
            client = connected;
            disconnectedHandler = () => Dispatcher.UIThread.Post(async () => await ExecuteAsync(async () =>
            {
                if (!ReferenceEquals(client, connected)) return;
                await DisconnectCoreAsync();
                status.Text = "Worker 연결 끊김 · 다시 연결 가능";
            }));
            connected.Disconnected += disconnectedHandler;
            var hello = await connected.SendAsync("hello", new { client = "EchoSub.Desktop", protocol_major = 1 });
            if (live && !hello.GetProperty("capabilities").GetProperty("live_asr").GetBoolean())
                throw new IOException("연결한 worker는 실제 원문 진단을 지원하지 않습니다.");
            StartupDiagnostics.Write($"Worker connected; live={live}");
            snapshot = null;
            await RefreshCoreAsync();
            if (live) timer.Start();
        }
        catch { await DisconnectCoreAsync(); throw; }
    }

    private Task RefreshCoreAsync() => RefreshCoreAsync(CancellationToken.None);

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (client is null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var state = await client.SendAsync("get_state", cancellationToken: deadline.Token);
        deadline.Token.ThrowIfCancellationRequested();
        if (!live) { status.Text = "Worker 연결됨 · MOCK"; details.Text = state.ToString(); return; }
        var refreshedSnapshot = snapshot;
        if (snapshot?.Version != state.GetProperty("history_version").GetUInt64() || client.Events.SnapshotRequired)
        {
            refreshedSnapshot = await client.ReadHistoryAsync(deadline.Token);
            // A fault/epoch change may occur while reading multiple history pages.
            state = await client.SendAsync("get_state", cancellationToken: deadline.Token);
        }
        // An interrupted background read must not repaint source text after Stop was clicked.
        deadline.Token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(snapshot, refreshedSnapshot))
        {
            snapshot = refreshedSnapshot;
            history.ItemsSource = snapshot?.Records.TakeLast(100).Reverse().Select(record =>
                $"[{record.Epoch}/{record.SegmentId} · {record.AudioStartSeconds:F3}~{record.AudioEndSeconds:F3}초 · {record.SourceState}] {record.SourceReason}\n{record.Source}").ToArray();
        }
        var capture = state.GetProperty("diagnostic_capture");
        var captureState = capture.GetProperty("state").GetString();
        modelReady = state.GetProperty("model").GetProperty("state").GetString() == "Ready";
        var joined = !capture.GetProperty("awaiting_capture_join").GetBoolean();
        var vad = state.GetProperty("diagnostic_live_vad");
        var vadJoined = !vad.TryGetProperty("awaiting_join", out var awaitingVad) || !awaitingVad.GetBoolean();
        captureStartable = (captureState is "Idle" or "Stopped" or "Failed") && joined && vadJoined;
        captureNeedsStop = captureState is "Opening" or "Running";
        status.Text = $"모델 {state.GetProperty("model").GetProperty("state").GetString()} · 캡처 {captureState}";
        var phase = capture.GetProperty("failure_native_phase").GetString() ??
            (capture.GetProperty("stats").TryGetProperty("native_phase", out var nativePhase) ? nativePhase.GetString() : "준비 중");
        var opening = capture.GetProperty("opening_elapsed_s");
        var openingText = opening.ValueKind == JsonValueKind.Number ? $" · 시작 대기 {opening.GetDouble():F3}초" : "";
        details.Text = $"{phase}{openingText} · 수신 {capture.GetProperty("accepted_audio_s").GetDouble():F3}초\n" +
            (!joined || !vadJoined ? "소유 스레드가 동작/정리 중입니다. 정리 완료 전 새 캡처를 시작할 수 없습니다.\n" : "") +
            (captureState == "Failed" ? $"실패: {capture.GetProperty("error").GetString()} · Worker 종료 후 다시 연결할 수 있습니다." : "정지는 미확정 발화와 대기 작업을 폐기합니다.");
        if (state.GetProperty("model").GetProperty("state").GetString() == "Failed")
            details.Text += "\n모델 읽기 실패: 모델/DLL 경로·해시와 native CPU 빌드를 확인하세요.";
        while (client.Events.TryRead(out _)) { }
        var epoch = state.GetProperty("diagnostic_asr").GetProperty("epoch").GetUInt64();
        var final = captureState == "Running" ? snapshot?.Records.LastOrDefault(record =>
            record.SessionId == capture.GetProperty("session_id").GetUInt64() && record.Epoch == epoch &&
            record.SourceState == "Final" && record.AppliedSourceRevision == record.SourceRevision) : null;
        if (final is not null)
        {
            var key = (final.SessionId, final.Epoch, final.SegmentId, final.SourceRevision);
            if (displayedKey != key) { displayedKey = key; sourceSince = Stopwatch.GetTimestamp(); }
        }
        latestSource = final is not null && Stopwatch.GetElapsedTime(sourceSince).TotalSeconds < 5 ? final.Source : null;
        overlay?.SetSource(latestSource);
    }

    private void ClearSource() { latestSource = null; if (live) overlay?.SetSource(null); }

    private void ExpireSource()
    {
        if (latestSource is not null && Stopwatch.GetElapsedTime(sourceSince).TotalSeconds >= 5)
            ClearSource();
    }

    private async Task DisconnectCoreAsync()
    {
        timer.Stop();
        var oldClient = client;
        client = null;
        ClearSource();
        snapshot = null;
        displayedKey = null;
        history.ItemsSource = null;
        modelReady = captureStartable = captureNeedsStop = false;
        if (oldClient is not null)
        {
            if (disconnectedHandler is not null) oldClient.Disconnected -= disconnectedHandler;
            disconnectedHandler = null;
            status.Text = "Worker 종료 중 · 필요 시 소유 프로세스 정리";
            await oldClient.DisposeAsync();
            details.Text = oldClient.ForcedTerminationUsed ? "종료 대기 5초를 넘어 소유 worker 프로세스를 종료했습니다." : "소유 worker가 종료됐습니다.";
            StartupDiagnostics.Write($"Worker disposed; forced={oldClient.ForcedTerminationUsed}");
        }
        status.Text = "Worker 종료됨 · 다시 연결 가능";
    }

    private void UpdateButtons()
    {
        var connected = client is not null;
        var available = pendingActions == 0 && !closing;
        connectButton.IsEnabled = available && !connected;
        pingButton.IsEnabled = stateButton.IsEnabled = stopButton.IsEnabled = available && connected;
        startCaptureButton.IsEnabled = available && connected && modelReady && captureStartable;
        stopCaptureButton.IsEnabled = available && connected && captureNeedsStop;
        language.IsEnabled = endpoint.IsEnabled = available && captureStartable;
    }

    public sealed record EndpointChoice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }
}
