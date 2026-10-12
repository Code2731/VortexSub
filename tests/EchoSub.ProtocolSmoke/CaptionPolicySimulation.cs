using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EchoSub.Desktop;

// Research only. Production receives none of these policies or settings.
// Inputs enter after CaptionDeck's ordered delivery, not at the audio/model boundary.
internal static class CaptionPolicySimulation
{
    private sealed record Input(double At, ulong Segment = 1, string? Text = null,
        bool Final = false, ulong Revision = 1, int UnitStart = 0, string Kind = "target", ulong Epoch = 1);
    private sealed record Required(ulong Segment, string Text, double Since = 0, string? IfPreviouslyShown = null,
        string Kind = "correction");
    private sealed record Trace(string Name, Input[] Inputs, Required[] Required, double End = 12,
        string[]? Forbidden = null, bool Overload = false, string Group = "development",
        Dictionary<string, int>? ExpectedDrops = null);
    private sealed record Frame(double At, string Upper, string Lower, CaptionIdentity? UpperId, CaptionIdentity? LowerId,
        double UpperDeadline = 0, double LowerDeadline = 0);
    private sealed record Emission(double At, CaptionIdentity? Id, int Slot, string Text);
    private sealed record Stats(double? Median, double? P95, double? Maximum, int Samples);
    private sealed record Comparison(string Policy, string Trace, bool FunctionalGate,
        double? FirstWaitP95DeltaSeconds, double? UpdateWaitP95DeltaSeconds, int EmittedCharactersDelta,
        int RepeatCharactersDelta, bool LatencyRegression, bool Overload);
    private sealed record Result(string Policy, string Trace, string Group, bool Passed, string[] Failures,
        Stats FirstLineWaitSeconds, Stats RequiredUpdateWaitSeconds, Stats CorrectionWaitSeconds, int EarlyRemovals, int Moves,
        int RepeatedLineCharacters, int EmittedCharacters, int PendingPeak, int HistoryPeak,
        int CandidateErasedUtf16Units, Dictionary<string, int> Drops, string[] Missing, string[] AccountedUnshown,
        List<Emission> Emissions, List<Frame> Frames);

    private static CaptionIdentity Key(Input input) => new("simulation", 1, input.Epoch, input.Segment, input.UnitStart);
    private static CaptionCard Card(Input input) => new("fixed source unit", input.Text, !input.Final, "fixed source unit")
    { Identity = Key(input), SourceRevision = input.Revision, TranslationRequestId = input.Revision };
    private static CaptionCards Cards(Input input) => new(null, null)
    {
        OrderedDelivery = true, ResetReading = input.Kind == "reset",
        RevisedSegments = input.Kind == "revise" ? [input.Segment] : [],
        InvalidatedSegments = input.Kind == "withdraw" ? [input.Segment] : [],
        Delivered = input.Kind == "target" ? [Card(input)] : []
    };
    private static int Fit(string text)
    {
        var newline = text.IndexOfAny(['\r', '\n']);
        var starts = StringInfo.ParseCombiningCharacters(text);
        var width = starts.Length > 32 ? starts[32] : text.Length;
        return newline >= 0 && newline < width ? newline +
            (text[newline] == '\r' && newline + 1 < text.Length && text[newline + 1] == '\n' ? 2 : 1) : width;
    }
    private interface IPolicy
    {
        void Apply(Input input);
        void Tick(double now);
        Frame Read(double now);
        int Pending { get; }
        int History { get; }
        Dictionary<string, int> Drops { get; }
        HashSet<ulong> DroppedSegments { get; }
    }
    private sealed class CurrentPolicy : IPolicy
    {
        private readonly CaptionLines lines = new();
        // Read-only research instrumentation catches expire-and-reassign of identical text
        // inside one Tick. Text-only snapshots cannot distinguish that from continued exposure.
        // Fail closed if the hashed production state no longer exposes this field.
        private readonly System.Reflection.FieldInfo deadlineField = typeof(CaptionLines).GetField("expires",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("Control deadline instrumentation is unavailable");
        public CurrentPolicy() => lines.Observed += observation =>
        {
            if (observation.Kind != "Dropped") return;
            var reason = observation.Reason ?? "unknown";
            Drops[reason] = Drops.GetValueOrDefault(reason) + 1;
            if (observation.Identity is { } id) DroppedSegments.Add(id.Segment);
        };
        public Dictionary<string, int> Drops { get; } = [];
        public HashSet<ulong> DroppedSegments { get; } = [];
        public int Pending => lines.PendingUnits;
        public int History => 0; // No separate comparison ledger in the current policy.
        public void Apply(Input input) => lines.Apply(Cards(input), input.At, Fit);
        public void Tick(double now) => lines.Tick(now, Fit);
        public Frame Read(double now)
        {
            var deadlines = (double[])deadlineField.GetValue(lines)!;
            return new(now, lines.Upper ?? "", lines.Lower ?? "", lines.UpperIdentity, lines.LowerIdentity,
                deadlines[0], deadlines[1]);
        }
    }

    private sealed class Unit
    {
        public List<string> Shown = [];
        public string Previous = "";
        public ulong Revision;
        public bool Started;
    }
    private sealed record Piece(string Text, bool Append = false);
    private sealed class Work(CaptionIdentity key, List<Piece> pieces, List<string> snapshot, double since, bool correction)
    {
        public CaptionIdentity Key = key;
        public List<Piece> Pieces = pieces;
        public List<string> Snapshot = snapshot;
        public double Since = since;
        public bool Correction = correction;
        public bool Admitted;
        public int Index;
        public int Offset;
    }
    private sealed class CandidatePolicy(int mode, bool breakReading = false, bool loseFinal = false,
        bool reuseSentences = true, bool allowFreeLower = true) : IPolicy
    {
        // Same active + four waiting unit limit as the control. Each snapshot is bounded.
        private readonly List<Work> queue = [];
        private readonly Dictionary<CaptionIdentity, Unit> history = [];
        private readonly string[] rows = ["", ""];
        private readonly CaptionIdentity?[] keys = new CaptionIdentity?[2];
        private readonly double[] deadlines = new double[2];
        private (string? Product, ulong Session, ulong Epoch)? context;
        private ulong retiredThrough;
        private ulong admittedThrough;
        public Dictionary<string, int> Drops { get; } = [];
        public HashSet<ulong> DroppedSegments { get; } = [];
        public int Pending => queue.Count - (queue.Count > 0 && queue[0].Admitted ? 1 : 0);
        public int History => history.Count;
        private void Drop(string reason, CaptionIdentity key)
        {
            Drops[reason] = Drops.GetValueOrDefault(reason) + 1;
            DroppedSegments.Add(key.Segment);
        }
        private void Clear()
        {
            queue.Clear(); history.Clear(); Array.Clear(rows); Array.Clear(keys); Array.Clear(deadlines);
            for (int i = 0; i < 2; i++) rows[i] = "";
            retiredThrough = 0; admittedThrough = 0;
        }
        public void Apply(Input input)
        {
            var key = Key(input);
            var nextContext = (key.ProductSession, key.Session, key.Epoch);
            if (context != nextContext || input.Kind == "reset") { Clear(); context = nextContext; }
            Expire(input.At);
            if (input.Kind is "revise" or "withdraw")
            {
                queue.RemoveAll(work => work.Key.Segment == input.Segment);
                if (input.Kind == "withdraw")
                {
                    foreach (var id in history.Keys.Where(id => id.Segment == input.Segment).ToArray()) history.Remove(id);
                    for (int slot = 0; slot < 2; slot++)
                        if (keys[slot]?.Segment == input.Segment) { rows[slot] = ""; keys[slot] = null; }
                }
                return;
            }
            if (input.Kind != "target" || loseFinal && input.Final) return;
            var text = input.Text ?? "";
            if (text.Length > CaptionLines.MaximumInputCharacters) { Drop("InputTooLong", key); return; }
            if (key.Segment <= retiredThrough || key.Segment < admittedThrough) { Drop("LateUnit", key); return; }
            // Do not emit a superseded unread tail before applying its revision.
            // Independent queued units may advance before adding a new unit, as in the control.
            if (!history.ContainsKey(key)) Tick(input.At);
            if (!history.TryGetValue(key, out var unit))
            {
                while (history.Count >= 64)
                {
                    var retired = history.Keys.Where(id => !queue.Any(work => work.Key == id) && !keys.Contains(id))
                        .OrderBy(id => id.Segment).FirstOrDefault();
                    if (retired is null) { Drop("HistoryCapacity", key); return; }
                    history.Remove(retired); retiredThrough = Math.Max(retiredThrough, retired.Segment);
                }
                unit = new(); history.Add(key, unit);
            }
            if (input.Revision < unit.Revision) return;
            unit.Revision = input.Revision;
            queue.RemoveAll(work => work.Key.Segment == key.Segment && work.Key.UnitStart > key.UnitStart);
            var chunks = Split(text);
            var previous = Split(unit.Previous);
            unit.Previous = text;
            var accepted = chunks;
            if (!input.Final && mode == 2 && unit.Started)
            {
                int common = 0;
                while (common < previous.Count && common < chunks.Count && previous[common] == chunks[common]) common++;
                accepted = chunks.Take(common).ToList();
            }
            if (!input.Final && mode == 3 && chunks.Count > 0 && !Closed(chunks[^1]))
            {
                var starts = StringInfo.ParseCombiningCharacters(chunks[^1]);
                // Six graphemes, not six model tokens. A distinct research control.
                if (starts.Length > 6)
                    accepted = [.. chunks.Take(chunks.Count - 1), chunks[^1][..starts[^6]]];
                else accepted = chunks.Take(chunks.Count - 1).ToList();
            }
            // Preserve the first meaningful clause in the agreement policy.
            if (mode == 2 && !unit.Started && accepted.Count > 1 && !input.Final) accepted = [accepted[0]];
            var shown = unit.Shown;
            var pieces = new List<Piece>();
            bool correction = false;
            bool overlap = history.Keys.Any(id => id.Segment == key.Segment && id != key);
            bool ambiguous = shown.Count > 64 || accepted.Count > 64 ||
                shown.Distinct(StringComparer.Ordinal).Count() != shown.Count ||
                accepted.Distinct(StringComparer.Ordinal).Count() != accepted.Count ||
                previous.Distinct(StringComparer.Ordinal).Count() != previous.Count || overlap;
            if (accepted.SequenceEqual(shown)) { queue.RemoveAll(work => work.Key == key); return; }
            if (accepted.Count == 0 && !input.Final) { queue.RemoveAll(work => work.Key == key); return; }
            bool extension = !ambiguous && shown.Count > 0 && accepted.Count >= shown.Count &&
                shown.Take(shown.Count - 1).SequenceEqual(accepted.Take(shown.Count - 1)) &&
                accepted[shown.Count - 1].StartsWith(shown[^1], StringComparison.Ordinal);
            if (extension)
            {
                var delta = accepted[shown.Count - 1][shown[^1].Length..];
                if (delta.Length > 0) pieces.Add(new(delta, true));
                pieces.AddRange(accepted.Skip(shown.Count).Select(chunk => new Piece(chunk)));
            }
            else
            {
                // Shortened finals and ambiguous repeated/overlapping clauses replay the whole correction.
                var reused = reuseSentences && !ambiguous && accepted.Count >= shown.Count ? Lcs(shown, accepted) : [];
                correction = shown.Count > 0 && reused.Count < shown.Count;
                pieces.AddRange(accepted.Where((_, index) => !reused.Contains(index)).Select(chunk => new Piece(chunk)));
            }
            if (breakReading && correction) { rows[0] = rows[1] = ""; Array.Clear(keys); }
            var index = queue.FindIndex(work => work.Key == key);
            var since = index >= 0 ? queue[index].Since : input.At;
            if (index >= 0) queue.RemoveAt(index);
            // An empty final is an explicit unsupported deletion; do not report silent success.
            if (pieces.Count == 0) { unit.Shown = accepted; return; }
            if (Pending >= CaptionLines.MaximumPendingUnits)
            {
                int victim = queue[0].Admitted ? 1 : 0;
                Drop("QueueCapacity", queue[victim].Key); queue.RemoveAt(victim);
            }
            var work = new Work(key, pieces, accepted, since, correction);
            if (index >= 0) queue.Insert(Math.Min(index, queue.Count), work); else queue.Add(work);
            Tick(input.At);
        }
        public void Tick(double now)
        {
            Expire(now);
            for (int i = queue.Count - 1; i >= 0; i--)
                if (now - queue[i].Since > CaptionLines.MaximumPendingSeconds)
                {
                    if (queue[i].Admitted) continue;
                    Drop("QueueAge", queue[i].Key); queue.RemoveAt(i);
                }
            if (queue.Count > 1 && queue[0].Admitted && rows.All(row => row.Length == 0) &&
                now - queue[0].Since > CaptionLines.MaximumPendingSeconds)
            {
                var active = queue[0];
                var tail = active.Pieces[active.Index].Text[active.Offset..] +
                    string.Concat(active.Pieces.Skip(active.Index + 1).Select(piece => piece.Text));
                if (Fit(tail) < tail.Length) { Drop("UnreadTailAge", active.Key); queue.RemoveAt(0); }
            }
            for (int pass = 0; pass < 6 && queue.Count > 0; pass++)
            {
                var work = queue[0];
                if (work.Correction && keys.Any(key => key == work.Key)) return;
                if (!allowFreeLower && keys.Any(key => key is not null && key != work.Key)) return;
                int slot = rows[0].Length == 0 && rows[1].Length == 0 ? 0 : rows[1].Length == 0 ? 1 : -1;
                var piece = work.Pieces[work.Index];
                if (piece.Append && work.Offset == 0)
                {
                    int open = keys[1] == work.Key ? 1 : keys[0] == work.Key ? 0 : -1;
                    if (open >= 0 && Fit(rows[open] + piece.Text) == rows[open].Length + piece.Text.Length) slot = open;
                }
                if (slot < 0) return;
                var tail = piece.Text[work.Offset..];
                int length = Fit(tail);
                if (length <= 0) throw new InvalidOperationException("Non-progressing line fit");
                var fragment = tail[..length].TrimEnd('\r', '\n');
                bool append = piece.Append && work.Offset == 0 && keys[slot] == work.Key;
                rows[slot] = append ? rows[slot] + fragment : fragment;
                keys[slot] = work.Key;
                work.Admitted = true;
                admittedThrough = Math.Max(admittedThrough, work.Key.Segment);
                work.Correction = false; // The old related lines have drained; this job can use both slots.
                deadlines[slot] = now + CaptionLines.HoldSeconds;
                if (!append && slot == 1 && rows[0].Length > 0)
                    deadlines[slot] = Math.Max(deadlines[slot], deadlines[0] + CaptionLines.LineStaggerSeconds);
                work.Offset += length;
                history[work.Key].Started = true;
                if (work.Offset == piece.Text.Length)
                {
                    history[work.Key].Shown.Add(piece.Text);
                    work.Index++; work.Offset = 0;
                    if (work.Index == work.Pieces.Count) { history[work.Key].Shown = work.Snapshot; queue.RemoveAt(0); }
                }
            }
        }
        private void Expire(double now)
        {
            for (int slot = 0; slot < 2; slot++)
                if (rows[slot].Length > 0 && now + 1e-8 >= deadlines[slot]) { rows[slot] = ""; keys[slot] = null; }
        }
        public Frame Read(double now) => new(now, rows[0], rows[1], keys[0], keys[1], deadlines[0], deadlines[1]);
    }

    private static bool Closed(string text) => text.TrimEnd().LastOrDefault() is '.' or '!' or '?' or '。' or '！' or '？';
    private static List<string> Split(string text)
    {
        var chunks = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            bool boundary = text[i] is '\r' or '\n' or '!' or '?' or '。' or '！' or '？';
            if (text[i] == '.' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                int wordStart = i;
                while (wordStart > 0 && char.IsLetter(text[wordStart - 1])) wordStart--;
                var word = text[wordStart..i].ToLowerInvariant();
                boundary = !(word.Length == 1 && word[0] is >= 'a' and <= 'z' ||
                    word is "mr" or "mrs" or "ms" or "dr" or "prof" or "vs" || i > 0 && text[i - 1] == '.');
            }
            if (!boundary) continue;
            var chunk = text[start..(i + 1)].Trim();
            if (chunk.Length > 0) chunks.Add(chunk);
            start = i + 1;
            if (chunks.Count > 64) return [text]; // No unbounded LCS matrix.
        }
        var tail = text[start..].Trim();
        if (tail.Length > 0) chunks.Add(tail);
        return chunks;
    }
    private static HashSet<int> Lcs(List<string> previous, List<string> incoming)
    {
        if (previous.Count > 64 || incoming.Count > 64) return [];
        var table = new int[previous.Count + 1, incoming.Count + 1];
        for (int i = previous.Count - 1; i >= 0; i--)
            for (int j = incoming.Count - 1; j >= 0; j--)
                table[i, j] = previous[i] == incoming[j] ? 1 + table[i + 1, j + 1] : Math.Max(table[i + 1, j], table[i, j + 1]);
        var result = new HashSet<int>();
        int a = 0, b = 0;
        while (a < previous.Count && b < incoming.Count)
            if (previous[a] == incoming[b]) { result.Add(b); a++; b++; }
            else if (table[a + 1, b] >= table[a, b + 1]) a++; else b++;
        return result;
    }
    private static string Flat(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    private static Stats Distribution(IEnumerable<double> input)
    {
        var values = input.Order().ToArray();
        if (values.Length == 0) return new(null, null, null, 0);
        return new(Math.Round((values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2, 4),
            Math.Round(values[(int)Math.Ceiling(values.Length * .95) - 1], 4), Math.Round(values[^1], 4), values.Length);
    }
    private static Result Replay(Trace trace, string name, IPolicy policy)
    {
        var failures = new List<string>();
        var emissions = new List<Emission>();
        var frames = new List<Frame>();
        var deadlines = new double[2];
        var old = new Frame(0, "", "", null, null);
        int early = 0, moves = 0, pending = 0, history = 0;
        var times = new SortedSet<double>(Enumerable.Range(0, (int)(trace.End * 10) + 1).Select(i => i / 10d));
        foreach (var input in trace.Inputs) times.Add(input.At);
        foreach (var now in times)
        {
            var inputs = trace.Inputs.Where(input => input.At == now).ToArray();
            foreach (var input in inputs) policy.Apply(input);
            policy.Tick(now);
            var next = policy.Read(now);
            string[] before = [old.Upper, old.Lower], after = [next.Upper, next.Lower];
            CaptionIdentity?[] beforeId = [old.UpperId, old.LowerId], afterId = [next.UpperId, next.LowerId];
            double[] oldDeadline = [old.UpperDeadline, old.LowerDeadline], newDeadline = [next.UpperDeadline, next.LowerDeadline];
            for (int slot = 0; slot < 2; slot++)
            {
                bool exempt = inputs.Any(input => input.Kind == "reset" || input.Epoch != beforeId[slot]?.Epoch ||
                    input.Kind == "withdraw" && input.Segment == beforeId[slot]?.Segment);
                bool append = beforeId[slot] == afterId[slot] && after[slot].StartsWith(before[slot], StringComparison.Ordinal);
                bool reassigned = before[slot].Length > 0 && now + 1e-8 >= oldDeadline[slot] && newDeadline[slot] > oldDeadline[slot] + 1e-8;
                if (before[slot].Length > 0 && !append && now + 1e-8 < deadlines[slot] && !exempt) early++;
                if (before[slot].Length > 0 && now + 1e-8 < deadlines[slot] && !append &&
                    before[1 - slot] != before[slot] && after[1 - slot] == before[slot] &&
                    afterId[1 - slot] == beforeId[slot] && !exempt) moves++;
                if (after[slot].Length > 0 && (after[slot] != before[slot] || afterId[slot] != beforeId[slot] || reassigned))
                {
                    emissions.Add(new(now, afterId[slot], slot, append && !reassigned ? after[slot][before[slot].Length..] : after[slot]));
                    deadlines[slot] = now + CaptionLines.HoldSeconds;
                }
            }
            if (next != old with { At = now }) frames.Add(next);
            old = next;
            pending = Math.Max(pending, policy.Pending); history = Math.Max(history, policy.History);
            if (policy.Pending > 4 || policy.History > 64) failures.Add("state_bound");
        }
        var missing = new List<string>();
        var accounted = new List<string>();
        var updateWaits = new List<double>();
        var correctionWaits = new List<double>();
        foreach (var required in trace.Required)
        {
            if (required.IfPreviouslyShown is { } prerequisite &&
                !Flat(string.Concat(emissions.Where(e => e.Id?.Segment == required.Segment && e.At < required.Since).Select(e => e.Text)))
                    .Contains(Flat(prerequisite), StringComparison.Ordinal)) continue;
            var text = ""; double? found = null;
            foreach (var emission in emissions.Where(e => e.Id?.Segment == required.Segment && e.At + 1e-8 >= required.Since))
            {
                text += Flat(emission.Text);
                if (text.Contains(Flat(required.Text), StringComparison.Ordinal)) { found = emission.At; break; }
            }
            if (found is null)
            {
                missing.Add(required.Text);
                if (trace.Overload && policy.DroppedSegments.Contains(required.Segment)) accounted.Add(required.Text);
            }
            if (required.Since > 0 && found is not null)
            {
                updateWaits.Add(found.Value - required.Since);
                if (required.Kind == "correction") correctionWaits.Add(found.Value - required.Since);
            }
        }
        if (early > 0) failures.Add("reading_deadline");
        if (moves > 0) failures.Add("slot_moved");
        if (missing.Count > accounted.Count) failures.Add("required_output_missing");
        if (!trace.Overload && policy.Drops.Count > 0 && trace.ExpectedDrops is null) failures.Add("unexpected_drop");
        if (trace.ExpectedDrops is { } expectedDrops &&
            (!expectedDrops.OrderBy(item => item.Key).SequenceEqual(policy.Drops.OrderBy(item => item.Key)))) failures.Add("drop_contract");
        if (trace.Overload && policy.Drops.Keys.Any(reason => reason is not ("QueueCapacity" or "QueueAge" or "UnreadTailAge")))
            failures.Add("unexpected_drop_reason");
        if (old.Upper.Length > 0 || old.Lower.Length > 0 || policy.Pending > 0) failures.Add("not_drained");
        foreach (var forbidden in trace.Forbidden ?? [])
            if (Flat(string.Concat(emissions.Select(e => e.Text))).Contains(Flat(forbidden), StringComparison.Ordinal)) failures.Add("obsolete_output");
        var firstWaits = emissions.Where(e => e.Id is not null).GroupBy(e => e.Id).Select(group =>
            group.First().At - trace.Inputs.First(input => input.Kind == "target" && Key(input) == group.Key).At);
        int duplicate = 0;
        var seen = new HashSet<(CaptionIdentity?, string)>();
        foreach (var e in emissions)
            if (!seen.Add((e.Id, e.Text))) duplicate += StringInfo.ParseCombiningCharacters(e.Text).Length;
        int erased = 0;
        foreach (var group in trace.Inputs.Where(i => i.Kind == "target").GroupBy(Key))
        {
            var previous = "";
            foreach (var input in group)
            {
                var next = input.Text ?? "";
                int common = 0;
                while (common < Math.Min(previous.Length, next.Length) && previous[common] == next[common]) common++;
                erased += previous.Length - common; previous = next;
            }
        }
        return new(name, trace.Name, trace.Group, failures.Count == 0, failures.Distinct().ToArray(), Distribution(firstWaits),
            Distribution(updateWaits), Distribution(correctionWaits), early, moves, duplicate,
            emissions.Sum(e => StringInfo.ParseCombiningCharacters(e.Text).Length),
            pending, history, erased, policy.Drops, missing.ToArray(), accounted.ToArray(), emissions, frames);
    }

    private static Trace[] Fixtures() =>
    [
        new("front-correction-identical-tail", [new(0, Text: "내일 출발합니다.\n표는 예약했습니다."),
            new(.5, Text: "다음 주에 출발합니다.\n표는 예약했습니다.", Final: true, Revision: 2)],
            [new(1, "표는 예약했습니다."), new(1, "다음 주에 출발합니다.", .5)]),
        new("front-correction-following-unit", [new(0, Text: "내일 출발합니다.\n표는 예약했습니다."),
            new(.5, Text: "다음 주에 출발합니다.\n표는 예약했습니다.", Final: true, Revision: 2),
            new(.7, 2, "다음 발화가 도착했습니다.", true)],
            [new(1, "표는 예약했습니다."), new(1, "다음 주에 출발합니다.", .5), new(2, "다음 발화가 도착했습니다.")]),
        new("independent-lines", [new(0, Text: "첫 발화입니다.", Final: true), new(.2, 2, "둘째 발화입니다.", true),
            new(.4, 3, "셋째 발화입니다.", true)], [new(1, "첫 발화입니다."), new(2, "둘째 발화입니다."), new(3, "셋째 발화입니다.")]),
        new("agreement-tail", [new(0, Text: "처음 들린 내용입니다."), new(.4, Text: "처음 들린 내용입니다.\n뒷문장이 추가됩니다.", Revision: 2),
            new(.8, Text: "처음 들린 내용입니다.\n뒷문장이 추가됩니다.", Revision: 3),
            new(1.2, Text: "처음 들린 내용입니다.\n뒷문장이 추가됩니다.", Final: true, Revision: 4)],
            [new(1, "처음 들린 내용입니다."), new(1, "뒷문장이 추가됩니다.", .4, Kind: "addition")]),
        new("unfinished-first", [new(0, Text: "실시간 자막이 아직 완성되지"), new(1, Text: "실시간 자막이 아직 완성되지 않았습니다.", Final: true, Revision: 2)],
            [new(1, "실시간 자막이 아직 완성되지 않았습니다.")]),
        new("short-unfinished-first", [new(0, Text: "곧 출발"), new(1, Text: "곧 출발합니다.", Final: true, Revision: 2)],
            [new(1, "곧 출발합니다.")]),
        new("number-negation-final", [new(0, Text: "가격은 10원입니다.\n주문할 수 있습니다."),
            new(.3, Text: "가격은 100원입니다.\n주문할 수 없습니다.", Final: true, Revision: 2)],
            [new(1, "가격은 100원입니다.", .3), new(1, "주문할 수 없습니다.", .3)]),
        new("repeated-revisions", [new(0, Text: "초기 내용입니다.\n같은 뒷문장입니다."),
            new(.2, Text: "버릴 수정본입니다.\n같은 뒷문장입니다.", Revision: 2),
            new(.4, Text: "다른 수정본입니다.\n같은 뒷문장입니다.", Revision: 3),
            new(.6, Text: "최종 교정 내용입니다.\n같은 뒷문장입니다.", Final: true, Revision: 4)],
            [new(1, "최종 교정 내용입니다.", .6), new(1, "같은 뒷문장입니다.")],
            Forbidden: ["버릴 수정본입니다.", "다른 수정본입니다."], Group: "adversarial"),
        new("shortened-final", [new(0, Text: "출발합니다.\n예약도 했습니다."), new(.4, Text: "출발합니다.", Final: true, Revision: 2)],
            [new(1, "출발합니다."), new(1, "출발합니다.", .4, "예약도 했습니다.")]),
        new("repeated-clauses", [new(0, Text: "맞습니다.\n맞습니다."), new(.5, Text: "아닙니다.\n맞습니다.", Final: true, Revision: 2)],
            [new(1, "아닙니다.", .5), new(1, "맞습니다.", .5)], Group: "adversarial"),
        new("source-revision-freezes-lines", [new(0, Text: "이전 내용입니다."), new(.064, Kind: "revise"),
            new(.2, Text: "새로 확인한 내용입니다.", Final: true, Revision: 2)], [new(1, "새로 확인한 내용입니다.", .2)], Group: "adversarial"),
        new("source-revision-at-deadline", [new(0, Text: "첫 표시입니다.\n둘째 표시입니다.\n철회된 꼬리입니다."),
            new(2.8, Kind: "revise"), new(2.9, Text: "첫 표시입니다.\n둘째 표시입니다.\n교정된 꼬리입니다.", Final: true, Revision: 2)],
            [new(1, "교정된 꼬리입니다.", 2.9)], Forbidden: ["철회된 꼬리입니다."], Group: "adversarial"),
        new("unread-rollback", [new(0, Text: "첫 문장입니다.\n둘째 문장입니다.\n폐기할 꼬리입니다."),
            new(.2, Text: "첫 문장입니다.\n둘째 문장입니다.", Final: true, Revision: 2)],
            [new(1, "첫 문장입니다."), new(1, "둘째 문장입니다.")], Forbidden: ["폐기할 꼬리입니다."], Group: "adversarial"),
        new("stale-revision", [new(0, Text: "최신 내용입니다.", Final: true, Revision: 3),
            new(.1, Text: "오래된 내용입니다.", Final: true, Revision: 2)], [new(1, "최신 내용입니다.")], Forbidden: ["오래된 내용입니다."], Group: "adversarial"),
        new("late-segment", [new(0, 2, "새 발화입니다.", true), new(.1, 1, "뒤늦은 발화입니다.", true)],
            [new(2, "새 발화입니다.")], Forbidden: ["뒤늦은 발화입니다."], Group: "adversarial", ExpectedDrops: new() { ["LateUnit"] = 1 }),
        new("overlapping-source-unit", [new(0, Text: "부분 문장입니다.", UnitStart: 8),
            new(.3, Text: "전체 문장이 교정됐습니다.", Final: true, Revision: 2)], [new(1, "전체 문장이 교정됐습니다.", .3)], Group: "adversarial"),
        new("decimal-abbreviation-unicode", [new(0, Text: "Dr. Kim: 3.14입니다.\n가족 👨‍👩‍👧‍👦 é입니다.", Final: true)],
            [new(1, "Dr. Kim: 3.14입니다."), new(1, "가족 👨‍👩‍👧‍👦 é입니다.")], Group: "adversarial"),
        new("withdraw-reset", [new(0, Text: "곧 철회합니다."), new(.1, Kind: "withdraw"), new(.2, Kind: "reset"),
            new(.3, Text: "다음 세션입니다.", Final: true, Epoch: 2)], [new(1, "다음 세션입니다.", .3, Kind: "addition")], Group: "adversarial"),
        new("long-wrapped-final", [new(0, Text: new string('가', 90) + ".", Final: true)],
            [new(1, new string('가', 90) + ".")], Group: "adversarial"),
        new("bounded-overload", Enumerable.Range(0, 12).Select(i => new Input(i * .1, (ulong)i + 1, $"발화{i}입니다.", true)).ToArray(),
            Enumerable.Range(0, 12).Select(i => new Required((ulong)i + 1, $"발화{i}입니다.")).ToArray(), End: 20, Overload: true, Group: "adversarial"),
        new("long-tail-age-overload", [new(0, Text: new string('나', 640) + ".", Final: true),
            new(8, 2, "뒤에 온 발화입니다.", true)], [new(1, new string('나', 640) + "."), new(2, "뒤에 온 발화입니다.")],
            End: 40, Overload: true, Group: "adversarial"),
        new("virtual-30-minutes", Enumerable.Range(0, 600).Select(i => new Input(i * 3, (ulong)i + 1, $"문장{i}입니다.", true)).ToArray(),
            Enumerable.Range(0, 600).Select(i => new Required((ulong)i + 1, $"문장{i}입니다.")).ToArray(), End: 1803, Group: "adversarial")
    ];

    public static int Run(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var reportPath = Path.Combine(directory, "simulation.json");
        if (File.Exists(reportPath)) throw new IOException("Refusing to overwrite a simulation report");
        var traces = Fixtures();
        var results = new List<Result>();
        foreach (var trace in traces)
        {
            results.Add(Replay(trace, "S0-current", new CurrentPolicy()));
            for (int mode = 1; mode <= 3; mode++) results.Add(Replay(trace, $"S{mode}", new CandidatePolicy(mode)));
        }
        var earlyControl = Replay(traces.Single(trace => trace.Name == "front-correction-identical-tail"),
            "negative-early-removal", new CandidatePolicy(1, breakReading: true));
        var missingControl = Replay(traces.Single(trace => trace.Name == "number-negation-final"),
            "negative-lost-final", new CandidatePolicy(1, loseFinal: true));
        bool controls = earlyControl.Failures.Contains("reading_deadline") && missingControl.Failures.Contains("required_output_missing");
        var attributionTrace = traces.Single(trace => trace.Name == "front-correction-following-unit");
        var ablations = new[]
        {
            Replay(attributionTrace, "S1-without-sentence-reuse", new CandidatePolicy(1, reuseSentences: false)),
            Replay(attributionTrace, "S1-without-free-lower-admission", new CandidatePolicy(1, allowFreeLower: false))
        };
        var comparisons = results.Where(result => result.Policy != "S0-current").Select(result =>
        {
            var control = results.Single(item => item.Policy == "S0-current" && item.Trace == result.Trace);
            var firstDelta = result.FirstLineWaitSeconds.P95 - control.FirstLineWaitSeconds.P95;
            var updateDelta = result.RequiredUpdateWaitSeconds.P95 - control.RequiredUpdateWaitSeconds.P95;
            bool overload = traces.Single(trace => trace.Name == result.Trace).Overload;
            return new Comparison(result.Policy, result.Trace, result.Passed, firstDelta, updateDelta,
                result.EmittedCharacters - control.EmittedCharacters, result.RepeatedLineCharacters - control.RepeatedLineCharacters,
                !overload && (firstDelta > .100001 || updateDelta > .100001), overload);
        }).ToArray();
        var selection = comparisons.GroupBy(comparison => comparison.Policy).Select(group => new
        {
            Policy = group.Key, FunctionalGate = group.All(item => item.FunctionalGate),
            LatencyRegressions = group.Where(item => item.LatencyRegression).Select(item => item.Trace).ToArray(),
            Next = group.All(item => item.FunctionalGate && !item.LatencyRegression) ? "prototype bounded input contract; not production acceptance" : "retain as research control; do not enable by default"
        }).ToArray();
        var sourceHashes = new Dictionary<string, string>();
        foreach (var path in new[] { "apps/EchoSub.Desktop/CaptionLines.cs", "tests/EchoSub.ProtocolSmoke/CaptionPolicySimulation.cs",
            "tests/EchoSub.ProtocolSmoke/Program.cs", "scripts/simulate-caption-policies.ps1" })
            sourceHashes[path] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        var report = new
        {
            Suite = "caption-policy-simulation-v1", CreatedAtUtc = DateTimeOffset.UtcNow,
            Scope = "Synthetic post-Deck target delivery; 32-grapheme fit; 0.1-second virtual ticks; no model inference",
            OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), DotnetRuntime = Environment.Version.ToString(),
            TraceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(traces)))).ToLowerInvariant(),
            SourceHashes = sourceHashes, Traces = traces, NegativeControlsDetected = controls,
            NegativeControls = new[] { earlyControl, missingControl }, Results = results, Comparisons = comparisons,
            Selection = selection, Ablations = ablations,
            Excluded = new[] { "natural speech effect", "translation semantic accuracy", "candidate physical/rendered screen", "audio/VAD/ASR/Deck latency", "actual 30-minute session", "macOS" }
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, CaptionRegression.JsonOptions));
        var md = new StringBuilder("# Caption policy simulation\n\nSynthetic scheduler comparison. Candidate failures are reported, not suppressed.\n\n");
        md.AppendLine($"Negative controls detected: {controls}\n");
        md.AppendLine("| Trace | Policy | Gate | First wait P95 (s) | Required update P95 (s) | Repeat line chars | Emitted chars | Drops |\n| --- | --- | --- | ---: | ---: | ---: | ---: | --- |");
        foreach (var result in results)
        {
            md.AppendLine($"| {result.Trace} | {result.Policy} | {(result.Passed ? "PASS" : string.Join(",", result.Failures))} | {result.FirstLineWaitSeconds.P95} | {result.RequiredUpdateWaitSeconds.P95} | {result.RepeatedLineCharacters} | {result.EmittedCharacters} | {string.Join(",", result.Drops.Select(d => $"{d.Key}:{d.Value}"))} |");
            Console.WriteLine($"{result.Policy} {result.Trace}: {(result.Passed ? "PASS" : string.Join(",", result.Failures))}");
        }
        md.AppendLine("\n## Selection\n");
        foreach (var item in selection)
            md.AppendLine($"- {item.Policy}: {item.Next}; latency regressions: {string.Join(",", item.LatencyRegressions)}");
        md.AppendLine("\n## Attribution controls\n\nFree-lower admission already exists in production. These controls separate its contribution from sentence reuse.\n");
        foreach (var item in ablations)
            md.AppendLine($"- {item.Policy}: first-wait P95 {item.FirstLineWaitSeconds.P95} s; repeat characters {item.RepeatedLineCharacters}; functional gate {item.Passed}");
        md.AppendLine("\nRepeated line characters count exact repeated fragments in one identity, including expire-and-reassign within one Tick. Legitimate repetitions can be counted. Candidate erasure counts UTF-16 units in the input trace, not visible reading violations. Required update/correction waits cover labelled fixtures, not general semantic correctness. All times are seconds. Overload loss stays in missing/accounted_unshown and is never reported as full delivery. The 0.1 s latency tolerance equals one virtual tick.\n");
        File.WriteAllText(Path.Combine(directory, "simulation.md"), md.ToString());
        Console.WriteLine($"Simulation completed; negative controls={controls}; {reportPath}");
        // Honest failed research candidates do not make the comparison runner fail.
        return controls ? 0 : 1;
    }
}
