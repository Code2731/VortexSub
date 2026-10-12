using EchoSub.Desktop;

internal static class CaptionSentenceReuseSmoke
{
    public static CaptionScheduleContractSmoke.Case[] Run()
    {
        var cases = new List<CaptionScheduleContractSmoke.Case>();
        void Test(string name, Action<Action<bool, string>> run)
        {
            int checks = 0; var failures = new List<string>();
            try { run((ok, message) => { checks++; if (!ok) failures.Add(message); }); }
            catch (Exception error) { failures.Add(error.GetType().Name + ": " + error.Message); }
            cases.Add(new("reuse-" + name, checks, failures.ToArray()));
        }
        CaptionScheduleInput Input(string text, ulong request, double at = 0) =>
            new(new("reuse", 1, 1, 1, 0), 1, request, true, new(0, 64, 1), text, at);
        (CaptionScheduleStore Store, CaptionScheduleToken Token, CaptionReuseAnalysis Analysis) Setup(
            string old, string next, Action<bool, string> check, int split = 0)
        {
            var store = new CaptionScheduleStore(); store.Begin(new("reuse", 1, 1));
            var token = store.Submit(Input(old, 1)).Token!;
            if (split > 0)
            {
                check(store.Display(token, 0, split, 0, 0, 2.5).Accepted, "upper assigned");
                check(store.Display(token, split, old.Length - split, 1, 0, 2.75).Accepted, "lower assigned");
            }
            else check(store.Display(token, 0, old.Length, 0, 0, 2.5).Accepted, "baseline actually displayed");
            check(store.Complete(token).Accepted, "baseline complete");
            token = store.Submit(Input(next, 2, .1)).Token!;
            return (store, token, CaptionSentenceReuse.Analyze(store.Snapshot(token.Identity)!));
        }
        Test("changed-head-stable-tail", check =>
        {
            const string old = "사과를 주세요. 문을 닫으세요.";
            const string next = "망고를 주세요. 문을 닫으세요.";
            var (store, token, analysis) = Setup(old, next, check);
            check(analysis.Reason == CaptionReuseReason.Ready && analysis.Proof?.Ranges.Count == 1, "exact second sentence reusable");
            var range = analysis.Proof!.Ranges[0];
            check(store.Reuse(analysis.Proof, range.Start, range.End).Reason == CaptionScheduleReason.DisplayOrder, "changed head cannot be skipped");
            check(store.Complete(token).Reason == CaptionScheduleReason.IncompleteDisplay, "unread correction not completed");
            check(store.Display(token, 0, range.Start, 0, .1, 2.6).Reason == CaptionScheduleReason.ReadingProtected, "old line protected");
            store.Advance(2.5);
            check(store.Display(token, 0, range.Start, 0, 2.5, 5).Accepted, "changed head assigned after reading");
            var lease = store.UpperLease;
            check(store.Reuse(analysis.Proof, range.Start, range.End).Accepted, "stable tail consumes verified evidence");
            check(store.UpperLease == lease, "reuse does not reset lease");
            check(store.Complete(token).Accepted, "assigned head and reused tail complete");
            var snapshot = store.Snapshot(token.Identity)!;
            check(snapshot.Displayed.Last().Reused && snapshot.Displayed.Last().Slot == -1, "reuse is labeled, no fake slot");
            check(store.DisplayedPieces <= 64 && store.StoredCharacters <= 131072, "reuse ledger bounded");
            token = store.Submit(Input("파파야를 주세요. 문을 닫으세요.", 3, 2.6)).Token!;
            check(CaptionSentenceReuse.Analyze(store.Snapshot(token.Identity)!).Proof?.Ranges.Count == 1, "next revision uses composed delivery evidence");
        });
        Test("identical-body-keeps-reading-deadline", check =>
        {
            var (store, token, analysis) = Setup("첫 문장. 둘째 문장.", "첫 문장. 둘째 문장.", check);
            var lease = store.UpperLease;
            var range = analysis.Proof!.Ranges[0];
            check(store.Reuse(analysis.Proof, range.Start, range.End).Reason == CaptionScheduleReason.DisplayOrder, "existing identical coverage cannot be consumed twice");
            check(store.Complete(token).Accepted && store.UpperLease == lease, "no deadline extension or early removal");
        });
        Test("cross-line-sentence", check =>
        {
            var (_, _, analysis) = Setup("첫 문장이 길게 이어집니다. 두 번째 문장입니다.", "첫 문장이 바뀌었습니다. 두 번째 문장입니다.", check, 5);
            check(analysis.Proof?.Ranges.Count == 1, "contiguous actual line pieces form baseline");
        });
        foreach (var (name, old, next, reason) in new[]
        {
            ("sentence-deletion", "첫 문장. 둘째 문장. 셋째 문장.", "첫 문장. 셋째 문장.", CaptionReuseReason.DeletionOrReorder),
            ("sentence-reorder", "첫 문장. 둘째 문장.", "둘째 문장. 첫 문장.", CaptionReuseReason.DeletionOrReorder),
            ("middle-insertion", "첫 문장. 둘째 문장.", "첫 문장. 추가 문장. 둘째 문장.", CaptionReuseReason.DeletionOrReorder),
            ("duplicate-sentence", "같은 문장. 같은 문장. 끝 문장.", "다른 문장. 같은 문장. 끝 문장.", CaptionReuseReason.AmbiguousSentence),
            ("open-tail", "첫 문장. 아직 미완성", "다른 문장. 아직 미완성", CaptionReuseReason.NoMatch),
            ("number-change", "가격은 10입니다.", "가격은 100입니다.", CaptionReuseReason.NoMatch),
            ("negation-change", "허용됩니다.", "허용되지 않습니다.", CaptionReuseReason.NoMatch),
            ("spacing-change", "문을 닫으세요.", "문을  닫으세요.", CaptionReuseReason.NoMatch)
        }) Test(name, check =>
        {
            var (_, _, analysis) = Setup(old, next, check);
            check(analysis.Reason == reason && analysis.Proof is null, "uncertain/changed text not reused");
        });
        Test("decimal-and-abbreviation", check =>
        {
            var (_, _, analysis) = Setup("Dr. Kim paid 3.14 dollars. The door is shut.",
                "Dr. Lee paid 3.14 dollars. The door is shut.", check);
            check(analysis.OldFragments == 2 && analysis.NewFragments == 2, "decimal and title not separate sentences");
            check(analysis.Proof?.Ranges.Count == 1, "unchanged closed sentence only");
        });
        Test("emoji-quotes-and-cjk", check =>
        {
            var (_, _, analysis) = Setup("가격이 달라요。 “👨‍👩‍👧‍👦 좋아요!”", "가격이 같아요。 “👨‍👩‍👧‍👦 좋아요!”", check);
            check(analysis.Proof?.Ranges.Count == 1, "quoted emoji sentence retained");
            var range = analysis.Proof!.Ranges[0];
            check(range.End == "가격이 같아요。 “👨‍👩‍👧‍👦 좋아요!”".Length, "UTF-16 range includes full grapheme and quote");
        });
        Test("fragment-cap", check =>
        {
            var (_, _, analysis) = Setup(string.Join(" ", Enumerable.Range(0, 65).Select(i => $"문장{i}.")),
                string.Join(" ", Enumerable.Range(0, 65).Select(i => $"문장{i}.")), check);
            check(analysis.Reason == CaptionReuseReason.FragmentLimit, "65 fragments rejected before matrix");
        });
        Test("maximum-matrix", check =>
        {
            string text = string.Join(" ", Enumerable.Range(0, 64).Select(i => $"문장{i}."));
            var (_, _, analysis) = Setup(text, text, check);
            check(analysis.MatrixCells == 4096 && analysis.Proof?.Ranges.Count == 64, "64x64 maximum bounded matrix");
        });
        Test("unknown-revised-overlap-incomplete", check =>
        {
            var (store, token, _) = Setup("첫 문장. 둘째 문장.", "다른 문장. 둘째 문장.", check);
            var snapshot = store.Snapshot(token.Identity)!;
            check(CaptionSentenceReuse.Analyze(snapshot with { HistoryComplete = false }).Reason == CaptionReuseReason.HistoryIncomplete, "trimmed history fallback");
            check(CaptionSentenceReuse.Analyze(snapshot with { Latest = snapshot.Latest! with { SourceSpan = null } }).Reason == CaptionReuseReason.UnknownSourceRange, "unknown source fallback");
            check(CaptionSentenceReuse.Analyze(snapshot with { Comparison = new(token, CaptionScheduleReason.SourceOverlap, .1) }).Reason == CaptionReuseReason.SourceOverlap, "overlap fallback");
            token = store.Submit(Input("다른 문장. 둘째 문장.", 3, .2) with { SourceRevision = 2, SourceSpan = new(0, 64, 2) }).Token!;
            check(CaptionSentenceReuse.Analyze(store.Snapshot(token.Identity)!).Reason == CaptionReuseReason.SourceChanged, "changed source revision fallback");
        });
        Test("missing-duplicate-ledger", check =>
        {
            var (store, token, _) = Setup("첫 문장. 둘째 문장.", "다른 문장. 둘째 문장.", check, 3);
            var snapshot = store.Snapshot(token.Identity)!;
            check(CaptionSentenceReuse.Analyze(snapshot with { Displayed = snapshot.Displayed.Skip(1).ToArray() }).Reason == CaptionReuseReason.HistoryIncomplete, "missing leading actual piece");
            check(CaptionSentenceReuse.Analyze(snapshot with { Displayed = snapshot.Displayed.Concat(snapshot.Displayed).ToArray() }).Reason == CaptionReuseReason.HistoryIncomplete, "duplicate actual piece");
        });
        Test("stale-proof-and-session", check =>
        {
            var (store, token, analysis) = Setup("첫 문장. 둘째 문장.", "다른 문장. 둘째 문장.", check);
            var range = analysis.Proof!.Ranges[0];
            store.Submit(Input("최신 문장. 둘째 문장.", 3, .2));
            check(store.Reuse(analysis.Proof, range.Start, range.End).Reason == CaptionScheduleReason.StaleToken, "replacement invalidates proof");
            store.Begin(new("reuse", 1, 1)); store.Submit(Input("다른 문장. 둘째 문장.", 2));
            check(store.Reuse(analysis.Proof, range.Start, range.End).Reason == CaptionScheduleReason.StaleToken, "new Begin invalidates old proof");
        });
        Test("altered-proof-and-range", check =>
        {
            var (store, _, analysis) = Setup("첫 문장. 둘째 문장.", "첫 문장. 다른 문장.", check);
            var proof = analysis.Proof!; var first = proof.Ranges[0];
            check(store.Reuse(new(proof.Token, "incorrect", proof.Ranges.ToArray()), first.Start, first.End).Reason == CaptionScheduleReason.InvalidReuseProof, "hash checked against current actual ledger");
            check(store.Reuse(proof, 0, first.End + 1).Reason == CaptionScheduleReason.InvalidReuseProof, "no arbitrary range");
            var ranges = (IList<CaptionReuseRange>)proof.Ranges;
            bool immutable = false; try { ranges[0] = new(0, 1); } catch (NotSupportedException) { immutable = true; }
            check(immutable, "caller cannot mutate proof ranges");
            check(store.Snapshot(proof.Token.Identity)!.DisplayedThrough == 0, "invalid proof leaves coverage unchanged");
        });
        Test("consumed-proof-and-omission", check =>
        {
            var (store, token, analysis) = Setup("첫 문장. 둘째 문장.", "첫 문장. 다른 문장.", check);
            var first = analysis.Proof!.Ranges[0];
            check(store.Reuse(analysis.Proof, first.Start, first.End).Accepted, "first sentence reused");
            check(store.Reuse(analysis.Proof, first.Start, first.End).Reason == CaptionScheduleReason.DisplayOrder, "no repeated consume");
            check(store.Complete(token).Reason == CaptionScheduleReason.IncompleteDisplay, "unconsumed second sentence not hidden");
        });
        Test("proof-cannot-change-ranges", check =>
        {
            var (store, token, analysis) = Setup("첫 문장. 둘째 문장.", "첫 문장. 다른 문장.", check);
            var proof = analysis.Proof!;
            var forged = new CaptionReuseProof(token, proof.EvidenceHash, [new(0, store.Next!.Latest!.Target.Length)]);
            check(store.Reuse(forged, 0, store.Next.Latest!.Target.Length).Reason == CaptionScheduleReason.InvalidReuseProof, "same hash cannot authorize a changed sentence");
            check(store.Next.DisplayedThrough == 0 && store.Next.HasUnread, "forged completion leaves unread work intact");
        });
        Test("ledger-trimming-revokes-proof", check =>
        {
            var store = new CaptionScheduleStore(); store.Begin(new("reuse", 1, 1));
            string body = string.Join(" ", Enumerable.Range(0, 64).Select(i => $"문장{i}."));
            var token = store.Submit(Input(body, 1)).Token!;
            int through = 0;
            for (int i = 0; i < 64; i++)
            {
                int end = body.IndexOf('.', through) + 1;
                if (end < body.Length) end++;
                double at = i * 3; store.Advance(at);
                check(store.Display(token, through, end - through, 0, at, at + 2.5).Accepted, "baseline piece displayed");
                through = end;
            }
            check(store.Complete(token).Accepted, "64-piece baseline complete");
            token = store.Submit(Input(body.Replace("문장63.", "바뀐 마지막 문장."), 2, 189.1)).Token!;
            var proof = CaptionSentenceReuse.Analyze(store.Snapshot(token.Identity)!).Proof!;
            var first = proof.Ranges[0];
            check(store.Reuse(proof, first.Start, first.End).Accepted, "first proven range committed within limit");
            check(store.DisplayedPieces == 64 && !store.Snapshot(token.Identity)!.HistoryComplete, "expired history trimmed and marked incomplete");
            var second = proof.Ranges[1];
            check(store.Reuse(proof, second.Start, second.End).Reason == CaptionScheduleReason.InvalidReuseProof, "trimmed evidence cannot authorize another skip");
            check(store.Complete(token).Reason == CaptionScheduleReason.IncompleteDisplay, "remaining changed/unreused text retained");
        });
        Test("sequential-reuse-remains-bounded", check =>
        {
            var store = new CaptionScheduleStore(); store.Begin(new("reuse", 1, 1));
            for (ulong segment = 1; segment <= 300; segment++)
            {
                double at = (segment - 1) * 6;
                store.Advance(at);
                string old = $"이전 문장{segment}. 같은 뒷문장.";
                var baseline = Input(old, 1, at) with { Identity = new("reuse", 1, 1, segment, 0) };
                var token = store.Submit(baseline).Token!;
                check(store.Display(token, 0, old.Length, 0, at, at + 2.5).Accepted, "baseline displayed");
                check(store.Complete(token).Accepted, "baseline completed");
                string next = $"새 문장{segment}. 같은 뒷문장.";
                token = store.Submit(baseline with { RequestId = 2, Target = next, AtSeconds = at + .1 }).Token!;
                var proof = CaptionSentenceReuse.Analyze(store.Snapshot(token.Identity)!).Proof!;
                var tail = proof.Ranges.Single();
                store.Advance(at + 2.5);
                check(store.Display(token, 0, tail.Start, 0, at + 2.5, at + 5).Accepted, "correction actually assigned");
                check(store.Reuse(proof, tail.Start, tail.End).Accepted, "exact rear sentence reused");
                check(store.Complete(token).Accepted, "complete with explicit evidence");
                check(store.TrackedUnits <= 64 && store.DisplayedPieces <= 256 && store.StoredCharacters <= 131072, "global limits include reuse evidence");
            }
            check(store.Counters.GetValueOrDefault(CaptionScheduleReason.Reused) == 300, "all reuse events counted");
            check(store.Counters.GetValueOrDefault(CaptionScheduleReason.QueueCapacity) == 0, "normal reuse has no capacity loss");
        });
        Test("source-correction-leading-prefix", check =>
        {
            var store = new CaptionScheduleStore(); store.Begin(new("reuse", 1, 1));
            const string old = "기다려.\n왼쪽.";
            var token = store.Submit(Input(old, 1)).Token!;
            check(store.Display(token, 0, 5, 0, 0, 2.5).Accepted, "leading sentence delivered");
            check(store.Display(token, 5, old.Length - 5, 1, 0, 2.75).Accepted, "old suffix delivered");
            store.Complete(token);
            store.Invalidate(new("reuse", 1, 1), 1, .064);
            token = store.Submit(Input("기다려.\n오른쪽.", 2, .2) with { SourceRevision = 2, SourceSpan = new(0, 70, 2) }).Token!;
            var snapshot = store.Snapshot(token.Identity)!;
            check(CaptionSentenceReuse.Analyze(snapshot).Proof is null, "cross-source rear reuse still forbidden");
            var proof = CaptionSentenceReuse.AnalyzeLeadingPrefix(snapshot).Proof!;
            check(proof.Kind == CaptionReuseKind.DeliveredLeadingPrefix && proof.Ranges.Single() == new CaptionReuseRange(0, 5), "only identical closed prefix proven");
            var upper = store.UpperLease; var lower = store.LowerLease;
            check(store.Reuse(new(proof.Token, "invalid", proof.Ranges.ToArray(), proof.Kind), 0, 5).Reason == CaptionScheduleReason.InvalidReuseProof, "forged prefix hash rejected");
            check(store.Reuse(new(proof.Token, proof.EvidenceHash, proof.Ranges.ToArray()), 0, 5).Reason == CaptionScheduleReason.InvalidReuseProof, "proof kind cannot bypass strict comparator");
            check(store.Reuse(proof, 0, 5).Accepted, "prefix covered without redisplay");
            check(store.UpperLease == upper && store.LowerLease == lower, "both original reading deadlines unchanged");
            check(store.Complete(token).Reason == CaptionScheduleReason.IncompleteDisplay, "changed suffix remains unread");
            check(store.Display(token, 5, 4, 0, .2, 2.7).Reason == CaptionScheduleReason.ReadingProtected, "changed suffix respects old leases");
            store.Advance(2.75);
            check(store.Display(token, 5, 4, 0, 2.75, 5.25).Accepted && store.Complete(token).Accepted, "changed suffix displayed after protection");
            token = store.Submit(Input("기다려.\n멈춰.", 3, 2.8) with { SourceRevision = 3, SourceSpan = new(0, 72, 3) }).Token!;
            check(CaptionSentenceReuse.AnalyzeLeadingPrefix(store.Snapshot(token.Identity)!).Proof?.Ranges.Single() == new CaptionReuseRange(0, 5), "composed evidence survives next correction");
            check(store.Reuse(proof, 0, 5).Reason == CaptionScheduleReason.StaleToken, "old proof cannot cross generations");
        });
        foreach (var (name, old, next) in new[]
        {
            ("changed-number", "10개입니다. 다음.", "100개입니다. 다음."),
            ("changed-negation", "허용됩니다. 다음.", "허용되지 않습니다. 다음."),
            ("open-leading", "아직 미완성", "아직 미완성 추가"),
            ("moved-leading", "첫 문장. 둘째 문장.", "둘째 문장. 첫 문장.")
        }) Test("prefix-reject-" + name, check =>
        {
            var (store, token, _) = Setup(old, next, check);
            token = store.Submit(Input(next, 3, .2) with { SourceRevision = 2, SourceSpan = new(0, 70, 2) }).Token!;
            check(CaptionSentenceReuse.AnalyzeLeadingPrefix(store.Snapshot(token.Identity)!).Proof is null, "changed or open leading text cannot be skipped");
        });
        Test("prefix-reject-unknown-and-incomplete", check =>
        {
            var (store, token, _) = Setup("첫 문장. 뒤.", "첫 문장. 변경.", check);
            var snapshot = store.Snapshot(token.Identity)!;
            check(CaptionSentenceReuse.AnalyzeLeadingPrefix(snapshot with { HistoryComplete = false }).Proof is null, "incomplete history rejected");
            check(CaptionSentenceReuse.AnalyzeLeadingPrefix(snapshot with { Latest = snapshot.Latest! with { SourceSpan = null } }).Proof is null, "unknown current source rejected");
            check(CaptionSentenceReuse.AnalyzeLeadingPrefix(snapshot with { Displayed = snapshot.Displayed.Select(piece => piece with { SourceSpan = null }).ToArray() }).Proof is null, "unknown baseline source rejected");
            check(CaptionSentenceReuse.AnalyzeLeadingPrefix(snapshot with { Displayed = snapshot.Displayed.Select(piece => piece with { TargetStart = 1 }).ToArray() }).Proof is null, "gapped evidence rejected");
            check(CaptionSentenceReuse.AnalyzeLeadingPrefix(snapshot with { Latest = snapshot.Latest! with { SourceSpan = new(1, 64, 1) } }).Proof is null, "nonzero source origin rejected");
            check(CaptionSentenceReuse.AnalyzeLeadingPrefix(snapshot with { Latest = snapshot.Latest! with { Target = "첫 문장." } }).Reason == CaptionReuseReason.DeletionOrReorder, "sentence deletion keeps full correction");
            check(CaptionSentenceReuse.AnalyzeLeadingPrefix(snapshot with { Latest = snapshot.Latest! with { Target = string.Concat(Enumerable.Repeat("문장입니다. ", 65)) } }).Reason == CaptionReuseReason.FragmentLimit, "prefix comparison respects fragment bound");
        });
        return cases.ToArray();
    }
}
