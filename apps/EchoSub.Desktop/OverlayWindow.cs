using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using System.Globalization;

namespace EchoSub.Desktop;

public sealed class OverlayWindow : Window
{
    internal const double ReadingLineHeight = 42;
    private readonly Border captionCard;
    private readonly TextBlock sourceCaption;
    private readonly TextBlock translationCaption;
    private readonly TextBlock secondTranslation;
    private readonly TextBlock measureLine = new() { FontSize = 26, TextWrapping = TextWrapping.NoWrap };
    private readonly CaptionLines lines = new();
    private readonly DispatcherTimer lineTimer = new() { Interval = TimeSpan.FromSeconds(0.1) };
    private readonly Func<double> readingClock;
    private readonly ScrollViewer captionScroll;
    private CaptionIdentity? displayedIdentity;
    private string latestSource = "";
    private string renderedUpper = "", renderedLower = "";
    private bool sourceVisible;
    private string? lastFitText;
    private double lastFitWidth;
    private int lastFitLength;

    public OverlayWindow(bool live = false, Func<double>? clock = null)
    {
        readingClock = clock ?? (() => CaptionDiagnostics.Now);
        lines.Observed += observation => CaptionDiagnostics.ReadingObserved(observation, IsVisible);
        Title = live ? "EchoSub · 실제 원문 진단" : "EchoSub · MOCK overlay";
        Width = 760;
        Height = 220;
        MinWidth = 420;
        MinHeight = 180;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = true;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        var moveHandle = new TextBlock
        {
            Text = live ? "실시간 자막 · 내용은 갱신될 수 있음 · 이 줄을 끌어 이동" : "MOCK · 샘플 자막 · 이 줄을 끌어 이동",
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
            FontSize = 20,
            TextWrapping = TextWrapping.Wrap
        };
        translationCaption = new TextBlock
        {
            Text = live ? "" : "왼쪽 길로 가자.",
            Foreground = new SolidColorBrush(Color.Parse("#F7DE92")),
            FontSize = 26, TextWrapping = TextWrapping.NoWrap,
            Height = ReadingLineHeight, LineHeight = 36, ClipToBounds = true
        };
        latestSource = sourceCaption.Text ?? "";
        renderedUpper = translationCaption.Text ?? "";
        secondTranslation = new TextBlock { FontSize = 26, Height = ReadingLineHeight, LineHeight = 36,
            ClipToBounds = true, TextWrapping = TextWrapping.NoWrap,
            Foreground = new SolidColorBrush(Color.Parse("#F7DE92")) };
        var captionText = new StackPanel { Spacing = 5, Children = { translationCaption, secondTranslation, sourceCaption } };
        captionScroll = new ScrollViewer { Content = captionText,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var captions = captionScroll;
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
        lineTimer.Tick += (_, _) => AdvanceReading();
        Opened += (_, _) => { if (live && clock is null) lineTimer.Start(); };
        Closed += (_, _) => lineTimer.Stop();
    }

    public void SetSourceVisible(bool visible)
    {
        sourceVisible = visible;
        if (visible && sourceCaption.Text != latestSource) sourceCaption.Text = latestSource;
        var show = visible && !string.IsNullOrWhiteSpace(latestSource);
        if (sourceCaption.IsVisible != show) sourceCaption.IsVisible = show;
    }

    public void SetCards(CaptionCards cards)
    {
        // Never fall back to an older card after a newer caption has been shown.
        var caption = cards.Current;
        latestSource = Body(caption?.Source);
        lines.Apply(cards, readingClock(), FitLine);
        RenderLines();
        SetSourceVisible(sourceVisible);
        if (IsVisible) CaptionDiagnostics.OverlayAssigned(cards);
    }

    private int FitLine(string text)
    {
        var width = Math.Max(100, captionCard.Bounds.Width > 0 ? captionCard.Bounds.Width - 40 : Width - 56);
        if (lastFitWidth == width && lastFitText == text) return lastFitLength;
        var elements = StringInfo.ParseCombiningCharacters(text);
        int fitted = 0, wordEnd = 0;
        for (int index = 0; index < elements.Length; index++)
        {
            var start = elements[index];
            if (text[start] is '\n' or '\r')
                return CacheFit(text, width, start + (text[start] == '\r' && start + 1 < text.Length && text[start + 1] == '\n' ? 2 : 1));
            var end = index + 1 < elements.Length ? elements[index + 1] : text.Length;
            measureLine.Text = text[..end];
            measureLine.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (measureLine.DesiredSize.Width > width && fitted > 0) break;
            fitted = end;
            if (char.IsWhiteSpace(text[start])) wordEnd = end;
        }
        // Prefer a word boundary when it does not waste most of the line.
        return CacheFit(text, width, fitted < text.Length && wordEnd > fitted / 2 ? wordEnd : Math.Max(1, fitted));
    }

    private int CacheFit(string text, double width, int length)
    {
        lastFitText = text; lastFitWidth = width; lastFitLength = length;
        return length;
    }

    private void RenderLines()
    {
        // Scroll position belongs to displayed lines, not incoming ASR hypotheses.
        if (displayedIdentity != lines.Identity) captionScroll.Offset = default;
        displayedIdentity = lines.Identity;
        // Keep both line boxes allocated. Never collapse/reflow one when it expires.
        if (renderedUpper != (lines.Upper ?? ""))
        {
            ApplyLine(translationCaption, ref renderedUpper, lines.Upper ?? "");
            if (IsVisible) CaptionDiagnostics.LineAssigned("upper", lines.UpperIdentity, renderedUpper.Length);
        }
        if (renderedLower != (lines.Lower ?? ""))
        {
            ApplyLine(secondTranslation, ref renderedLower, lines.Lower ?? "");
            if (IsVisible) CaptionDiagnostics.LineAssigned("lower", lines.LowerIdentity, renderedLower.Length);
        }
    }

    private static void ApplyLine(TextBlock control, ref string rendered, string next)
    {
        // Append a new glyph run; do not remove existing runs for simple growth.
        if (rendered.Length > 0 && next.StartsWith(rendered, StringComparison.Ordinal))
            control.Inlines!.Add(new Run(next[rendered.Length..]));
        else
        {
            control.Text = null;
            control.Inlines!.Clear();
            if (next.Length > 0) control.Inlines.Add(new Run(next));
        }
        rendered = next;
    }

    public void ClearReadingLines()
    {
        lines.Clear();
        RenderLines();
    }

    public void ValidateSession(string? product, ulong session, ulong epoch, bool running)
    {
        lines.ValidateSession(product, session, epoch, running);
        RenderLines();
    }

    internal void AdvanceReading()
    {
        lines.Tick(readingClock(), FitLine);
        RenderLines();
    }

    internal (string Upper, string Lower, Rect UpperBounds, Rect LowerBounds) ReadingState() =>
        (renderedUpper, renderedLower,
            new Rect(translationCaption.TranslatePoint(default, this) ?? default, translationCaption.Bounds.Size),
            new Rect(secondTranslation.TranslatePoint(default, this) ?? default, secondTranslation.Bounds.Size));
    internal (Inline[] Upper, Inline[] Lower) ReadingRuns() =>
        (translationCaption.Inlines!.ToArray(), secondTranslation.Inlines!.ToArray());

    private static string Body(string? text)
    {
        if (text is null) return "";
        foreach (var label in new[] { "[임시 번역] ", "[인식 중] ", "[확정 처리 중] " })
            if (text.StartsWith(label, StringComparison.Ordinal)) return text[label.Length..];
        return text;
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
        Height = 220;
        Position = new PixelPoint(
            area.X + (area.Width - (int)Math.Round(Width * scale)) / 2,
            area.Bottom - (int)Math.Round((Height + 32) * scale));
    }
}
