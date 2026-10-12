using System.Text;
using System.Text.Json;

namespace EchoSub.Desktop;

// Fixed regression inputs shared by the state runner and the real overlay probe.
// The clock advances without waiting for speech, models, or user playback.
public sealed record CaptionRegressionState(string Upper, string Lower,
    CaptionIdentity? UpperIdentity, CaptionIdentity? LowerIdentity);
public sealed record CaptionRegressionDriver(Action Clear, Action<CaptionCards, double> Apply,
    Action<double> Tick, Func<CaptionRegressionState> Read,
    Action<Action<CaptionReadingObservation>> Observe);
public sealed record CaptionRegressionFailure(string Rule, double AtSeconds, string Detail);
public sealed record CaptionRegressionCaseResult(string Name, bool Passed, int Checks,
    int EarlyRemovals, int PendingPeak, int EmittedCharacters, double? FirstLineWaitP95Seconds,
    double? FirstLineWaitMaxSeconds, IReadOnlyDictionary<string, int> Observations,
    IReadOnlyDictionary<string, int> Drops, IReadOnlyList<CaptionRegressionFailure> Failures);
public sealed record CaptionRegressionResult(bool Passed, string Scope, int Checks,
    IReadOnlyList<CaptionRegressionCaseResult> Cases, IReadOnlyList<string> Skipped);

public static class CaptionRegression
{
    public const string SuiteVersion = "caption-regression-v1";
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true
    };
    private sealed record Input(double At, HistoryRecord? Record = null, bool Stop = false);
    private sealed record Expected(double At, string Upper, string Lower);
    private sealed record Scenario(string Name, Input[] Inputs, Expected[] Expected,
        string[] Required, string[] Forbidden, double End = 6, double MaximumWait = 3,
        int CapacityDrops = 0, bool Render = true);

    public static CaptionRegressionDriver StateDriver(bool negativeControl = false)
    {
        var lines = new CaptionLines();
        int Fit(string text) => text.IndexOfAny(['\r', '\n']) is var end && end >= 0 ?
            end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1) : Math.Min(32, text.Length);
        return new(lines.Clear, (cards, now) =>
        {
            // Deliberate old defect. The automation must reject this control.
            if (negativeControl) cards = cards with
            {
                InvalidatedSegments = cards.InvalidatedSegments.Concat(cards.RevisedSegments).Distinct().ToArray(),
                RevisedSegments = []
            };
            lines.Apply(cards, now, Fit);
        }, now => lines.Tick(now, Fit),
            () => new(lines.Upper ?? "", lines.Lower ?? "", lines.UpperIdentity, lines.LowerIdentity),
            observer => lines.Observed += observer);
    }

    public static async Task<CaptionRegressionResult> RunAsync(CaptionRegressionDriver driver, bool renderOnly = false,
        Func<int, double, CaptionRegressionState, Task>? capture = null)
    {
        var results = new List<CaptionRegressionCaseResult>();
        var skipped = new List<string>();
        List<CaptionReadingObservation>? observations = null;
        driver.Observe(item => observations?.Add(item));
        var scenarios = Scenarios();
        for (int caseIndex = 0; caseIndex < scenarios.Length; caseIndex++)
        {
            var scenario = scenarios[caseIndex];
            if (renderOnly && !scenario.Render) { skipped.Add(scenario.Name); continue; }
            observations = [];
            var failures = new List<CaptionRegressionFailure>();
            var checks = 0; var early = 0; var peak = 0;
            var emitted = new StringBuilder();
            var changedAt = new double[2];
            var previous = new CaptionRegressionState("", "", null, null);
            void Check(bool passed, string rule, double at, string detail = "")
            {
                checks++;
                if (!passed && failures.Count < 100) failures.Add(new(rule, at, detail));
            }
            var deck = new CaptionDeck { LineReadingManaged = true };
            var product = "caption-regression-" + caseIndex;
            double now = 0; ulong epoch = 1;
            try
            {
                driver.Clear();
                var times = new SortedSet<double>(Enumerable.Range(0, (int)Math.Ceiling(scenario.End * 10) + 1)
                    .Select(i => i / 10.0));
                foreach (var item in scenario.Inputs) times.Add(item.At);
                foreach (var item in scenario.Expected) times.Add(item.At);
                var inputIndex = 0;
                foreach (var time in times)
                {
                    now = time;
                    var hardSegments = new HashSet<ulong>();
                    var reset = false; var hadInput = false;
                    while (inputIndex < scenario.Inputs.Length && scenario.Inputs[inputIndex].At <= now)
                    {
                        var input = scenario.Inputs[inputIndex++]; hadInput = true;
                        if (input.Stop)
                        {
                            driver.Apply(deck.Update([], product, 1, epoch, false, now), now);
                            reset = true;
                        }
                        else
                        {
                            var record = input.Record! with { ProductSessionId = product };
                            if (record.Epoch != epoch) { reset = true; epoch = record.Epoch; }
                            if (record.SourceState is "Failed" or "Discarded" or "Skipped") hardSegments.Add(record.SegmentId);
                            driver.Apply(deck.Update([record], product, 1, epoch, true, now), now);
                        }
                    }
                    driver.Apply(deck.Tick(now), now); driver.Tick(now);
                    var current = driver.Read();
                    var oldText = new[] { previous.Upper, previous.Lower };
                    var nextText = new[] { current.Upper, current.Lower };
                    var oldKeys = new[] { previous.UpperIdentity, previous.LowerIdentity };
                    var nextKeys = new[] { current.UpperIdentity, current.LowerIdentity };
                    for (int slot = 0; slot < 2; slot++)
                    {
                        if (oldText[slot] == nextText[slot] && oldKeys[slot] == nextKeys[slot]) continue;
                        var append = oldText[slot].Length > 0 && oldKeys[slot] == nextKeys[slot] &&
                            nextText[slot].StartsWith(oldText[slot], StringComparison.Ordinal);
                        if (oldText[slot].Length > 0 && !append && !reset &&
                            !(oldKeys[slot] is { } key && hardSegments.Contains(key.Segment)))
                        {
                            var protectedTime = now - changedAt[slot];
                            if (protectedTime < 2.5 - .000001) early++;
                            Check(protectedTime >= 2.5 - .000001, "reading_deadline", now,
                                $"slot={slot}; visible_s={protectedTime:F6}");
                        }
                        if (nextText[slot].Length > 0)
                        {
                            emitted.Append(append ? nextText[slot][oldText[slot].Length..] : nextText[slot]);
                            changedAt[slot] = now;
                        }
                    }
                    Check(!(current.UpperIdentity is { } upper && current.LowerIdentity is { } lower) ||
                        upper.Segment <= lower.Segment, "segment_order", now);
                    var expected = scenario.Expected.FirstOrDefault(item => item.At == now);
                    if (expected is not null)
                    {
                        Check(current.Upper == expected.Upper, "expected_upper", now, $"expected={expected.Upper}; actual={current.Upper}");
                        Check(current.Lower == expected.Lower, "expected_lower", now, $"expected={expected.Lower}; actual={current.Lower}");
                    }
                    if (capture is not null && (hadInput || expected is not null || current != previous))
                        await capture(caseIndex, now, current);
                    previous = current;
                }
                var text = emitted.ToString();
                foreach (var token in scenario.Required)
                    Check(Occurrences(text, token) == 1, "required_target_once", now, token);
                foreach (var token in scenario.Forbidden)
                    Check(!text.Contains(token, StringComparison.Ordinal), "obsolete_target_absent", now, token);
                Check(previous.Upper.Length == 0 && previous.Lower.Length == 0, "eventual_drain", now);
                peak = observations.Count == 0 ? 0 : observations.Max(item => item.PendingUnits);
                Check(peak <= 4, "bounded_pending", now, peak.ToString());
                var drops = observations.Where(item => item.Kind == "Dropped").ToArray();
                Check(drops.Length == scenario.CapacityDrops && drops.All(item => item.Reason == "QueueCapacity"),
                    "explicit_drop_policy", now, string.Join(",", drops.Select(item => item.Reason)));
                var waits = observations.Where(item => item.Kind == "FirstLine").Select(item => item.WaitSeconds).ToArray();
                Check(waits.All(wait => wait <= scenario.MaximumWait), "first_line_wait_budget", now,
                    $"limit_s={scenario.MaximumWait}; max_s={waits.DefaultIfEmpty(0).Max():F6}");
            }
            catch (Exception error) { Check(false, "execution", now, error.ToString()); }
            var readingWaits = observations.Where(item => item.Kind == "FirstLine").Select(item => item.WaitSeconds).Order().ToArray();
            results.Add(new(scenario.Name, failures.Count == 0, checks, early, peak, emitted.Length,
                readingWaits.Length == 0 ? null : readingWaits[(int)Math.Ceiling(readingWaits.Length * .95) - 1],
                readingWaits.Length == 0 ? null : readingWaits[^1],
                observations.GroupBy(item => item.Kind).ToDictionary(group => group.Key, group => group.Count()),
                observations.Where(item => item.Kind == "Dropped").GroupBy(item => item.Reason ?? "Unreported")
                    .ToDictionary(group => group.Key, group => group.Count()), failures));
            observations = null;
        }
        return new(results.Count > 0 && results.All(item => item.Passed),
            renderOnly ? "Synthetic fixed inputs through production deck and Avalonia overlay; virtual clock" :
                "Synthetic fixed inputs through production deck and lines; virtual clock, no rendered screen",
            results.Sum(item => item.Checks), results, skipped);
    }

    private static int Occurrences(string text, string token)
    {
        int count = 0, start = 0;
        while ((start = text.IndexOf(token, start, StringComparison.Ordinal)) >= 0) { count++; start += token.Length; }
        return count;
    }

    private static Scenario[] Scenarios()
    {
        HistoryRecord Record(string target, ulong segment = 1, ulong revision = 1, string source = "Wait here.", bool preview = false) =>
            new(1, 1, segment, revision, revision, 0, 16000, 0, 1, preview ? "Partial" : "Final", source,
                null, "Done", target, null, revision, StableSource: preview ? source : "", TranslationIsPreview: preview,
                TranslationSource: preview ? source : "", TranslationPrefix: preview ? source : "");
        HistoryRecord Pending(string source, ulong revision) => Record("", revision: revision, source: source, preview: true) with
            { TranslationState = "Pending", TranslationRequestId = null };
        var first = Record("기다려.\n왼쪽.", source: "Wait here. Take the left path.", preview: true);
        var right = Record("기다려.\n오른쪽.", revision: 3, source: "Wait here. Take the right path.");
        var result = new List<Scenario>
        {
            new("source-revision-at-0.064s", [new(0, first), new(.064, Pending(right.Source, 2)), new(.2, right)],
                [new(.064, "기다려.", "왼쪽."), new(.2, "기다려.", "왼쪽."), new(2.49, "기다려.", "왼쪽."),
                 new(2.75, "오른쪽.", "")], ["기다려.", "오른쪽."], []),
            new("repeated-source-revisions", [new(0, first), new(.064, Pending(right.Source, 2)),
                new(.2, right with { SourceState = "Partial", TranslationIsPreview = true, StableSource = right.Source,
                    TranslationSource = right.Source, TranslationPrefix = right.Source }),
                new(.3, Pending("Wait here. Stop.", 4)), new(.4, Record("기다려.\n멈춰.", revision: 5, source: "Wait here. Stop."))],
                [new(.4, "기다려.", "왼쪽."), new(2.49, "기다려.", "왼쪽."), new(2.75, "멈춰.", "")],
                ["기다려.", "멈춰."], ["오른쪽."]),
            new("revised-source-with-growing-target", [new(0, Record("대기", source: "Wait.", preview: true)),
                new(.064, Pending("Stay here.", 2)), new(.2, Record("대기 중", revision: 3, source: "Stay here."))],
                [new(.2, "대기", ""), new(2.49, "대기", ""), new(2.5, "대기 중", "")], ["대기 중"], []),
            new("rollback-discards-unread-suffix", [new(0, first), new(.1, first with { SourceRevision = 2,
                AppliedSourceRevision = 2, TranslationRequestId = 2, Source = first.Source + " Next.",
                StableSource = first.Source + " Next.", TranslationPrefix = first.Source + " Next.",
                TranslationSource = "Next.", Translation = "폐기할 꼬리." }), new(.2, right)],
                [new(.2, "기다려.", "왼쪽."), new(2.75, "오른쪽.", "")], ["오른쪽."], ["폐기할 꼬리."]),
            new("revision-preserves-other-segments", [new(0, first), new(.1, Record("둘째.", 2)),
                new(.2, Pending(right.Source, 2)), new(.3, Record("셋째.", 3))],
                [new(.3, "기다려.", "왼쪽."), new(2.75, "둘째.", "셋째.")], ["둘째.", "셋째."], []),
            new("stop-clears-without-replay", [new(0, first), new(.064, Stop: true)],
                [new(.064, "", ""), new(3, "", "")], [], []),
            new("epoch-replaces-old-session", [new(0, first), new(.064, Record("새 세션.") with { Epoch = 2 })],
                [new(.064, "새 세션.", "")], ["새 세션."], []),
            new("long-target-tail-is-not-lost", [new(0, Record("줄1.\n줄2.\n줄3.\n줄4.\n줄5.\n줄6.")), new(.1, Record("다음.", 2))],
                [new(8.4, "다음.", "")], ["줄1.", "줄2.", "줄3.", "줄4.", "줄5.", "줄6.", "다음."], [], End: 12, MaximumWait: 8.3),
            new("separator-retains-deadline", [new(0, Record("첫 줄\n둘째 줄")), new(.1, Record("다음.", 2)),
                new(2, Record("첫 줄\n둘째 줄\n", revision: 2))],
                [new(2.49, "첫 줄", "둘째 줄"), new(2.75, "다음.", "")], ["다음."], []),
            new("open-line-appends-without-replacement", [new(0, Record("대", preview: true)),
                new(.1, Record("대기", revision: 2, preview: true)), new(.2, Record("대기 중", revision: 3, preview: true))],
                [new(.1, "대기", ""), new(.2, "대기 중", ""), new(2.69, "대기 중", "")], ["대기 중"], []),
            new("unicode-is-preserved", [new(0, Record("👩‍💻 한글."))], [new(.1, "👩‍💻 한글.", "")], ["👩‍💻 한글."], [])
        };
        foreach (var status in new[] { "Failed", "Discarded", "Skipped" })
            result.Add(new("terminal-" + status.ToLowerInvariant(), [new(0, first), new(.064, first with
                { SourceRevision = 2, AppliedSourceRevision = 2, SourceState = status, TranslationState = "None", Translation = "" })],
                [new(.064, "", "")], [], []));
        result.Add(new("bounded-overload-is-explicit", [new(0, Record("고정.\n읽기.")),
            .. Enumerable.Range(2, 6).Select(index => new Input((index - 1) / 10.0, Record($"문장{index}.", (ulong)index)))],
            [], ["문장4.", "문장5.", "문장6.", "문장7."], ["문장2.", "문장3."], End: 12, MaximumWait: 8, CapacityDrops: 2));
        result.Add(new("30-minute-virtual-session", Enumerable.Range(1, 600)
            .Select(index => new Input((index - 1) * 3.0, Record($"#{index:D4}.", (ulong)index))).ToArray(),
            [], Enumerable.Range(1, 600).Select(index => $"#{index:D4}.").ToArray(), [], End: 1800, MaximumWait: 0, Render: false));
        return result.ToArray();
    }
}
