using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace EchoSub.Desktop;

public sealed class OverlayWindow : Window
{
    private readonly Border captionCard;
    private readonly TextBlock sourceCaption;
    private readonly TextBlock translationCaption;
    private readonly TextBlock previousSource;
    private readonly TextBlock previousTranslation;
    private readonly StackPanel previousCard;
    private readonly TextBlock currentLabel;
    private bool sourceVisible;

    public OverlayWindow(bool live = false)
    {
        Title = live ? "EchoSub · 실제 원문 진단" : "EchoSub · MOCK overlay";
        Width = 760;
        Height = 310;
        MinWidth = 420;
        MinHeight = 230;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = true;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        var moveHandle = new TextBlock
        {
            Text = live ? "실제 자막 진단 · 이 줄을 끌어 이동" : "MOCK · 샘플 자막 · 이 줄을 끌어 이동",
            FontSize = 12,
            Foreground = Brushes.LightGray,
            Cursor = new Cursor(StandardCursorType.SizeAll)
        };
        moveHandle.PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(args);
                args.Handled = true;
            }
        };
        var resizeHandle = new TextBlock
        {
            Text = "↘",
            FontSize = 18,
            Foreground = Brushes.LightGray,
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = new Cursor(StandardCursorType.BottomRightCorner)
        };
        resizeHandle.PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginResizeDrag(WindowEdge.SouthEast, args);
                args.Handled = true;
            }
        };
        sourceCaption = new TextBlock
        {
            Text = live ? "원문 대기 중" : "We should take the left path. / 左の道へ進もう。",
            IsVisible = false,
            Foreground = Brushes.White,
            FontSize = 22,
            TextWrapping = TextWrapping.Wrap
        };
        translationCaption = new TextBlock
        {
            Text = "왼쪽 길로 가자.", IsVisible = !live,
            Foreground = new SolidColorBrush(Color.Parse("#F7DE92")),
            FontSize = 26, TextWrapping = TextWrapping.Wrap
        };
        previousSource = new TextBlock
        {
            Foreground = Brushes.White, FontSize = 20, TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        previousTranslation = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#F7DE92")), FontSize = 24,
            TextWrapping = TextWrapping.Wrap
        };
        previousCard = new StackPanel
        {
            Spacing = 5, IsVisible = false,
            Children =
            {
                new TextBlock { Text = "이전 자막", FontSize = 11, Foreground = Brushes.LightGray },
                previousSource, previousTranslation,
                new Border { Height = 1, Margin = new Thickness(0, 5), Background = Brushes.Gray }
            }
        };
        currentLabel = new TextBlock { FontSize = 11, Foreground = Brushes.LightGray, IsVisible = false };
        var captions = new StackPanel
        {
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                previousCard,
                currentLabel,
                sourceCaption,
                translationCaption
            }
        };
        Grid.SetRow(captions, 1);
        Grid.SetRow(resizeHandle, 2);
        captionCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(190, 18, 22, 30)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20, 12),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Children = { moveHandle, captions, resizeHandle }
            }
        };
        // A resized window can still show both cards without clipping long captions.
        Content = new Border { Padding = new Thickness(8), Child = new ScrollViewer { Content = captionCard } };
        Opened += (_, _) => WindowsOverlayPlatform.PreventActivation(this);
    }

    public void SetSourceVisible(bool visible)
    {
        sourceVisible = visible;
        sourceCaption.IsVisible = visible && !string.IsNullOrWhiteSpace(sourceCaption.Text);
        previousSource.IsVisible = visible && !string.IsNullOrWhiteSpace(previousSource.Text);
        previousCard.IsVisible = previousTranslation.IsVisible || previousSource.IsVisible;
    }

    public void SetCards(CaptionCards cards)
    {
        sourceCaption.Text = cards.Current?.Source ?? "";
        translationCaption.Text = cards.Current?.Translation ?? "";
        translationCaption.IsVisible = !string.IsNullOrWhiteSpace(translationCaption.Text);
        previousSource.Text = cards.Previous?.Source ?? "";
        previousTranslation.Text = cards.Previous?.Translation ?? "";
        previousTranslation.IsVisible = !string.IsNullOrWhiteSpace(previousTranslation.Text);
        SetSourceVisible(sourceVisible);
        currentLabel.Text = cards.Current?.IsDraft == true ? "갱신 중" : "현재 자막";
        currentLabel.IsVisible = sourceCaption.IsVisible || translationCaption.IsVisible;
        if (IsVisible) CaptionDiagnostics.OverlayAssigned(cards);
    }

    public void SetCardOpacity(double opacity)
    {
        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0.25, 1) * 255);
        captionCard.Background = new SolidColorBrush(Color.FromArgb(alpha, 18, 22, 30));
    }

    public void ResetPlacement(Window? reference = null)
    {
        var screen = Screens.ScreenFromWindow(reference ?? this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        Width = Math.Min(760, area.Width / scale - 32);
        Height = 310;
        Position = new PixelPoint(
            area.X + (area.Width - (int)Math.Round(Width * scale)) / 2,
            area.Bottom - (int)Math.Round((Height + 32) * scale));
    }
}
