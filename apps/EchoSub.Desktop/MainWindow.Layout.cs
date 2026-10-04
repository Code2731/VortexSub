using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EchoSub.Desktop;

public sealed partial class MainWindow
{
    private readonly TextBlock userStatus = new() { Text = "준비 상태를 확인하고 있습니다.", TextWrapping = TextWrapping.Wrap };
    internal TabControl? CaptureTabs { get; private set; }
    internal Expander? FirstUseGuide { get; private set; }

    private Control BuildMainLayout(Button resetOverlay, Slider overlayWidth, Slider cardOpacity)
    {
        static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
        static StackPanel Stack(params Control[] controls)
        {
            var panel = new StackPanel { Spacing = 10 };
            foreach (var control in controls) panel.Children.Add(control);
            return panel;
        }
        static Border Card(string title, params Control[] controls)
        {
            var body = Stack(controls);
            body.Children.Insert(0, new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold });
            return new Border { Padding = new Thickness(16), BorderThickness = new Thickness(1),
                BorderBrush = Brushes.LightGray, CornerRadius = new CornerRadius(6), Child = body };
        }
        static WrapPanel Actions(params Control[] controls)
        {
            var panel = new WrapPanel();
            foreach (var control in controls) { control.Margin = new Thickness(0, 0, 8, 8); panel.Children.Add(control); }
            return panel;
        }
        static TabItem Tab(string name, Control body) => new() { Header = name,
            Content = new ScrollViewer { Margin = new Thickness(0, 14, 0, 0), Content = body,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };

        endpoint.Width = double.NaN;
        endpoint.HorizontalAlignment = HorizontalAlignment.Stretch;
        language.HorizontalAlignment = HorizontalAlignment.Left;
        language.Width = 110;
        language.ItemTemplate = new FuncDataTemplate<object>((item, _) => Note((item as string) switch
        {
            "en" => "영어", "ja" => "일본어", "ko" => "한국어", _ => item?.ToString() ?? ""
        }));
        var input = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110"),
            RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 12, RowSpacing = 6 };
        var deviceLabel = Note("음성을 받을 출력 장치");
        var languageLabel = Note("음성 언어");
        Grid.SetColumn(languageLabel, 1); Grid.SetRow(endpoint, 1); Grid.SetRow(language, 1); Grid.SetColumn(language, 1);
        input.Children.Add(deviceLabel); input.Children.Add(languageLabel); input.Children.Add(endpoint); input.Children.Add(language);
        startCaptureButton.Content = "자막 시작";
        stopCaptureButton.Content = "자막 중지";
        startCaptureButton.FontWeight = FontWeight.SemiBold;
        Control? translationSection = null;
        var openTranslationSettings = new Button { Content = "번역 설정" };
        openTranslationSettings.Click += (_, _) =>
        {
            if (CaptureTabs is null) return;
            CaptureTabs.SelectedIndex = 1;
            Dispatcher.UIThread.Post(() => translationSection?.BringIntoView());
        };

        var main = Stack(
            Card("실시간 자막", preparationStatus,
                input, Actions(startCaptureButton, pauseButton, resumeButton, stopCaptureButton, openTranslationSettings),
                Note("장치와 언어를 변경하려면 자막을 중지하세요.")),
            Card("화면에 자막 표시", Actions(overlayButton, resetOverlay), overlaySourceEnabled,
                Note("자막 창의 폭과 배경은 설정 탭에서 조절할 수 있습니다.")));
        main.Children[0].IsVisible = live;
        if (!live) main.Children.Insert(0, Note("모의 실행입니다. 실제 음성 캡처와 번역은 실행하지 않습니다."));

        var widthValue = Note($"{overlayWidth.Value:F0}");
        var opacityValue = Note($"{cardOpacity.Value:P0}");
        overlayWidth.HorizontalAlignment = cardOpacity.HorizontalAlignment = HorizontalAlignment.Left;
        overlayWidth.ValueChanged += (_, _) => widthValue.Text = $"{overlayWidth.Value:F0}";
        cardOpacity.ValueChanged += (_, _) => opacityValue.Text = $"{cardOpacity.Value:P0}";
        static StackPanel SliderRow(Slider slider, TextBlock value) => new()
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { slider, value }
        };
        widthValue.VerticalAlignment = opacityValue.VerticalAlignment = VerticalAlignment.Center;
        var appearance = Card("자막 창 모양", Note("폭"), SliderRow(overlayWidth, widthValue),
            Note("배경 불투명도"), SliderRow(cardOpacity, opacityValue));
        translationEndpoint.HorizontalAlignment = translationModel.HorizontalAlignment = HorizontalAlignment.Left;
        var translation = Card("번역 연결", Note("로컬 번역 서버를 실행한 뒤 연결하세요. 변경은 자막을 중지한 후 적용합니다."),
            Note("서버 주소"), translationEndpoint, Actions(catalogButton, disableTranslationButton),
            Note("모델"), translationModel, translationStatus,
            new Expander { Header = "오류 상세", Content = translationErrorDetails },
            new Expander { Header = "고급 번역 설정", Content = Stack(translationProfile,
                translationProfileStatus, isolatedTranslationContext, isolatedContextStatus) }, applyTranslationButton);
        var experiments = Card("실험 기능", Note("부분 결과는 최종 결과와 다를 수 있습니다."),
            partialEnabled, partialTranslationEnabled, cosmeticRevisions);
        translation.IsVisible = experiments.IsVisible = live;
        translationSection = translation;
        var settings = Stack(appearance, translation, experiments,
            Card("설정 보관", Note("음성 언어, 출력 장치, 자막 창 모양, 원문 표시와 부분 자막 옵션을 저장합니다. 번역 모델은 런처 설정을 사용합니다. 지연 로그 기록은 자동으로 켜지지 않습니다."),
                savePreferencesButton, preferencesStatus));
        var records = Stack(Card("최근 자막", Note("최근 100개 구간입니다. 자막 내용은 자동으로 파일에 저장하지 않습니다."), history),
            Card("기록 저장", Note("일시정지 또는 중지 후 저장할 수 있습니다. 최대 1,000개 구간을 내보냅니다."),
                exportSession, Actions(exportTxtButton, exportSrtButton, clearHistoryButton), exportResult));
        var diagnostics = Stack(Card("연결과 상세 상태", status,
            Actions(connectButton, pingButton, stateButton, stopButton),
            new ScrollViewer { Height = 180, Content = details }),
            Card("문제 분석", captionTimingEnabled, captionTimingStatus));
        CaptureTabs = new TabControl { ItemsSource = new[] { Tab("자막", main), Tab("설정", settings),
            Tab("기록", records), Tab("진단", diagnostics) } };
        var heading = Stack(new TextBlock { Text = "EchoSub", FontSize = 24, FontWeight = FontWeight.SemiBold },
            Note(live ? "음성을 한국어 자막으로 표시합니다." : "모의 자막 화면"), userStatus);
        FirstUseGuide = new Expander { Header = "처음 사용하는 경우", Content = Note(
            "1. 실시간 런처로 앱과 번역 서버를 실행하세요.\n" +
            "2. 출력 장치와 음성 언어를 선택하세요. 번역 연결은 설정 탭에서 확인하세요.\n" +
            "3. 자막 시작을 누른 뒤 음성을 재생하세요. 자막 오버레이 표시로 자막 창을 여세요.") };
        heading.Children.Add(FirstUseGuide);
        heading.Margin = new Thickness(0, 0, 0, 16);
        DockPanel.SetDock(heading, Dock.Top);
        var root = new DockPanel { Margin = new Thickness(20) };
        root.Children.Add(heading); root.Children.Add(CaptureTabs);
        return root;
    }
}
