using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EchoSub.Desktop;

/// <summary>
/// First-run UI for model installation and setup.
/// Guides the user through: status check → model selection → consent → installation → ready.
/// </summary>
internal sealed class SetupFirstRun
{
    private readonly SetupCatalog catalog;
    private readonly SetupHost host;
    private readonly string installRoot;
    private readonly ISetupDownloadTransport transport;
    private readonly Action<SetupInstallProgress>? progressCallback;

    // UI Controls
    private readonly TextBlock statusText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock detailsText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar progressBar = new() { Height = 20 };
    private readonly Button installButton = new() { Content = "설치 시작" };
    private readonly Button cancelButton = new() { Content = "취소" };
    private readonly ComboBox profileSelector = new() { Width = 300 };
    private readonly ComboBox languageSelector = new() { Width = 150 };
    private readonly CheckBox consentCheck = new() { Content = "다운로드·설치에 동의합니다." };

    private CancellationTokenSource? cancellationSource;
    private bool isInstalling;

    public SetupFirstRun(SetupCatalog catalog, SetupHost host, string installRoot,
        ISetupDownloadTransport transport, Action<SetupInstallProgress>? progressCallback = null)
    {
        this.catalog = catalog;
        this.host = host;
        this.installRoot = installRoot;
        this.transport = transport;
        this.progressCallback = progressCallback;
    }

    /// <summary>
    /// Creates the first-run UI panel.
    /// </summary>
    public Control CreatePanel()
    {
        // Profile selector
        var profiles = catalog.Profiles.Values
            .Where(p => p.Os == host.Os && p.Architecture == host.Architecture)
            .Select(p => new ProfileChoice(p.Id, p))
            .ToList();
        profileSelector.ItemsSource = profiles;
        if (profiles.Count > 0) profileSelector.SelectedIndex = 0;

        // Language selector
        var languages = new[] { "en", "ja", "ko", "zh" };
        languageSelector.ItemsSource = languages;
        languageSelector.SelectedIndex = 0;

        // Layout
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(16) };

        panel.Children.Add(new TextBlock
        {
            Text = "첫 실행 설정",
            FontSize = 20,
            FontWeight = FontWeight.Bold
        });

        panel.Children.Add(new TextBlock
        {
            Text = "사용할 모델 조합과 언어를 선택하세요.",
            TextWrapping = TextWrapping.Wrap
        });

        // Profile selection
        panel.Children.Add(new TextBlock { Text = "모델 조합" });
        panel.Children.Add(profileSelector);

        // Language selection
        panel.Children.Add(new TextBlock { Text = "음성 언어" });
        panel.Children.Add(languageSelector);

        // Consent
        panel.Children.Add(consentCheck);

        // Status
        panel.Children.Add(statusText);
        panel.Children.Add(detailsText);

        // Progress bar
        progressBar.IsVisible = false;
        panel.Children.Add(progressBar);

        // Buttons
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        buttons.Children.Add(installButton);
        buttons.Children.Add(cancelButton);
        cancelButton.IsVisible = false;
        panel.Children.Add(buttons);

        // Event handlers
        installButton.Click += OnInstallClick;
        cancelButton.Click += OnCancelClick;
        profileSelector.SelectionChanged += OnProfileChanged;
        languageSelector.SelectionChanged += OnLanguageChanged;

        // Initial status check
        UpdateStatus();

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
    }

    private void OnProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var profile = (profileSelector.SelectedItem as ProfileChoice)?.Profile;
        var language = languageSelector.SelectedItem as string ?? "en";

        if (profile is null)
        {
            statusText.Text = "사용 가능한 모델 조합이 없습니다.";
            installButton.IsEnabled = false;
            return;
        }

        // Check readiness
        var readiness = SetupReadiness.Evaluate(
            catalog,
            profile.Id,
            language,
            profile.Target,
            host,
            [],
            null
        );

        statusText.Text = readiness.State switch
        {
            SetupState.Ready => "✅ 설치 완료. 자막을 시작할 수 있습니다.",
            SetupState.DownloadRequired => $"📥 다운로드 필요: {readiness.DownloadBytes / 1024 / 1024:F1} MB",
            SetupState.AwaitingConsent => "📋 동의가 필요합니다.",
            SetupState.InsufficientSpace => $"❌ 저장 공간 부족: {readiness.RequiredFreeBytes / 1024 / 1024:F1} MB 필요",
            SetupState.StorageUnavailable => "❌ 저장소 사용 불가",
            SetupState.CatalogIncomplete => "❌ 카탈로그 불완전",
            SetupState.Unsupported => "❌ 지원되지 않는 조합",
            _ => "⏳ 상태 확인 중..."
        };

        // Show details
        if (readiness.AffectedAssets.Count > 0)
        {
            detailsText.Text = $"영향받는 자산: {string.Join(", ", readiness.AffectedAssets)}";
        }
        else
        {
            detailsText.Text = "";
        }

        // Update install button
        installButton.IsEnabled = readiness.State is SetupState.DownloadRequired or SetupState.AwaitingConsent;
        installButton.Content = readiness.State == SetupState.DownloadRequired ? "다운로드 시작" : "설치 시작";
    }

    private async void OnInstallClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (isInstalling) return;

        var profile = (profileSelector.SelectedItem as ProfileChoice)?.Profile;
        var language = languageSelector.SelectedItem as string ?? "en";

        if (profile is null) return;

        // Check consent
        if (!consentCheck.IsChecked == true)
        {
            statusText.Text = "⚠️ 동의를 확인해주세요.";
            return;
        }

        isInstalling = true;
        installButton.IsEnabled = false;
        cancelButton.IsVisible = true;
        progressBar.IsVisible = true;
        progressBar.Value = 0;

        cancellationSource = new CancellationTokenSource();

        try
        {
            var result = await SetupInstaller.InstallAsync(
                catalog,
                profile.Id,
                language,
                profile.Target,
                host,
                null, // consentFingerprint - will be set after consent
                installRoot,
                transport,
                progress =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (progress.Total > 0)
                        {
                            progressBar.Value = (progress.Bytes * 100) / progress.Total;
                        }
                        statusText.Text = $"📥 {progress.AssetKey}: {progress.Phase} ({progress.Bytes / 1024 / 1024:F1} MB / {progress.Total / 1024 / 1024:F1} MB)";
                    });
                },
                cancellationSource.Token
            );

            Dispatcher.UIThread.Post(() =>
            {
                statusText.Text = "✅ 설치 완료! 자막을 시작할 수 있습니다.";
                progressBar.Value = 100;
                progressBar.IsVisible = false;
                cancelButton.IsVisible = false;
                installButton.IsEnabled = true;
                installButton.Content = "다시 설치";
            });
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(() =>
            {
                statusText.Text = "⏹️ 설치가 취소되었습니다.";
                progressBar.IsVisible = false;
                cancelButton.IsVisible = false;
                installButton.IsEnabled = true;
            });
        }
        catch (SetupException ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                statusText.Text = $"❌ 설치 실패: {ex.Message}";
                detailsText.Text = $"오류 코드: {ex.Code}";
                progressBar.IsVisible = false;
                cancelButton.IsVisible = false;
                installButton.IsEnabled = true;
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                statusText.Text = $"❌ 설치 실패: {ex.Message}";
                progressBar.IsVisible = false;
                cancelButton.IsVisible = false;
                installButton.IsEnabled = true;
            });
        }
        finally
        {
            isInstalling = false;
            cancellationSource?.Dispose();
            cancellationSource = null;
        }
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        cancellationSource?.Cancel();
    }

    private sealed record ProfileChoice(string Id, SetupProfile Profile)
    {
        public override string ToString() => Profile.Id;
    }
}
