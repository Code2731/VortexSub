using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using System.Globalization;

namespace EchoSub.Desktop;

// Explicit rendered fixture: real OverlayWindow, synthetic text, no worker/audio/model.
internal static class CaptionLineProbe
{
    public static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string reportPath)
    {
        OverlayWindow? overlay = null;
        var checks = new List<object>();
        var frames = new List<object>();
        var exit = 0;
        double now = 0;
        try
        {
            reportPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            overlay = new OverlayWindow(true, () => now);
            overlay.ResetPlacement(); overlay.Show();
            await Task.Delay(400);
            CaptionCard Card(string? text, ulong epoch = 1) => new("fixture source", text, true, "fixture source")
                { Identity = new("fixture", 1, epoch, 1, 0) };
            HistoryRecord Record(string text, ulong request = 1, bool preview = false) => new(1, 2, 1, request, request,
                0, 16000, 0, 1, preview ? "Partial" : "Final", "fixture source", null, "Done", text, null,
                request, "fixture", StableSource: preview ? "fixture source" : "", TranslationIsPreview: preview,
                TranslationSource: preview ? "fixture source" : "", TranslationPrefix: preview ? "fixture source" : "");
            void Require(bool passed, string name)
            {
                checks.Add(new { name, passed, virtual_s = now });
                if (!passed) throw new IOException(name);
            }
            void Frame(string name)
            {
                var scale = overlay.RenderScaling;
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(overlay.Bounds.Width * scale),
                    (int)Math.Ceiling(overlay.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                bitmap.Render(overlay);
                bitmap.Save(Path.Combine(Path.GetDirectoryName(reportPath)!, name + ".png"));
                var state = overlay.ReadingState();
                object Box(Rect r) => new { x = r.X * scale, y = r.Y * scale,
                    width = r.Width * scale, height = r.Height * scale };
                frames.Add(new { name, virtual_s = now, upper = state.Upper, lower = state.Lower,
                    upper_box = Box(state.UpperBounds), lower_box = Box(state.LowerBounds) });
            }
            overlay.SetCards(new(null, Card("문 앞에서 기다려.")));
            await Task.Delay(100);
            var initial = overlay.ReadingState();
            Require(initial.Upper == "문 앞에서 기다려." && initial.Lower == "", "first line rendered");
            Frame("01-first");
            var suffix = " 신호가 오면 움직여.";
            for (int i = 1; i <= suffix.Length; i++)
            {
                var prior = overlay.ReadingState();
                var priorRuns = overlay.ReadingRuns();
                now = i * .025;
                overlay.SetCards(new(null, Card("문 앞에서 기다려." + suffix[..i])));
                await Task.Delay(40);
                var state = overlay.ReadingState();
                var nextRuns = overlay.ReadingRuns();
                Require(priorRuns.Upper.Select((run, index) => index < nextRuns.Upper.Length &&
                    ReferenceEquals(run, nextRuns.Upper[index])).All(retained => retained),
                    "append preserves existing upper glyph runs " + i);
                Require(priorRuns.Lower.Select((run, index) => index < nextRuns.Lower.Length &&
                    ReferenceEquals(run, nextRuns.Lower[index])).All(retained => retained),
                    "append preserves existing lower glyph runs " + i);
                Require(state.Upper == initial.Upper && state.UpperBounds == initial.UpperBounds,
                    "append retains first line text/geometry " + i);
                if (i > 1) Require(state.Lower.Length > 0, "append never clears second line " + i);
                if (i > 2) Require(state.Lower.StartsWith(prior.Lower, StringComparison.Ordinal) &&
                    state.LowerBounds == prior.LowerBounds, "append retains lower prefix/geometry " + i);
                Frame($"append-{i:D2}");
            }
            Require(overlay.ReadingState().Lower.Trim() == suffix.Trim(), "open line receives entire appended phrase");
            Frame("02-appended");
            var beforePending = overlay.ReadingState();
            overlay.SetCards(new(null, Card(null)));
            Require(overlay.ReadingState() == beforePending, "pending translation does not blank/reflow controls");
            now = 1;
            overlay.SetCards(new(null, Card("문 앞에서 기다리지 마.")));
            Require(overlay.ReadingState() == beforePending, "correction waits without blank frame");
            Frame("03-correction-held");
            now = 3.5; overlay.AdvanceReading();
            await Task.Delay(100);
            Require(overlay.ReadingState().Upper == "문 앞에서 기다리지 마.", "latest correction rendered after reading");
            Frame("04-corrected");
            overlay.ValidateSession("fixture", 1, 2, true);
            Require(overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "", "epoch clears rendered lines");
            now = 4; overlay.SetCards(new(null, Card("새 세션 자막", 2)));
            await Task.Delay(100);
            Require(overlay.ReadingState().Upper == "새 세션 자막", "new epoch shows only fresh text");
            Frame("05-new-epoch");
            overlay.ValidateSession("fixture", 1, 2, false);
            await Task.Delay(100);
            Require(overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "", "stop clears rendered lines");
            Frame("06-stopped");

            overlay.ClearReadingLines();
            overlay.Width = 420;
            await Task.Delay(100);
            now = 10;
            overlay.SetCards(new(null, Card("첫 번째 줄\n두 번째 줄", 2)));
            await Task.Delay(100);
            var multiline = overlay.ReadingState();
            Require(multiline.Upper == "첫 번째 줄" && multiline.Lower == "두 번째 줄",
                "explicit newline occupies two single-line slots");
            Require(Math.Abs(multiline.UpperBounds.Height - OverlayWindow.ReadingLineHeight) < 1 && Math.Abs(multiline.LowerBounds.Height - OverlayWindow.ReadingLineHeight) < 1,
                "newline cannot increase line box height");
            Frame("07-newline");
            overlay.SetSourceVisible(true);
            await Task.Delay(100);
            Require(overlay.ReadingState().UpperBounds == multiline.UpperBounds &&
                overlay.ReadingState().LowerBounds == multiline.LowerBounds,
                "source visibility does not move translation slots");
            overlay.SetSourceVisible(false);
            foreach (var separator in new[] { "\r\n", "\r" })
            {
                overlay.ClearReadingLines(); now += 1;
                overlay.SetCards(new(null, Card("계속", 2)));
                await Task.Delay(40);
                overlay.SetCards(new(null, Card("계속" + separator + "다음", 2)));
                await Task.Delay(40);
                var state = overlay.ReadingState();
                Require(state.Upper == "계속" && state.Lower == "다음" &&
                    Math.Abs(state.UpperBounds.Height - OverlayWindow.ReadingLineHeight) < 1 && Math.Abs(state.LowerBounds.Height - OverlayWindow.ReadingLineHeight) < 1,
                    "appended separator keeps single-line geometry " + separator.Length);
            }
            overlay.ClearReadingLines(); now = 15;
            const string longText = "첫 구간에서는 문 앞에서 기다려 주세요. 🙂 두 번째 구간에서는 신호를 확인하고 다음 위치로 이동합니다. " +
                "日本語の字幕も同じ場所に表示します。 세 번째 구간은 결합 문자 e\u0301와 가족 👨‍👩‍👧‍👦을 포함합니다. 마지막 구간까지 차례로 표시합니다.";
            var deck = new CaptionDeck { LineReadingManaged = true };
            overlay.SetCards(deck.Update([Record(longText)], "fixture", 1, 2, true, now));
            await Task.Delay(100);
            Frame("08-long-initial");
            var narrow = overlay.ReadingState();
            Require(narrow.Upper.Length > 0 && narrow.Lower.Length > 0 &&
                !narrow.Upper.Contains('\n') && !narrow.Lower.Contains('\n'), "long multilingual text occupies two bounded slots");
            overlay.Width = 760;
            await Task.Delay(100);
            Require(overlay.ReadingState().Upper == narrow.Upper && overlay.ReadingState().Lower == narrow.Lower,
                "widening does not rewrite reading lines");
            overlay.Width = 420;
            await Task.Delay(100);
            Require(overlay.ReadingState() == narrow, "resize round trip restores geometry without re-splitting text");
            var emitted = new List<string> { narrow.Upper, narrow.Lower };
            for (int step = 1; step <= 250; step++)
            {
                var prior = overlay.ReadingState();
                now = 15 + step * .1;
                overlay.SetCards(deck.Tick(now));
                overlay.AdvanceReading();
                await Task.Delay(2);
                var state = overlay.ReadingState();
                if (prior.Lower.Length > 0 && state.Lower == prior.Lower && state.Upper != prior.Upper)
                    Require(state.Upper.Length == 0, "newer text never appears above retained older lower line " + step);
                if (state.Upper.Length > 0 && state.Upper != prior.Upper) emitted.Add(state.Upper);
                if (state.Lower.Length > 0 && state.Lower != prior.Lower) emitted.Add(state.Lower);
                Require(Math.Abs(state.UpperBounds.Height - OverlayWindow.ReadingLineHeight) < 1 && Math.Abs(state.LowerBounds.Height - OverlayWindow.ReadingLineHeight) < 1,
                    "long text keeps line heights " + step);
                Require(state.UpperBounds == narrow.UpperBounds && state.LowerBounds == narrow.LowerBounds,
                    "long text never moves either slot " + step);
                if (state.Upper != prior.Upper || state.Lower != prior.Lower) Frame($"long-{step:D3}");
            }
            Require(string.Concat(emitted) == longText, "long text drains in order without dropped or duplicated graphemes");
            var boundaries = StringInfo.ParseCombiningCharacters(longText).ToHashSet();
            var offset = 0;
            foreach (var fragment in emitted)
            {
                offset += fragment.Length;
                Require(offset == longText.Length || boundaries.Contains(offset),
                    "line ends at a grapheme boundary " + offset);
            }
            Require(overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "",
                "drained long text never resurrects");

            overlay.ClearReadingLines(); deck.Clear(); now = 45;
            overlay.SetCards(deck.Update([Record("문 앞", preview: true)], "fixture", 1, 2, true, now));
            await Task.Delay(100);
            var priorAppend = overlay.ReadingState();
            now = 45.1;
            overlay.SetCards(deck.Update([Record("문 앞에서 기다려", 2, true)], "fixture", 1, 2, true, now));
            await Task.Delay(100);
            Require(overlay.ReadingState().Upper == "문 앞에서 기다려" &&
                overlay.ReadingState().UpperBounds == priorAppend.UpperBounds,
                "history deck append reaches rendered line without replacement delay");
            Frame("09-deck-append");
            now = 45.2;
            overlay.SetCards(deck.Update([Record("문 뒤에서 기다려", 3)], "fixture", 1, 2, true, now));
            await Task.Delay(100);
            Require(overlay.ReadingState().Upper == "문 앞에서 기다려", "deck final rewrite preserves reading line");
            now = 47.7; overlay.SetCards(deck.Tick(now)); overlay.AdvanceReading();
            await Task.Delay(100);
            Require(overlay.ReadingState().Upper == "문 뒤에서 기다려", "deck final correction renders after reading deadline");
            Frame("10-deck-correction");
            overlay.ValidateSession("fixture", 1, 2, false);
            overlay.SetCards(deck.Update([Record("문 뒤에서 기다려", 3)], "fixture", 1, 2, false, 48));
            now = 60; overlay.AdvanceReading();
            Require(overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "",
                "stop clears deck and retained unread input together");

            now = 65;
            overlay.SetCards(deck.Update([Record("앞 문장.")], "fixture", 1, 2, true, now));
            await Task.Delay(100);
            Frame("11-before-next-unit");
            now = 65.1;
            var arriving = deck.Update([Record("다음 문장.", 2) with { SegmentId = 2 }], "fixture", 1, 2, true, now);
            Require(arriving.Current?.Identity?.Segment == 2, "next unit admitted without paragraph replacement delay");
            overlay.SetCards(arriving);
            await Task.Delay(100);
            Require(overlay.ReadingState().Upper == "앞 문장.", "next unit cannot erase a protected line");
            now = 67.6; overlay.SetCards(deck.Tick(now)); overlay.AdvanceReading();
            await Task.Delay(100);
            Require(overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "다음 문장.",
                "one-shot next unit stays lower without history refresh or relocation");
            Frame("12-next-unit-rendered");
            overlay.ClearReadingLines(); deck.Clear(); now = 80;
            const string assemblyBefore = "문 앞에서 기다려. 왼쪽 길로 가세요.";
            const string assemblyAfter = "문 앞에서 기다려. 오른쪽 길로 가세요.";
            overlay.SetCards(deck.Update([Record(assemblyBefore, preview: true)], "fixture", 1, 2, true, now));
            await Task.Delay(100);
            var assemblyInitial = overlay.ReadingState();
            Frame("13-assembly-before");
            now = 80.1;
            overlay.SetCards(deck.Update([Record(assemblyAfter, 2)], "fixture", 1, 2, true, now));
            await Task.Delay(100);
            Require(overlay.ReadingState() == assemblyInitial, "assembly correction retains unread glyphs and geometry");
            Frame("14-assembly-held");
            now = 83.5; overlay.SetCards(deck.Tick(now)); overlay.AdvanceReading();
            await Task.Delay(100);
            Require(overlay.ReadingState().Upper == "오른쪽 길로 가세요." && overlay.ReadingState().Lower == "",
                "assembly renders changed clause without replaying identical sentence");
            Frame("15-assembly-corrected");
            overlay.ClearReadingLines(); now = 90;
            overlay.SetCards(new(null, Card(assemblyBefore, 2)));
            await Task.Delay(100);
            var recoveryInitial = overlay.ReadingState();
            Frame("16-recovery-before");
            now = 90.1;
            overlay.SetCards(new(null, Card("대기 중인 다음 구간.", 2) with
                { Identity = new("fixture", 1, 2, 1, 20) }));
            now = 90.2;
            overlay.SetCards(new(null, Card(assemblyAfter, 2)));
            await Task.Delay(100);
            Require(overlay.ReadingState() == recoveryInitial, "returned unit preserves protected lines");
            Frame("17-recovery-held");
            now = 93.5; overlay.AdvanceReading();
            await Task.Delay(100);
            Require(overlay.ReadingState().Upper == "오른쪽 길로 가세요." && overlay.ReadingState().Lower == "",
                "returned unit clears obsolete unit transition and reuses completed clause");
            Frame("18-recovery-corrected");
            overlay.ClearReadingLines(); now = 100;
            overlay.SetCards(new(null, Card(assemblyBefore, 2)));
            await Task.Delay(100);
            var rapidInitial = overlay.ReadingState();
            Frame("19-rapid-before");
            foreach (var target in new[] { assemblyAfter, "문 앞에서 기다려. 뒤쪽 길로 가세요.", "문 앞에서 기다려. 문을 열지 마세요." })
            {
                now += .1; overlay.SetCards(new(null, Card(target, 2)));
                Require(overlay.ReadingState() == rapidInitial, "rapid correction retains protected render " + target);
            }
            await Task.Delay(100); Frame("20-rapid-held");
            now = 103.5; overlay.AdvanceReading();
            await Task.Delay(100);
            Require(overlay.ReadingState().Upper == "문을 열지 마세요." && overlay.ReadingState().Lower == "",
                "rapid correction renders latest negation without obsolete candidates");
            Frame("21-rapid-corrected");
            overlay.ClearReadingLines(); now = 110;
            overlay.SetCards(new(null, Card(assemblyBefore, 2)));
            now += .1; overlay.SetCards(new(null, Card(assemblyAfter, 2)));
            now += .1; overlay.SetCards(new(null, Card(assemblyBefore, 2)));
            now = 113.5; overlay.AdvanceReading();
            await Task.Delay(100);
            Require(overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "", "cancelled correction never reappears in render");
            Frame("22-cancelled");
            now = 120; overlay.SetCards(new(null, Card(assemblyBefore, 2)));
            now += .1; overlay.SetCards(new(null, Card(assemblyAfter, 2)));
            overlay.ValidateSession("fixture", 1, 2, false);
            now = 123.5; overlay.AdvanceReading();
            await Task.Delay(100);
            Require(overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "", "stopped pending correction never reappears in render");
            Frame("23-pending-stopped");
        }
        catch (Exception error) { exit = 1; checks.Add(new { error = error.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { passed = exit == 0,
                scope = "Actual Avalonia overlay controls/rendered fixture with virtual reading clock; no inference, audio or continuous desktop-frame proof", checks, frames }, new JsonSerializerOptions { WriteIndented = true }));
            overlay?.Close(); desktop.Shutdown(exit);
        }
    }
}

