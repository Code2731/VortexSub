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

    public OverlayWindow(bool live = false)
    {
        Title = live ? "EchoSub · 실제 원문 진단" : "EchoSub · MOCK overlay";
        Width = 760;
        Height = 190;
        MinWidth = 420;
        MinHeight = 150;
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
        var captions = new StackPanel
        {
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
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
        Content = new Border { Padding = new Thickness(8), Child = captionCard };
        Opened += (_, _) => WindowsOverlayPlatform.PreventActivation(this);
    }

    public void SetSource(string? source) => sourceCaption.Text =
        string.IsNullOrWhiteSpace(source) ? "원문 대기 중" : source;

    public void SetCaptions(string? source, string? translation)
    {
        SetSource(source);
        translationCaption.Text = translation ?? "";
        translationCaption.IsVisible = !string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(translation);
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
        Height = 190;
        Position = new PixelPoint(
            area.X + (area.Width - (int)Math.Round(Width * scale)) / 2,
            area.Bottom - (int)Math.Round((Height + 32) * scale));
    }
}
