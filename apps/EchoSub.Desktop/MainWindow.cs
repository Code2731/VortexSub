using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;

namespace EchoSub.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly bool live = Environment.GetEnvironmentVariable("ECHOSUB_LIVE_UI") == "1";
    private readonly string? sessionProbeWorker;
    private readonly bool translationProbe;
    private readonly Slider overlayWidth = new() { Minimum = 420, Maximum = 1100, Value = 760, Width = 240 };
    private readonly Slider cardOpacity = new() { Minimum = 0.25, Maximum = 1, Value = 0.75, Width = 240 };
    private readonly TextBlock status = new() { Text = "Worker 연결 준비 중" };
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button connectButton = new() { Content = "다시 연결" };
    private readonly Button pingButton = new() { Content = "Ping" };
    private readonly Button stateButton = new() { Content = "상태 조회" };
    private readonly Button stopButton = new() { Content = "Worker 종료" };
    private readonly Button startCaptureButton = new() { Content = "세션 시작" };
    private readonly Button stopCaptureButton = new() { Content = "세션 종료" };
    private readonly Button pauseButton = new() { Content = "일시정지" };
    private readonly Button resumeButton = new() { Content = "재개" };
    private readonly Button overlayButton = new();
    private readonly CheckBox overlaySourceEnabled = new() { Content = "오버레이에 원문 표시", IsChecked = false };
    private readonly Button exportTxtButton = new() { Content = "TXT 저장" };
    private readonly Button exportSrtButton = new() { Content = "원문 SRT 저장" };
    private readonly Button clearHistoryButton = new() { Content = "선택 세션 기록 삭제" };
    private readonly ComboBox exportSession = new() { Width = 420 };
    private readonly TextBlock exportResult = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox language = new() { ItemsSource = new[] { "en", "ja", "ko" }, SelectedIndex = 0, Width = 80 };
    private readonly CheckBox partialEnabled = new() { Content = "부분 전사 켜기 · 실험 기능 / 기본 끔", IsChecked = false };
    private readonly CheckBox partialTranslationEnabled = new() { Content = "안정된 부분 먼저 번역 · 임시 결과 / 기본 끔", IsChecked = false };
    private readonly CheckBox captionTimingEnabled = new() { Content = "자막 지연 기록 · 기본 끔", IsChecked = false };
    private readonly CheckBox cosmeticRevisions = new() { Content = "마침표 갱신 묶기 · 실험 / 기본 끔", IsChecked = false };
    private readonly CheckBox isolatedTranslationContext = new() { Content = "이전 문맥 분리 번역 · 실험 기능 / 기본 끔", IsChecked = false };
    private sealed record TranslationProfileChoice(string Id, string Label) { public override string ToString() => Label; }
    private static readonly TranslationProfileChoice[] profileChoices = [
        new("standard", "기존 자막 번역 입력"), new("qwen-greedy", "Qwen · 고정 생성 설정 / 실험"),
        new("hymt2-greedy", "Hy-MT2 · 전용 번역 입력 / 실험")];
    private readonly ComboBox translationProfile = new() { ItemsSource = profileChoices, SelectedIndex = 0 };
    private readonly TextBlock translationProfileStatus = new() { Text = "입력 방식 변경은 세션 종료 후 적용하세요. 모델 가중치 변경은 런처를 다시 실행해야 합니다.", TextWrapping = TextWrapping.Wrap };
    private bool profilesSupported, profileInitialized, profileApplyPending;
    private string appliedProfile = "standard";
    private string SelectedProfile => (translationProfile.SelectedItem as TranslationProfileChoice)?.Id ?? "standard";
    private readonly TextBlock isolatedContextStatus = new() { Text = "세션 종료 후 변경하세요. 일부 문맥 혼입은 줄지만 의미 오류가 남아 있습니다.", TextWrapping = TextWrapping.Wrap };
    private bool isolatedContextSupported, isolatedContextInitialized, updatingIsolatedContext;
    private bool appliedIsolatedContext, translationConfigured;
    private readonly TextBlock captionTimingStatus = new() { Text = "원문·번역 내용은 저장하지 않습니다. 실행 중에도 켜고 끌 수 있습니다.", TextWrapping = TextWrapping.Wrap };
    private bool updatingCaptionTiming;
    private readonly DispatcherTimer diagnosticsTimer = new() { Interval = TimeSpan.FromSeconds(0.5) };
    private bool partialTranslationSupported;
    private readonly ComboBox endpoint = new() { Width = 450 };
    private readonly ListBox history = new() { Height = 190 };
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(0.5) };
    private readonly DispatcherTimer captionTimer = new() { Interval = TimeSpan.FromSeconds(0.1) };
    private CancellationTokenSource? refreshCancellation;
    private CancellationTokenSource? eventRefreshCancellation;
    private Task? eventRefreshTask;
    private OverlayWindow? overlay;
    private WorkerClient? client;
    private Action? disconnectedHandler;
    private HistorySnapshot? snapshot;
    private CaptionCards latestCards = new(null, null);
    private CaptionDeck captions = new() { LineReadingManaged = true };
    private readonly Stopwatch captionClock = Stopwatch.StartNew();
    private readonly TextBox translationEndpoint = new() { Text = "http://127.0.0.1:1234/v1/", Width = 350 };
    private readonly ComboBox translationModel = new() { Width = 350 };
    private readonly Button catalogButton = new() { Content = "서버 연결 / 모델 조회" };
    private readonly Button applyTranslationButton = new() { Content = "선택 모델 적용" };
    private readonly Button disableTranslationButton = new() { Content = "번역 끄기" };
    private readonly TextBlock translationStatus = new() { Text = "번역 끔 · 로컬 서버를 먼저 실행하세요.", TextWrapping = TextWrapping.Wrap };
    private bool translationSupported, translationBusy;
    private string[] translationModels = [];
    private bool closing, closeReady, modelReady, captureStartable, captureNeedsStop;
    private int pendingActions;
    private string? sessionId;
    private string? sessionState;
    private bool resumeReady;
    private bool exportReady, exportSupported, selectingExport;
    private bool clearSupported, historyReady;
    private bool partialSupported;

    public MainWindow() : this(null) { }

    internal MainWindow(string? sessionProbeWorker, string? isolatedPreferencesPath = null, bool translationProbe = false)
    {
        this.sessionProbeWorker = sessionProbeWorker;
        this.isolatedPreferencesPath = isolatedPreferencesPath;
        this.translationProbe = translationProbe;
        if ((isolatedPreferencesPath is not null || translationProbe) && sessionProbeWorker is null)
            throw new ArgumentException("Explicit mock worker required for isolated UI probe");
        if (sessionProbeWorker is not null) live = true;
        Title = sessionProbeWorker is not null ? "EchoSub · 세션 연결 검사 / 모의 입력" : live ? "EchoSub · 실제 원문 진단" : "EchoSub · MOCK";
        Width = live ? 820 : 560;
        Height = live ? 760 : 460;
        MinWidth = live ? 720 : 500;
        MinHeight = live ? 700 : 430;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        overlayButton.Content = OverlayLabel(false);
        endpoint.ItemsSource = new[] { new EndpointChoice(null, "기본 출력 장치 · 시작 시 선택") };
        endpoint.SelectedIndex = 0;
        var resetOverlay = new Button { Content = "오버레이 위치 초기화" };
        RestorePreferences(overlayWidth, cardOpacity);
        Content = BuildMainLayout(resetOverlay, overlayWidth, cardOpacity);
        savePreferencesButton.Click += (_, _) => SavePreferences(overlayWidth, cardOpacity);
        history.IsVisible = live;
        history.ItemTemplate = new FuncDataTemplate<object>((item, _) => new TextBlock
        {
            Text = item?.ToString(), TextWrapping = TextWrapping.Wrap
        });
        catalogButton.Click += async (_, _) => await ConfigureTranslationAsync(false);
        applyTranslationButton.Click += async (_, _) => await ConfigureTranslationAsync(true);
        disableTranslationButton.Click += async (_, _) => await ExecuteAsync(async () =>
        {
            if (client is null) return;
            try
            {
                await client.SendAsync("disable_translation", new { });
                translationCommandFailure = null;
                await RefreshCoreAsync();
            }
            catch (Exception error) { ReportTranslationFailure(error); }
        });
        translationModel.SelectionChanged += (_, _) => UpdateButtons();
        var launchProfile = Environment.GetEnvironmentVariable("ECHOSUB_TRANSLATION_INPUT_PROFILE");
        if (profileChoices.FirstOrDefault(p => p.Id == launchProfile) is { } choice)
        { translationProfile.SelectedItem = choice; profileInitialized = true; }
        translationProfile.SelectionChanged += (_, _) =>
        {
            if (SelectedProfile != "standard")
            {
                updatingIsolatedContext = true;
                isolatedTranslationContext.IsChecked = false;
                updatingIsolatedContext = false;
            }
            UpdateButtons();
        };
        isolatedTranslationContext.IsCheckedChanged += async (_, _) =>
        {
            if (updatingIsolatedContext || closing) return;
            isolatedContextStatus.Text = "서버 연결 / 모델 조회 또는 선택 모델 적용 시 반영됩니다.";
            UpdateButtons();
            if (translationConfigured && translationModel.SelectedItem is string)
                await ConfigureTranslationAsync(true);
        };
        exportTxtButton.Click += async (_, _) => await ExportAsync("txt");
        exportSrtButton.Click += async (_, _) => await ExportAsync("srt");
        clearHistoryButton.Click += async (_, _) => await ClearHistoryAsync();
        exportSession.SelectionChanged += (_, _) => UpdateButtons();
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
            ClearSource("SessionStartRequested");
            var config = new Dictionary<string, object?> { ["source_language"] = language.SelectedItem as string, ["device_id"] = (endpoint.SelectedItem as EndpointChoice)?.Id };
            if (partialSupported) config["partial_enabled"] = partialEnabled.IsChecked == true;
            if (partialTranslationSupported) config["partial_translation_enabled"] = partialTranslationEnabled.IsChecked == true && partialEnabled.IsChecked == true;
            var accepted = await client.SendAsync("start_session", new
            {
                history_policy = "retain",
                config
            });
            sessionId = accepted.GetProperty("session_id").GetString();
            sessionState = "Preparing";
            captureStartable = false;
            captureNeedsStop = true;
            status.Text = "세션 시작 요청 수락 · 준비 상태 확인 중";
        });
        stopCaptureButton.Click += async (_, _) =>
        {
            StartupDiagnostics.Write("User requested stop_session");
            ClearSource("SessionStopRequested");
            await ExecuteAsync(async () =>
            {
                if (client is null) return;
                await client.SendAsync("stop_session", new { session_id = sessionId });
                sessionState = "Stopping";
                captureStartable = captureNeedsStop = false;
                resumeReady = false;
                status.Text = "세션 종료 요청 수락 · 소유 스레드/native 반환 확인 중";
            });
        };
        pauseButton.Click += async (_, _) =>
        {
            StartupDiagnostics.Write("User requested pause_session");
            ClearSource("SessionPauseRequested");
            await ExecuteAsync(async () =>
            {
                if (client is null || sessionId is null) return;
                await client.SendAsync("pause_session", new { session_id = sessionId });
                sessionState = "Paused";
                resumeReady = false;
                status.Text = "일시정지 수락 · 캡처/VAD 정리 확인 중";
            });
        };
        resumeButton.Click += async (_, _) => await ExecuteAsync(async () =>
        {
            if (client is null || !resumeReady) return;
            await client.SendAsync("resume_session", new { session_id = sessionId });
            sessionState = "Preparing";
            resumeReady = false;
            status.Text = "재개 요청 수락 · 준비 상태 확인 중";
        });
        overlayButton.Click += (_, _) =>
        {
            if (overlay?.IsVisible == true) { overlay.Hide(); overlayButton.Content = OverlayLabel(false); return; }
            var created = overlay is null;
            if (overlay is null)
            {
                overlay = new OverlayWindow(live);
                overlay.Closed += (_, _) => { overlay = null; overlayButton.Content = OverlayLabel(false); };
                overlay.ResetPlacement(this);
            }
            overlay.SetSourceVisible(overlaySourceEnabled.IsChecked == true);
            if (live) overlay.SetCards(created && latestCards.OrderedDelivery && latestCards.Current is { } caption
                ? latestCards with { Delivered = [caption], ResetReading = true } : latestCards);
            overlay.Width = overlayWidth.Value;
            overlay.SetCardOpacity(cardOpacity.Value);
            overlay.Show();
            overlayButton.Content = OverlayLabel(true);
        };
        resetOverlay.Click += (_, _) => overlay?.ResetPlacement(this);
        overlaySourceEnabled.IsCheckedChanged += (_, _) =>
        {
            captions.ShowSource = overlaySourceEnabled.IsChecked == true;
            overlay?.SetSourceVisible(captions.ShowSource);
        };
        captionTimingEnabled.IsCheckedChanged += async (_, _) =>
        {
            if (!updatingCaptionTiming && !closing) await ConfigureCaptionTimingAsync(captionTimingEnabled.IsChecked == true);
        };
        partialTranslationEnabled.IsCheckedChanged += (_, _) =>
        {
            if (partialTranslationEnabled.IsChecked == true) partialEnabled.IsChecked = true;
        };
        partialEnabled.IsCheckedChanged += (_, _) =>
        {
            if (partialEnabled.IsChecked != true) partialTranslationEnabled.IsChecked = false;
        };
        overlayWidth.ValueChanged += (_, _) => { if (overlay is not null) overlay.Width = overlayWidth.Value; };
        cardOpacity.ValueChanged += (_, _) => overlay?.SetCardOpacity(cardOpacity.Value);
        captions.Applied += RecordCaptionApplied;
        cosmeticRevisions.IsCheckedChanged += (_, _) =>
        {
            ClearSource();
            captions.StabilizeCosmeticRevisions = cosmeticRevisions.IsChecked == true;
        };
        captions.TargetObserved += RecordTargetObserved;
        captionTimer.Tick += (_, _) => ExpireSource();
        diagnosticsTimer.Tick += (_, _) =>
        {
            if (!updatingCaptionTiming && captionTimingEnabled.IsChecked == true && !CaptionDiagnostics.Enabled)
            {
                updatingCaptionTiming = true;
                captionTimingEnabled.IsChecked = false;
                captionTimingStatus.Text = "자막 지연 기록 중단: " + CaptionDiagnostics.LastError;
                updatingCaptionTiming = false;
            }
        };
        diagnosticsTimer.Start();
        timer.Tick += async (_, _) =>
        {
            if (pendingActions == 0 && !closing && client is not null) await PollAsync();
        };
        Opened += async (_, _) =>
        {
            StartupDiagnostics.Write($"Main window opened; live={live}; visible={IsVisible}; native={WindowsOverlayPlatform.Inspect(this)}");
            var requestedLog = Environment.GetEnvironmentVariable("ECHOSUB_CAPTION_TIMING_LOG");
            if (!string.IsNullOrWhiteSpace(requestedLog)) await ConfigureCaptionTimingAsync(true, requestedLog);
            await ExecuteAsync(ConnectCoreAsync);
        };
        Closing += async (_, args) =>
        {
            if (closeReady) return;
            args.Cancel = true;
            if (closing) return;
            SavePreferences(overlayWidth, cardOpacity);
            closing = true;
            captionTimingEnabled.IsEnabled = false;
            diagnosticsTimer.Stop();
            timer.Stop();
            captionTimer.Stop();
            ClearSource();
            overlay?.Close();
            await ExecuteAsync(DisconnectCoreAsync);
            await CaptionDiagnostics.DrainAsync();
            closeReady = true;
            Close();
        };
        UpdateButtons();
    }

    private async Task ConfigureCaptionTimingAsync(bool enabled, string? requestedPath = null)
    {
        updatingCaptionTiming = true;
        captionTimingEnabled.IsEnabled = false;
        try
        {
            var path = await CaptionDiagnostics.SetEnabledAsync(enabled, requestedPath);
            captionTimingEnabled.IsChecked = enabled;
            captionTimingStatus.Text = enabled ? $"기록 중: {path}" :
                "기록 꺼짐 · 생성된 로그 파일은 유지됩니다.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            captionTimingEnabled.IsChecked = false;
            captionTimingStatus.Text = "자막 지연 기록 실패: " + error.Message;
        }
        finally
        {
            updatingCaptionTiming = false;
            captionTimingEnabled.IsEnabled = !closing;
        }
    }

    private string OverlayLabel(bool shown) => (live ? "자막 오버레이 " : "샘플 오버레이 ") + (shown ? "숨기기" : "표시");

    private async Task ExportAsync(string format)
    {
        if (!exportReady || selectingExport || client is null || exportSession.SelectedItem is not ExportChoice choice) return;
        selectingExport = true;
        UpdateButtons();
        try
        {
            using var selected = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "세션 기록 저장", SuggestedFileName = $"EchoSub-{choice.Id}.{format}",
                DefaultExtension = format, ShowOverwritePrompt = true,
                FileTypeChoices = new[] { new FilePickerFileType(format.ToUpperInvariant()) { Patterns = new[] { $"*.{format}" } } }
            });
            if (selected is null) return;
            var path = selected.TryGetLocalPath() ?? throw new IOException("로컬 파일 경로를 선택하세요.");
            await ExecuteAsync(async () =>
            {
                if (client is null) throw new IOException("Worker 연결이 끊겼습니다.");
                try
                {
                    var result = await client.SendAsync("export_history", new { session_id = choice.Id, format, path, overwrite = true });
                    exportResult.Text = $"저장 완료 · {result.GetProperty("cue_count").GetInt32()}개 원문 · {path}";
                }
                catch (Exception error) { exportResult.Text = $"저장 실패: {error.Message}"; throw; }
            });
        }
        catch (Exception error) { exportResult.Text = $"저장 실패: {error.Message}"; ReportError(error); }
        finally { selectingExport = false; UpdateButtons(); }
    }

    private async Task ClearHistoryAsync()
    {
        if (!historyReady || !clearSupported || selectingExport || client is null || exportSession.SelectedItem is not ExportChoice choice) return;
        var selectedClient = client;
        var count = snapshot?.Records.Count(r => r.ProductSessionId == choice.Id) ?? 0;
        selectingExport = true;
        UpdateButtons();
        try
        {
            var confirm = new Window
            {
                Title = "세션 기록 삭제", Width = 510, SizeToContent = SizeToContent.Height,
                CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var cancel = new Button { Content = "취소" };
            var remove = new Button { Content = "기록 삭제" };
            cancel.Click += (_, _) => confirm.Close(false);
            remove.Click += (_, _) => confirm.Close(true);
            confirm.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children =
            {
                new TextBlock { Text = $"선택 세션의 메모리 기록 {count}개를 삭제합니다.\n{choice}\n복구할 수 없습니다. 필요한 기록은 먼저 저장하세요.\n이미 저장한 TXT/SRT 파일은 유지됩니다.", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, remove } }
            } };
            if (!await confirm.ShowDialog<bool>(this)) return;
            await ExecuteAsync(async () =>
            {
                if (!ReferenceEquals(client, selectedClient)) throw new IOException("Worker 연결이 변경되었습니다. 세션을 다시 선택하세요.");
                try
                {
                    var result = await selectedClient.SendAsync("clear_history", new { session_id = choice.Id });
                    ClearSource();
                    exportResult.Text = $"기록 삭제 완료 · {result.GetProperty("removed_count").GetInt32()}개 · {choice.Id}";
                }
                catch (Exception error) { exportResult.Text = $"기록 삭제 실패: {error.Message}"; throw; }
                await RefreshCoreAsync();
            });
        }
        catch (Exception error) { ReportError(error); }
        finally { selectingExport = false; UpdateButtons(); }
    }

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

    private async Task RefreshOnEventsAsync(WorkerClient connected, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && ReferenceEquals(client, connected) && !closing)
            {
                await connected.Events.WaitForChangeAsync(cancellationToken);
                // Collapse source/translation/history events into a single snapshot refresh.
                await Task.Delay(TimeSpan.FromSeconds(0.03), cancellationToken);
                if (!ReferenceEquals(client, connected) || closing) return;
                await PollAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
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
            var path = sessionProbeWorker ?? Environment.GetEnvironmentVariable("ECHOSUB_WORKER_PATH") ??
                Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "echosub-worker.exe" : "echosub-worker");
            var arguments = sessionProbeWorker is not null ? (translationProbe
                ? ["--mock-pipeline", "--mock-session-control", "--diagnostic-translation"]
                : new[] { "--mock-pipeline", "--mock-session-control" }) :
                live ? JsonSerializer.Deserialize<string[]>(Environment.GetEnvironmentVariable("ECHOSUB_WORKER_ARGUMENTS") ?? "[]") : [];
            if (live && sessionProbeWorker is null && (arguments is null || !arguments.Contains("--live-asr")))
                throw new IOException("실제 원문 진단은 scripts/run.ps1 -Live로 실행하세요.");
            if (live && sessionProbeWorker is null && Environment.GetEnvironmentVariable("ECHOSUB_ENDPOINTS") is { } choices)
            {
                RestoreDeviceChoices(JsonSerializer.Deserialize<EndpointChoice[]>(choices) ?? throw new IOException("Invalid endpoint list"));
            }
            var connected = WorkerClient.Start(path, arguments: arguments);
            connected.EventReceived += (name, payload) => CaptionDiagnostics.Received(connected.ProcessId, name, payload);
            client = connected;
            disconnectedHandler = () => Dispatcher.UIThread.Post(async () => await ExecuteAsync(async () =>
            {
                if (!ReferenceEquals(client, connected)) return;
                await DisconnectCoreAsync();
                status.Text = "Worker 연결 끊김 · 다시 연결 가능";
            }));
            connected.Disconnected += disconnectedHandler;
            var hello = await connected.SendAsync("hello", new { client = "EchoSub.Desktop", protocol_major = 1 });
            if (live && sessionProbeWorker is null && !hello.GetProperty("capabilities").GetProperty("live_asr").GetBoolean())
                throw new IOException("연결한 worker는 실제 원문 진단을 지원하지 않습니다.");
            if (live && !hello.GetProperty("capabilities").TryGetProperty("session_control", out var sessionCapability))
                throw new IOException("session control worker를 다시 빌드하세요: scripts/run.ps1 -Live");
            if (live && !hello.GetProperty("capabilities").GetProperty("session_control").GetBoolean())
                throw new IOException("session control 모드로 실행하세요: scripts/run.ps1 -Live");
            if (live && (!hello.GetProperty("capabilities").TryGetProperty("session_history_uuid", out var historyCapability) || !historyCapability.GetBoolean()))
                throw new IOException("UUID history worker를 다시 빌드하세요: scripts/run.ps1 -Live");
            exportSupported = hello.GetProperty("capabilities").TryGetProperty("history_export", out var exportCapability) && exportCapability.GetBoolean();
            clearSupported = hello.GetProperty("capabilities").TryGetProperty("history_clear", out var clearCapability) && clearCapability.GetBoolean();
            partialSupported = hello.GetProperty("capabilities").TryGetProperty("source_partial", out var partialCapability) && partialCapability.GetBoolean();
            partialTranslationSupported = hello.GetProperty("capabilities").TryGetProperty("partial_translation", out var previewCapability) && previewCapability.GetBoolean();
            translationSupported = hello.GetProperty("capabilities").TryGetProperty("translation", out var translationCapability) && translationCapability.GetBoolean();
            isolatedContextSupported = hello.GetProperty("capabilities").TryGetProperty("isolated_translation_context", out var isolatedCapability) && isolatedCapability.GetBoolean();
            profilesSupported = hello.GetProperty("capabilities").TryGetProperty("translation_input_profiles", out var profileCapability) && profileCapability.GetBoolean();
            StartupDiagnostics.Write($"Worker connected; live={live}");
            snapshot = null;
            await RefreshCoreAsync();
            if (live)
            {
                timer.Start();
                captionTimer.Start();
                eventRefreshCancellation = new CancellationTokenSource();
                eventRefreshTask = RefreshOnEventsAsync(connected, eventRefreshCancellation.Token);
            }
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
        var captionRecords = new List<HistoryRecord>();
        while (client.Events.TryReadCaptionRecord(out var captionRecord))
            if (captionRecord is not null) captionRecords.Add(captionRecord);
        var refreshedSnapshot = snapshot;
        if (snapshot?.Version != state.GetProperty("history_version").GetUInt64() || client.Events.SnapshotRequired)
        {
            refreshedSnapshot = await client.ReadHistoryAsync(deadline.Token);
            // A fault/epoch change may occur while reading multiple history pages.
            state = await client.SendAsync("get_state", cancellationToken: deadline.Token);
        }
        // An interrupted background read must not repaint source text after Stop was clicked.
        deadline.Token.ThrowIfCancellationRequested();
        var historyChanged = !ReferenceEquals(snapshot, refreshedSnapshot);
        if (historyChanged)
        {
            snapshot = refreshedSnapshot;
            history.ItemsSource = snapshot?.Records.TakeLast(100).Reverse().Select(CaptionPresentation.HistoryText).ToArray();
            var selectedId = (exportSession.SelectedItem as ExportChoice)?.Id ?? sessionId;
            var choices = snapshot?.Records.Where(r => r.ProductSessionId is not null)
                .GroupBy(r => r.ProductSessionId!).Select(group => new ExportChoice(group.Key, group.First().SessionStartedAtUtc)).ToArray() ?? [];
            exportSession.ItemsSource = choices;
            exportSession.SelectedItem = choices.FirstOrDefault(c => c.Id == selectedId) ?? choices.LastOrDefault();
        }
        var capture = state.GetProperty("diagnostic_capture");
        var captureState = capture.GetProperty("state").GetString();
        modelReady = sessionProbeWorker is not null || state.GetProperty("model").GetProperty("state").GetString() == "Ready";
        var joined = !capture.GetProperty("awaiting_capture_join").GetBoolean();
        var vad = state.GetProperty("diagnostic_live_vad");
        var vadJoined = !vad.TryGetProperty("awaiting_join", out var awaitingVad) || !awaitingVad.GetBoolean();
        var session = state.GetProperty("session");
        sessionId = session.GetProperty("session_id").GetString();
        sessionState = session.GetProperty("state").GetString();
        // Explicit fixture mode has no capture/model owner. Only substitute this input
        // boundary; commands, history/epoch guards and real UI timers remain production.
        if (sessionProbeWorker is not null) captureState = sessionState == "Running" ? "Running" : "Stopped";
        captureStartable = sessionState == "Idle" && joined && vadJoined;
        captureNeedsStop = sessionId is not null && sessionState is "Preparing" or "Running" or "Paused" or "Error";
        resumeReady = sessionState == "Paused" && joined && vadJoined && modelReady;
        historyReady = sessionState is "Idle" or "Paused" && joined && vadJoined && !state.GetProperty("diagnostic_asr").GetProperty("decoding").GetBoolean();
        var translator = state.GetProperty("translator");
        translationConfigured = translator.GetProperty("state").GetString() == "Ready";
        appliedProfile = translator.TryGetProperty("input_profile", out var inputProfile) ? inputProfile.GetString() ?? "standard" : "standard";
        if (!profileInitialized || profileApplyPending && !translator.GetProperty("catalog_pending").GetBoolean())
        {
            translationProfile.SelectedItem = profileChoices.FirstOrDefault(p => p.Id == appliedProfile) ?? profileChoices[0];
            profileInitialized = true;
            if (profileApplyPending)
            {
                updatingIsolatedContext = true;
                isolatedTranslationContext.IsChecked = translator.GetProperty("isolated_context").GetBoolean();
                updatingIsolatedContext = false;
                profileApplyPending = false;
            }
        }
        var launchedModel = Environment.GetEnvironmentVariable("ECHOSUB_TRANSLATION_LAUNCH_MODEL_ID");
        translationProfileStatus.Text = (!profilesSupported ? "모델별 입력을 사용하려면 worker를 다시 빌드하세요. " :
            $"적용 입력: {profileChoices.FirstOrDefault(p => p.Id == appliedProfile)?.Label ?? appliedProfile}" +
            (SelectedProfile != appliedProfile ? " · 변경 대기: 선택 모델 적용을 누르세요." : "")) +
            (launchedModel is null ? "\n외부 서버는 모델 목록의 ID만 확인합니다. 가중치는 서버에서 변경하세요." :
                $"\n런처가 불러온 모델: {launchedModel} · 모델 변경은 앱 종료 후 런처를 다시 실행하세요.");
        appliedIsolatedContext = translator.TryGetProperty("isolated_context", out var isolatedContext) && isolatedContext.GetBoolean();
        if (isolatedContextSupported && !isolatedContextInitialized)
        {
            updatingIsolatedContext = true;
            isolatedTranslationContext.IsChecked = appliedIsolatedContext;
            updatingIsolatedContext = false;
            isolatedContextInitialized = true;
        }
        isolatedContextStatus.Text = !isolatedContextSupported ? "이 옵션을 사용하려면 worker를 다시 빌드하세요." :
            (isolatedTranslationContext.IsChecked == true) != appliedIsolatedContext ? "변경 대기 · 서버 연결 / 모델 조회 또는 선택 모델 적용으로 반영하세요." :
            $"적용 상태: {(appliedIsolatedContext ? "켜짐" : "꺼짐")} · 세션 종료 후 변경 · 의미 오류가 남아 있는 실험 기능입니다.";
        translationBusy = translator.GetProperty("in_flight").GetBoolean() || translator.GetProperty("catalog_pending").GetBoolean();
        historyReady &= !translationBusy;
        var ids = translator.GetProperty("models").EnumerateArray().Select(id => id.GetString()!).ToArray();
        if (!translationModels.SequenceEqual(ids))
        {
            var selected = translationModel.SelectedItem as string;
            translationModels = ids;
            translationModel.ItemsSource = ids;
            translationModel.SelectedItem = ids.Contains(selected) ? selected : translator.GetProperty("model_id").GetString();
        }
        UpdateTranslationFeedback(translator);
        exportReady = exportSupported && historyReady;
        status.Text = $"세션 {sessionState} · 모델 {state.GetProperty("model").GetProperty("state").GetString()} · 캡처 {captureState}";
        var phase = capture.GetProperty("failure_native_phase").GetString() ??
            (capture.GetProperty("stats").TryGetProperty("native_phase", out var nativePhase) ? nativePhase.GetString() : "준비 중");
        var opening = capture.GetProperty("opening_elapsed_s");
        var openingText = opening.ValueKind == JsonValueKind.Number ? $" · 시작 대기 {opening.GetDouble():F3}초" : "";
        details.Text = $"{phase}{openingText} · 수신 {capture.GetProperty("accepted_audio_s").GetDouble():F3}초\n" +
            (!joined || !vadJoined ? "소유 스레드가 동작/정리 중입니다. 정리 완료 전 새 캡처를 시작할 수 없습니다.\n" : "") +
            (captureState == "Failed" ? $"실패: {capture.GetProperty("error").GetString()} · 세션 종료 또는 Worker 종료 후 다시 연결하세요." : "일시정지/종료는 미확정 발화와 대기 작업을 폐기합니다.") +
            $"\nUUID {sessionId ?? "없음"} · 경과 {session.GetProperty("elapsed_s").GetDouble():F3}초 · native 실행 {state.GetProperty("diagnostic_asr").GetProperty("native_running").GetBoolean()}" +
            (session.TryGetProperty("started_at_utc", out var utc) ? $"\n시작 UTC {utc.GetString()}" : "");
        if (state.GetProperty("model").GetProperty("state").GetString() == "Failed")
            details.Text += "\n모델 읽기 실패: 모델/DLL 경로·해시와 native CPU 빌드를 확인하세요.";
        while (client.Events.TryRead(out _)) { }
        while (client.Events.TryReadCaptionRecord(out var captionRecord))
            if (captionRecord is not null) captionRecords.Add(captionRecord);
        CaptionDiagnostics.CaptionEventOverflow(client.ProcessId, client.Events.DroppedCaptionEvents);
        var epoch = state.GetProperty("diagnostic_asr").GetProperty("epoch").GetUInt64();
        if (historyChanged && snapshot is not null)
            CaptionDiagnostics.SnapshotApplied(client.ProcessId, snapshot,
                sessionId is not null ? session.GetProperty("internal_session_id").GetUInt64() : 0, epoch);
        CaptionDiagnostics.StatePolled(client.ProcessId, state, snapshot?.Version);
        captions.ShowSource = overlaySourceEnabled.IsChecked == true;
        if (live) overlay?.ValidateSession(sessionId,
            sessionId is not null ? session.GetProperty("internal_session_id").GetUInt64() : 0, epoch,
            sessionState == "Running" && captureState == "Running");
        SetCaptionCards(captions.Update(captionRecords.Concat(snapshot?.Records ?? []), sessionId,
            sessionId is not null ? session.GetProperty("internal_session_id").GetUInt64() : 0, epoch,
            sessionState == "Running" && captureState == "Running", captionClock.Elapsed.TotalSeconds));
    }

    private async Task ConfigureTranslationAsync(bool selectedModel)
    {
        await ExecuteAsync(async () =>
        {
            if (client is null) return;
            var settingAccepted = false;
            var requestSent = false;
            translationCommandFailure = null;
            try
            {
                if (!IsLocalTranslationEndpoint(translationEndpoint.Text?.Trim()))
                {
                    translationCommandFailure = "Contract(InvalidEndpoint)";
                    translationErrorDetails.Text = translationCommandFailure;
                    translationStatus.Text = TranslationFailureAdvice(translationCommandFailure);
                    return;
                }
                var configuration = new Dictionary<string, object?>
                {
                    ["endpoint"] = translationEndpoint.Text?.Trim(),
                    ["model_id"] = selectedModel ? translationModel.SelectedItem as string : null
                };
                if (!profilesSupported && SelectedProfile != "standard") throw new IOException("현재 worker는 모델별 입력을 지원하지 않습니다. 다시 빌드하세요.");
                if (profilesSupported) configuration["input_profile"] = SelectedProfile;
                if (isolatedContextSupported) configuration["isolated_context"] = SelectedProfile == "standard" && isolatedTranslationContext.IsChecked == true;
                requestSent = true;
                await client.SendAsync("configure_translation", configuration);
                settingAccepted = true;
                profileApplyPending = profilesSupported;
                translationBusy = true;
                translationStatus.Text = "번역 서버에 연결해 모델을 확인하고 있습니다.";
                await RefreshCoreAsync();
            }
            catch (Exception error)
            {
                if (settingAccepted || requestSent && error is not WorkerException)
                {
                    translationErrorDetails.Text = error.Message[..Math.Min(error.Message.Length, 512)];
                    translationStatus.Text = settingAccepted
                        ? "설정 요청은 수락됐지만 상태를 확인하지 못했습니다. 상태를 다시 확인하고 있습니다."
                        : "설정 요청의 결과를 확인하지 못했습니다. 상태를 다시 확인하고 있습니다.";
                }
                else ReportTranslationFailure(error);
                if (!settingAccepted && (!requestSent || error is WorkerException))
                {
                    translationProfile.SelectedItem = profileChoices.FirstOrDefault(p => p.Id == appliedProfile) ?? profileChoices[0];
                    updatingIsolatedContext = true;
                    isolatedTranslationContext.IsChecked = appliedIsolatedContext;
                    updatingIsolatedContext = false;
                    isolatedContextStatus.Text = "설정 실패 · 기존 문맥 분리 설정을 유지합니다.";
                }
            }
        });
    }

    private void ClearSource(string reason = "Other")
    {
        CaptionDiagnostics.Cleared(reason, client?.ProcessId);
        captions.Clear();
        latestCards = new(null, null);
        if (live) overlay?.ClearReadingLines();
        if (live) overlay?.SetCards(latestCards);
    }

    private void ExpireSource()
    {
        SetCaptionCards(captions.Tick(captionClock.Elapsed.TotalSeconds));
    }

    private void RecordCaptionApplied(CaptionUpdateTiming timing) =>
        CaptionDiagnostics.Applied(timing, overlay?.IsVisible == true, client?.ProcessId);

    private void RecordTargetObserved(CaptionTargetObservation observation)
    {
        // Candidate diagnostics describe the newest history hypothesis, not necessarily
        // the lines being read. They must never clear the overlay as a side effect.
        CaptionDiagnostics.TargetObserved(observation, client?.ProcessId, overlay?.IsVisible == true);
    }

    private void SetCaptionCards(CaptionCards cards)
    {
        if (latestCards == cards) return;
        latestCards = cards;
        if (live) overlay?.SetCards(cards);
    }

    private async Task DisconnectCoreAsync()
    {
        timer.Stop();
        captionTimer.Stop();
        eventRefreshCancellation?.Cancel();
        if (eventRefreshTask is not null) await eventRefreshTask;
        eventRefreshTask = null;
        eventRefreshCancellation?.Dispose();
        eventRefreshCancellation = null;
        var oldClient = client;
        client = null;
        ClearSource();
        snapshot = null;
        captions = new CaptionDeck { LineReadingManaged = true,
            StabilizeCosmeticRevisions = cosmeticRevisions.IsChecked == true };
        captions.Applied += RecordCaptionApplied;
        captions.TargetObserved += RecordTargetObserved;
        translationSupported = translationBusy = false;
        translationCommandFailure = null;
        translationErrorDetails.Text = "오류가 없습니다.";
        profilesSupported = profileApplyPending = false;
        isolatedContextSupported = translationConfigured = false;
        translationModels = [];
        translationModel.ItemsSource = null;
        translationStatus.Text = "번역 끔 · 로컬 서버를 먼저 실행하세요.";
        history.ItemsSource = null;
        modelReady = captureStartable = captureNeedsStop = false;
        sessionId = sessionState = null;
        resumeReady = false;
        exportReady = exportSupported = false;
        clearSupported = historyReady = partialSupported = false;
        partialTranslationSupported = false;
        exportSession.ItemsSource = null;
        exportResult.Text = "";
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
        savePreferencesButton.IsEnabled = PreferencesEnabled && !closing;
        connectButton.IsEnabled = available && !connected;
        pingButton.IsEnabled = stateButton.IsEnabled = stopButton.IsEnabled = available && connected;
        startCaptureButton.IsEnabled = available && connected && modelReady && captureStartable && !translationBusy &&
            (profilesSupported ? !translationConfigured || SelectedProfile == appliedProfile : SelectedProfile == "standard") &&
            (!isolatedContextSupported || !translationConfigured || (isolatedTranslationContext.IsChecked == true) == appliedIsolatedContext);
        UpdatePreparationStatus();
        userStatus.Text = sessionProbeWorker is not null || !live
            ? "모의 연결입니다. 실제 음성은 처리하지 않습니다."
            : !connected ? "연결되지 않았습니다. 진단 탭에서 다시 연결하세요." : sessionState switch
            {
                "Running" => "자막을 생성하고 있습니다.",
                "Preparing" => "음성 캡처를 준비하고 있습니다.",
                "Paused" => "일시정지했습니다. 재개하면 자막을 이어서 표시합니다.",
                "Stopping" => "자막을 중지하고 있습니다.",
                _ when !modelReady => "음성 인식 준비가 필요합니다. 진단 탭에서 상태를 확인하세요.",
                _ when translationBusy || !available => "작업을 처리하고 있습니다. 잠시 기다리세요.",
                _ when startCaptureButton.IsEnabled => "시작할 준비가 됐습니다.",
                _ => "시작할 수 없습니다. 설정과 진단 탭에서 상태를 확인하세요."
            };
        stopCaptureButton.IsEnabled = available && connected && captureNeedsStop;
        pauseButton.IsEnabled = available && connected && sessionState is "Preparing" or "Running";
        resumeButton.IsEnabled = available && connected && resumeReady;
        exportTxtButton.IsEnabled = exportSrtButton.IsEnabled = available && connected && exportReady && !selectingExport && exportSession.SelectedItem is ExportChoice;
        clearHistoryButton.IsEnabled = available && connected && clearSupported && historyReady && !selectingExport && exportSession.SelectedItem is ExportChoice;
        language.IsEnabled = endpoint.IsEnabled = available && captureStartable;
        var translationEditable = available && connected && captureStartable && historyReady && translationSupported && !translationBusy;
        catalogButton.IsEnabled = disableTranslationButton.IsEnabled = translationEndpoint.IsEnabled = translationModel.IsEnabled = translationEditable;
        applyTranslationButton.IsEnabled = translationEditable && translationModel.SelectedItem is string;
        translationProfile.IsEnabled = translationEditable && profilesSupported;
        isolatedTranslationContext.IsEnabled = translationEditable && isolatedContextSupported && SelectedProfile == "standard";
        partialEnabled.IsEnabled = available && captureStartable && partialSupported;
        partialTranslationEnabled.IsEnabled = available && captureStartable && partialTranslationSupported;
        cosmeticRevisions.IsEnabled = available && (sessionState is null or "Idle");
    }

    public sealed record EndpointChoice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }
    public sealed record ExportChoice(string Id, string? StartedUtc)
    {
        public override string ToString() => $"{StartedUtc ?? "UTC 없음"} · {Id}";
    }
}
