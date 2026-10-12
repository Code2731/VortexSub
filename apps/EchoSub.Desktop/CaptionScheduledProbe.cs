using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;

namespace EchoSub.Desktop;

internal static class CaptionScheduledProbe
{
    public static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop, string reportPath, bool fromDeck = false)
    {
        reportPath = Path.GetFullPath(reportPath);
        if (File.Exists(reportPath)) { desktop.Shutdown(2); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        OverlayWindow? overlay = null;
        var frames = new List<object>(); var checks = new List<object>();
        int failures = 0, run = 0; double now = 0; string? error = null;
        void Check(string name, bool ok) { checks.Add(new { name, passed = ok }); if (!ok) failures++; }
        try
        {
            overlay = new OverlayWindow(true, () => now);
            overlay.ResetPlacement(); overlay.Width = 760; overlay.Show(); await Task.Delay(300);
            var initial = overlay.ReadingState();
            CaptionDeck? deck = null;
            void Begin()
            {
                run++; now = 0;
                if (fromDeck) { overlay.ClearReadingLines(); deck = new() { LineReadingManaged = true }; }
                else overlay.BeginScheduledReading(new("candidate", 1, 1));
            }
            CaptionScheduleInput Input(string body, ulong request = 1, double at = 0, ulong segment = 1) =>
                new(new("candidate", 1, 1, segment, 0), 1, request, false, new(0, 64, 1), body, at);
            void Submit(CaptionScheduleInput input)
            {
                now = input.AtSeconds;
                if (!fromDeck) Check($"run {run} input accepted", overlay.SetScheduledInput(input).Accepted);
                else
                {
                    string source = new('x', 64);
                    var record = new HistoryRecord(1, 1, input.Identity.Segment, input.SourceRevision, input.SourceRevision,
                        0, 16000, 0, 1, input.IsFinal ? "Final" : "Partial", source, null,
                        "Done", input.Target, null, input.RequestId, "candidate", StableSource: input.IsFinal ? "" : source,
                        TranslationIsPreview: !input.IsFinal, TranslationSource: input.IsFinal ? "" : source,
                        TranslationPrefix: input.IsFinal ? "" : source);
                    var cards = deck!.Update([record], "candidate", 1, 1, true, now);
                    Check($"run {run} selected record delivered", cards.Delivered.Count > 0 && cards.Delivered.All(card => card.ScheduleInput is not null));
                    overlay.SetScheduledCards(cards);
                }
            }
            async Task Frame(double time)
            {
                now = time;
                if (fromDeck) overlay.SetScheduledCards(deck!.Tick(now));
                overlay.AdvanceReading(); await Task.Delay(10);
                var state = overlay.ReadingState();
                Check($"run {run} {time} fixed geometry", state.UpperBounds == initial.UpperBounds && state.LowerBounds == initial.LowerBounds);
                string name = $"candidate-{run:D2}-frame-{frames.Count:D3}";
                var scale = overlay.RenderScaling;
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(overlay.Bounds.Width * scale),
                    (int)Math.Ceiling(overlay.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                bitmap.Render(overlay); bitmap.Save(Path.Combine(Path.GetDirectoryName(reportPath)!, name + ".png"));
                object Box(Rect r) => new { x = r.X * scale, y = r.Y * scale, width = r.Width * scale, height = r.Height * scale };
                frames.Add(new { name, run, virtual_s = time, upper = state.Upper, lower = state.Lower,
                    upper_box = Box(state.UpperBounds), lower_box = Box(state.LowerBounds) });
            }
            Begin(); Submit(Input("이전 문장. 같은 뒷문장.")); await Frame(0);
            Submit(Input("새 문장. 같은 뒷문장.", 2, .1)); await Frame(.1);
            Submit(Input("다음 발화.", at: .2, segment: 2)); await Frame(.2); await Frame(2.49);
            Check("old line held until 2.5", overlay.ReadingState().Upper == "이전 문장. 같은 뒷문장.");
            await Frame(2.5);
            Check("correction head upper and following utterance lower", overlay.ReadingState().Upper == "새 문장. " && overlay.ReadingState().Lower == "다음 발화.");
            Check("stable rear delivered by reuse", overlay.ScheduledStore!.Counters.GetValueOrDefault(CaptionScheduleReason.Reused) == 1);
            await Frame(5); Check("lower stays in place", overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "다음 발화.");
            await Frame(5.25); Check("both lines drain", overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "");

            Begin(); Submit(Input("안녕")); await Frame(0); var firstRuns = overlay.ReadingRuns().Upper;
            Submit(Input("안녕하세요", 2, .4)); await Frame(.4);
            Check("append retains existing Run", ReferenceEquals(firstRuns[0], overlay.ReadingRuns().Upper[0]));
            Check("append stays upper", overlay.ReadingState().Upper == "안녕하세요" && overlay.ReadingState().Lower == "");
            Submit(Input("안녕하세요\n", 3, .8)); await Frame(.8);
            Check("separator retains reading deadline", overlay.ScheduledStore!.UpperLease!.ReadingUntilSeconds == 2.9);
            await Frame(2.9); Check("separator does not delay expiry", overlay.ReadingState().Upper == "");

            Begin(); Submit(Input("원문 수정 전")); await Frame(0);
            now = .1; overlay.InvalidateScheduled(1); await Frame(.1);
            Submit(Input("원문 수정 후.", 2, .2) with { SourceRevision = 2, SourceSpan = new(0, 64, 2) }); await Frame(.2);
            Check("source change preserves visible text", overlay.ReadingState().Upper == "원문 수정 전");
            await Frame(2.5); Check("full revised body assigned", overlay.ReadingState().Upper == "원문 수정 후.");
            now = 2.6; overlay.InvalidateScheduled(1, true); await Frame(2.6);
            Check("withdraw clears visible text", overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "");

            Begin(); Submit(Input("짧은 이전 자막.")); await Frame(0);
            string longBody = string.Concat(Enumerable.Repeat("두 줄에 이어지는 긴 교정 자막을 읽는 동안 위치를 유지합니다. ", 10));
            Submit(Input(longBody, 2, .1)); await Frame(.1); await Frame(2.5);
            Check("correction fills both reading slots", overlay.ReadingState().Upper.Length > 0 && overlay.ReadingState().Lower.Length > 0);
            var retainedLower = overlay.ReadingState().Lower;
            await Frame(5); Check("newer fragment does not jump above retained lower", overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == retainedLower);
            await Frame(5.25); Check("remaining correction resumes", overlay.ReadingState().Upper.Length > 0);

            Begin(); Submit(Input("기다려.\n왼쪽.")); await Frame(0);
            var prefixRuns = overlay.ReadingRuns().Upper;
            var reusedBefore = overlay.ScheduledStore!.Counters.GetValueOrDefault(CaptionScheduleReason.Reused);
            now = .064; overlay.InvalidateScheduled(1); await Frame(.064);
            Submit(Input("기다려.\n오른쪽.", 2, .2) with { SourceRevision = 2, SourceSpan = new(0, 64, 2) }); await Frame(.2);
            Check("prefix reuse preserves visible Run", ReferenceEquals(prefixRuns[0], overlay.ReadingRuns().Upper[0]));
            Check("prefix reuse retains both reading leases", overlay.ScheduledStore!.UpperLease!.ReadingUntilSeconds == 2.5 && overlay.ScheduledStore.LowerLease!.ReadingUntilSeconds == 2.75);
            await Frame(2.49); await Frame(2.5);
            Check("prefix expiry does not move old lower", overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "왼쪽.");
            await Frame(2.75);
            Check("changed suffix replaces expired rows without repeating prefix", overlay.ReadingState().Upper == "오른쪽." && overlay.ReadingState().Lower == "");
            Check("prefix reuse recorded once", overlay.ScheduledStore!.Counters.GetValueOrDefault(CaptionScheduleReason.Reused) == reusedBefore + 1);
            await Frame(5.25); Check("corrected suffix drains", overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "");

            Begin(); Submit(Input("이전 앞문장.\n같은 뒷문장.")); await Frame(0);
            var heldLowerRuns = overlay.ReadingRuns().Lower;
            var heldLowerLease = overlay.ScheduledStore!.LowerLease;
            Submit(Input("교정 앞문장.\n같은 뒷문장.", 2, .1)); await Frame(.1); await Frame(2.49);
            Check("upper correction waits for upper reading deadline", overlay.ReadingState().Upper == "이전 앞문장.");
            await Frame(2.5);
            Check("upper correction displays above proven lower", overlay.ReadingState().Upper == "교정 앞문장." && overlay.ReadingState().Lower == "같은 뒷문장.");
            Check("retained lower Run instance is unchanged", ReferenceEquals(heldLowerRuns[0], overlay.ReadingRuns().Lower[0]));
            Check("retained lower reading lease is unchanged", overlay.ScheduledStore!.LowerLease == heldLowerLease);
            Check("corrected upper gets full reading time", overlay.ScheduledStore!.UpperLease!.ReadingUntilSeconds == 5);
            await Frame(2.75);
            Check("old lower expires without moving new upper", overlay.ReadingState().Upper == "교정 앞문장." && overlay.ReadingState().Lower == "");
            await Frame(5); Check("independent correction drains", overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "");

            Begin(); Submit(Input("철회 전 앞문장.\n같은 뒷문장.")); await Frame(0);
            Submit(Input("철회할 교정.\n같은 뒷문장.", 2, .1)); await Frame(.1); await Frame(2.5);
            Check("correction visible before withdrawal", overlay.ReadingState().Upper == "철회할 교정.");
            now = 2.6;
            if (fromDeck)
            {
                var failed = new HistoryRecord(1, 1, 1, 2, 2, 0, 16000, 0, 1, "Failed", new string('x', 64), null,
                    "None", "", null, 3, "candidate");
                overlay.SetScheduledCards(deck!.Update([failed], "candidate", 1, 1, true, now));
                Check("failure invalidates selected segment", overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "");
                var stale = failed with { SourceRevision = 1, AppliedSourceRevision = 1, SourceState = "Final",
                    TranslationState = "Done", Translation = "철회된 과거 자막.", TranslationRequestId = 1 };
                now = 2.7;
                overlay.SetScheduledCards(deck.Update([stale], "candidate", 1, 1, true, now));
            }
            else
            {
                overlay.InvalidateScheduled(1, true);
                now = 2.7;
                Check("withdrawn late input rejected", !overlay.SetScheduledInput(Input("철회된 과거 자막.", 3, now)).Accepted);
            }
            await Frame(2.7); await Frame(5.3);
            Check("withdrawn correction never reappears", overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "");

            Begin(); Submit(Input("이전 세션 앞문장.\n같은 뒷문장.")); await Frame(0);
            Submit(Input("이전 세션 교정.\n같은 뒷문장.", 2, .1)); await Frame(.1); await Frame(2.5);
            now = 2.6; overlay.ValidateSession("candidate", 1, 2, true);
            if (fromDeck)
            {
                var fresh = new HistoryRecord(1, 2, 1, 1, 1, 0, 16000, 0, 1, "Final", new string('x', 64), null,
                    "Done", "새 epoch 자막.", null, 1, "candidate");
                overlay.SetScheduledCards(deck!.Update([fresh], "candidate", 1, 2, true, now));
                now = 2.7;
                overlay.SetScheduledCards(deck.Update([fresh with { Epoch = 1, Translation = "지난 epoch 자막." }], "candidate", 1, 2, true, now));
            }
            else
            {
                overlay.BeginScheduledReading(new("candidate", 1, 2));
                Check("new epoch input accepted", overlay.SetScheduledInput(Input("새 epoch 자막.", at: now) with
                    { Identity = new("candidate", 1, 2, 1, 0) }).Accepted);
                now = 2.7;
                Check("old epoch input rejected", !overlay.SetScheduledInput(Input("지난 epoch 자막.", 3, now)).Accepted);
            }
            await Frame(2.7);
            Check("context transition shows only new epoch", overlay.ReadingState().Upper == "새 epoch 자막." && overlay.ReadingState().Lower == "");
            await Frame(5.2);
            Check("new epoch drains without old replay", overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "");

            Begin(); Submit(Input("이동 중 윗줄.\n이동 중 아랫줄.")); await Frame(0);
            var movingUpperRun = overlay.ReadingRuns().Upper[0];
            var movingLowerRun = overlay.ReadingRuns().Lower[0];
            var movingUpperLease = overlay.ScheduledStore!.UpperLease;
            var movingLowerLease = overlay.ScheduledStore!.LowerLease;
            var screen = overlay.Screens.Primary ?? throw new IOException("No primary screen for placement fixture");
            var area = new OverlayArea(screen.WorkingArea.X, screen.WorkingArea.Y, screen.WorkingArea.Width,
                screen.WorkingArea.Height, screen.Scaling, true);
            overlay.Position = new PixelPoint(-5000, -5000);
            double movingWidth = overlay.Width;
            overlay.Width = movingWidth + 1; await Task.Delay(80);
            overlay.Width = movingWidth; await Task.Delay(80);
            Check("native size notification recovers off-screen position", overlay.Position.X >= area.X && overlay.Position.Y >= area.Y);
            Check("removed screen placement recovers", overlay.RecoverPlacement([area]));
            await Frame(.5);
            Check("recovered window is inside work area", overlay.Position.X >= area.X && overlay.Position.Y >= area.Y &&
                overlay.Position.X + overlay.Bounds.Width * overlay.RenderScaling <= area.X + area.Width + 1 &&
                overlay.Position.Y + overlay.Bounds.Height * overlay.RenderScaling <= area.Y + area.Height + 1);
            Check("recovery preserves both text Runs", ReferenceEquals(movingUpperRun, overlay.ReadingRuns().Upper[0]) &&
                ReferenceEquals(movingLowerRun, overlay.ReadingRuns().Lower[0]));
            Check("recovery preserves reading deadlines", movingUpperLease == overlay.ScheduledStore!.UpperLease && movingLowerLease == overlay.ScheduledStore.LowerLease);
            overlay.RecoverPlacement([area]); await Frame(.6);
            Check("repeated recovery preserves visible text", overlay.ReadingState().Upper == "이동 중 윗줄." && overlay.ReadingState().Lower == "이동 중 아랫줄.");
            await Frame(2.5); Check("original upper expiry remains", overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "이동 중 아랫줄.");
            await Frame(2.75); Check("original lower expiry remains", overlay.ReadingState().Lower == "");

            Begin(); Submit(Input("👨‍👩‍👧‍👦 가족이 함께 있습니다.")); await Frame(0);
            overlay.ClearReadingLines(); await Frame(.1); await Frame(5);
            Check("stop has no replay", overlay.ReadingState().Upper == "" && overlay.ReadingState().Lower == "");
        }
        catch (Exception exception) { error = exception.ToString(); failures++; }
        finally
        {
            bool passed = failures == 0 && frames.Count > 0;
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                passed, cases = run, checks, frames, error,
                scope = fromDeck ? "Synthetic HistoryRecord through presentation/deck/adapter, real Avalonia snapshots; no live worker/audio"
                    : "Explicit candidate inputs, real Avalonia saved snapshots, virtual clock; no live worker/audio or continuous compositor frames"
            }, CaptionRegression.JsonOptions));
            overlay?.Close(); desktop.Shutdown(passed ? 0 : 1);
        }
    }
}
