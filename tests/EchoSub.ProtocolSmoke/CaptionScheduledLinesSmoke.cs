using EchoSub.Desktop;

internal static class CaptionScheduledLinesSmoke
{
    public static CaptionScheduleContractSmoke.Case[] Run()
    {
        var cases = new List<CaptionScheduleContractSmoke.Case>();
        void Test(string name, Action<Action<bool, string>> test)
        {
            int checks = 0; var errors = new List<string>();
            try { test((ok, detail) => { checks++; if (!ok) errors.Add(detail); }); }
            catch (Exception error) { errors.Add(error.ToString()); }
            cases.Add(new("reader-" + name, checks, errors.ToArray()));
        }
        CaptionScheduledLines Reader() { var reader = new CaptionScheduledLines(); reader.Begin(new("reader", 1, 1)); return reader; }
        CaptionScheduleInput Input(string text, ulong request = 1, double at = 0, ulong segment = 1) =>
            new(new("reader", 1, 1, segment, 0), 1, request, false, new(0, 64, 1), text, at);
        int Fit(string text) => text.IndexOfAny(['\r', '\n']) is var i && i >= 0 ? i + 1 : Math.Min(32, text.Length);
        Test("corrected-upper-retains-proven-lower", check =>
        {
            var reader = Reader(); reader.Submit(Input("old head.\nsame tail."), Fit);
            var lower = reader.Store.LowerLease!;
            reader.Submit(Input("new head.\nsame tail.", 2, .1), Fit);
            reader.Submit(Input("following.", at: .2, segment: 2), Fit);
            reader.Tick(2.49, Fit);
            check(reader.Upper == "old head." && reader.Lower == "same tail.", "both old rows protected");
            reader.Tick(2.5, Fit);
            check(reader.Upper == "new head." && reader.Lower == "same tail.", "correction occupies expired upper only");
            check(reader.Store.LowerLease == lower, "lower piece and deadline unchanged");
            check(reader.Store.UpperLease!.ReadingUntilSeconds == 5, "new upper has full reading time");
            check(reader.Store.Counters.GetValueOrDefault(CaptionScheduleReason.Reused) == 1, "tail reused once");
            reader.Tick(2.75, Fit);
            check(reader.Upper == "new head." && reader.Lower == "following.", "following remains below correction");
            check(reader.Store.LowerLease!.ReadingUntilSeconds == 5.25, "following full reading and stagger");
            reader.Tick(5, Fit); check(reader.Upper is null && reader.Lower == "following.", "no row movement");
        });
        Test("retained-lower-denies-unknown-and-changed-source", check =>
        {
            foreach (bool unknown in new[] { true, false })
            {
                var reader = Reader(); reader.Submit(Input("old head.\nsame tail."), Fit);
                var next = Input("new head.\nsame tail.", 2, .1);
                next = unknown ? next with { SourceSpan = null } : next with { SourceRevision = 2, SourceSpan = new(0, 64, 2) };
                reader.Submit(next, Fit); reader.Tick(2.5, Fit);
                check(reader.Upper is null && reader.Lower == "same tail.", "unknown or revised source waits for lower");
                reader.Tick(2.75, Fit);
                check(reader.Upper == "new head." && reader.Lower == "same tail.", "whole correction remains available");
            }
        });
        Test("retained-lower-denies-changed-tail-and-long-head", check =>
        {
            foreach (string target in new[] { "new head.\nchanged tail.", new string('x', 40) + ".\nsame tail." })
            {
                var reader = Reader(); reader.Submit(Input("old head.\nsame tail."), Fit);
                reader.Submit(Input(target, 2, .1), text => Math.Min(32, Fit(text)));
                reader.Tick(2.5, text => Math.Min(32, Fit(text)));
                check(reader.Upper is null && reader.Lower == "same tail.", "only a complete prefix with proven lower may bypass order");
            }
        });
        Test("retained-lower-proof-rechecked-at-display", check =>
        {
            var store = new CaptionScheduleStore(); store.Begin(new("reader", 1, 1));
            var old = store.Submit(Input("old head.\nsame tail.")).Token!;
            store.Display(old, 0, 10, 0, 0, 2.5);
            store.Display(old, 10, 10, 1, 0, 2.5);
            store.Complete(old);
            var token = store.Submit(Input("new head.\nsame tail.", 2, .1)).Token!;
            var proof = CaptionSentenceReuse.Analyze(store.Snapshot(token.Identity)!).Proof!;
            check(!store.CanDisplayAboveRetainedLower(token, proof, 0, 10), "upper still protected");
            store.Advance(2.5);
            check(store.CanDisplayAboveRetainedLower(token, proof, 0, 10), "exact lower proof eligible after upper expiry");
            var bad = new CaptionReuseProof(token, "bad hash", proof.Ranges.ToArray());
            check(store.Display(token, 0, 10, 0, 2.5, 5, retainedLowerProof: bad).Reason == CaptionScheduleReason.ReadingProtected, "bad proof does not bypass guard");
            check(store.Display(token, 0, 10, 0, 2.5, 5).Reason == CaptionScheduleReason.ReadingProtected, "ordinary calls keep guard");
            check(store.Snapshot(token.Identity)!.DisplayedThrough == 0, "denials do not advance coverage");
            check(store.Display(token, 0, 10, 0, 2.5, 5, retainedLowerProof: proof).Accepted, "validated proof permits upper");
            check(store.LowerLease!.ReadingUntilSeconds == 2.75, "lower deadline not extended");
        });
        Test("retained-lower-thirty-minute-bounds", check =>
        {
            var reader = Reader();
            for (ulong segment = 1; segment <= 300; segment++)
            {
                double at = (segment - 1) * 6;
                reader.Submit(Input("old head.\nsame tail.", at: at, segment: segment), Fit);
                var lower = reader.Store.LowerLease;
                reader.Submit(Input("new head.\nsame tail.", 2, at + .1, segment), Fit);
                reader.Tick(at + 2.5, Fit);
                check(reader.Upper == "new head." && reader.Lower == "same tail.", "early correction retains lower");
                check(reader.Store.LowerLease == lower, "repeated correction does not prolong old reading");
                check(reader.Store.TrackedUnits <= 64 && reader.Store.DisplayedPieces <= 256 && reader.Store.StoredCharacters <= 131072,
                    "evidence and retained text remain bounded");
                reader.Tick(at + 5, Fit);
                check(reader.Upper is null && reader.Lower is null, "both rows drain without replay");
                check(reader.Store.Counters.GetValueOrDefault(CaptionScheduleReason.Reused) == segment, "one tail reuse per correction");
            }
            reader.Stop(); check(reader.Store.StoredCharacters == 0, "stop releases correction history");
        });
        Test("protected-correction-reuses-tail-and-follows-lower", check =>
        {
            var reader = Reader(); reader.Submit(Input("이전 문장. 같은 뒷문장."), Fit);
            reader.Submit(Input("새 문장. 같은 뒷문장.", 2, .1), Fit);
            check(reader.Upper == "이전 문장. 같은 뒷문장.", "old line protected");
            reader.Submit(Input("다음 발화.", at: .2, segment: 2), Fit);
            reader.Tick(2.5, Fit);
            check(reader.Upper == "새 문장. " && reader.Lower == "다음 발화.", "changed head upper, next utterance lower");
            check(reader.Store.Counters.GetValueOrDefault(CaptionScheduleReason.Reused) == 1, "rear sentence not assigned twice");
            check(reader.Store.LowerLease!.ReadingUntilSeconds >= reader.Store.UpperLease!.ReadingUntilSeconds + .25, "lower stagger");
            reader.Tick(5, Fit); check(reader.Upper is null && reader.Lower == "다음 발화.", "no lower-to-upper jump");
        });
        Test("mixed-lifecycle-thirty-minute", check =>
        {
            var reader = Reader(); var owner = new CaptionScheduleContext("reader", 1, 1);
            var events = new List<CaptionReadingObservation>(); reader.Observed += events.Add;
            for (ulong segment = 1; segment <= 300; segment++)
            {
                double at = (segment - 1) * 6;
                CaptionScheduleInput Make(string text, ulong request, double time) => Input(text, request, time, segment) with
                { Identity = new(owner.ProductSession, owner.Session, owner.Epoch, segment, 0) };
                var original = Make("old head.\nsame tail.", 1, at);
                check(reader.Submit(original, Fit).Accepted, "initial input accepted");
                var lower = reader.Store.LowerLease;
                var correction = Make("new head.\nsame tail.", 2, at + .1);
                var token = reader.Submit(correction, Fit).Token!;
                var proof = CaptionSentenceReuse.Analyze(reader.Store.Snapshot(token.Identity)!).Proof!;
                switch ((segment - 1) % 6)
                {
                    case 0:
                        reader.Submit(Make("latest head.\nsame tail.", 3, at + .2), Fit);
                        check(!reader.Submit(correction with { AtSeconds = at + .3 }, Fit).Accepted, "older correction rejected");
                        reader.Tick(at + 2.49, Fit);
                        check(reader.Upper == "old head." && reader.Store.LowerLease == lower, "repeated correction preserves reading");
                        reader.Tick(at + 2.5, Fit);
                        check(reader.Upper == "latest head." && reader.Lower == "same tail.", "latest correction delivered above retained lower");
                        break;
                    case 1:
                        reader.Invalidate(segment, at + .2);
                        reader.Submit(Make("revised head.\nrevised tail.", 3, at + .3) with
                        { SourceRevision = 2, SourceSpan = new(0, 64, 2) }, Fit);
                        reader.Tick(at + 2.5, Fit);
                        check(reader.Upper is null && reader.Lower == "same tail.", "source revision waits for protected lower");
                        reader.Tick(at + 2.75, Fit);
                        check(reader.Upper == "revised head." && reader.Lower == "revised tail.", "whole source correction delivered");
                        break;
                    case 2:
                    case 3:
                        if ((segment - 1) % 6 == 3) reader.Tick(at + 2.5, Fit);
                        double withdrawnAt = at + ((segment - 1) % 6 == 3 ? 2.6 : .2);
                        check(reader.Invalidate(segment, withdrawnAt, true), "explicit withdrawal accepted");
                        check(reader.Upper is null && reader.Lower is null, "withdrawal clears both rows");
                        check(!reader.Store.Reuse(proof, proof.Ranges[0].Start, proof.Ranges[0].End).Accepted, "withdrawn evidence cannot replay");
                        check(!reader.Submit(Make("late head.\nsame tail.", 3, withdrawnAt + .1), Fit).Accepted, "late withdrawn input rejected");
                        break;
                    case 4:
                        reader.Tick(at + .2, Fit); reader.Stop(); reader.Stop();
                        check(!reader.Submit(correction with { AtSeconds = at + .3 }, Fit).Accepted, "stopped reader rejects input");
                        reader.Begin(owner);
                        reader.Submit(Make("resumed context.", 3, at + .4), Fit);
                        check(!reader.Store.Display(token, 0, 1, 0, at + .4, at + 2.9).Accepted, "old token cannot enter restarted context");
                        check(!reader.Store.Reuse(proof, proof.Ranges[0].Start, proof.Ranges[0].End).Accepted, "old proof cannot enter restarted context");
                        check(reader.Upper == "resumed context.", "restart receives only fresh text");
                        break;
                    case 5:
                        owner = ((segment - 1) / 6 % 3) switch
                        {
                            0 => owner with { Epoch = owner.Epoch + 1 },
                            1 => owner with { Session = owner.Session + 1 },
                            _ => owner with { ProductSession = "reader-" + segment }
                        };
                        reader.ValidateSession(owner.ProductSession, owner.Session, owner.Epoch, true);
                        check(reader.Upper is null && reader.Store.StoredCharacters == 0, "context transition clears old state");
                        reader.Begin(owner);
                        reader.Submit(Make("new context.", 1, at + .3), Fit);
                        check(!reader.Submit(original with { AtSeconds = at + .4 }, Fit).Accepted, "old context input rejected");
                        check(reader.Upper == "new context.", "new context receives only new text");
                        break;
                }
                reader.Tick(at + 5.75, Fit);
                check(reader.Upper is null && reader.Lower is null, "cycle drains without late replay");
                check(reader.Store.PendingUnits == 0 && reader.Store.TrackedUnits <= 64 && reader.Store.DisplayedPieces <= 256 &&
                    reader.Store.StoredCharacters <= 131072, "mixed lifecycle retains bounded state");
            }
            reader.Stop(); reader.Tick(1800, Fit);
            check(reader.Store.StoredCharacters == 0 && reader.Upper is null && reader.Lower is null, "final stop releases state");
            check(events.Count(e => e.Kind == "Blocked") == events.Count(e => e.Kind == "BlockEnded"), "all block intervals closed");
        });
        Test("open-line-appends-without-moving", check =>
        {
            var reader = Reader(); reader.Submit(Input("안녕하세요"), Fit);
            reader.Submit(Input("안녕하세요 반갑습니다", 2, .4), Fit);
            check(reader.Upper == "안녕하세요 반갑습니다" && reader.Lower is null, "open prefix appended in same slot");
            check(reader.Store.UpperLease!.ReadingUntilSeconds == 2.9, "visible growth protected");
        });
        Test("separator-does-not-extend-reading", check =>
        {
            var reader = Reader(); reader.Submit(Input("hello"), Fit);
            reader.Submit(Input("hello\n", 2, .4), Fit);
            check(reader.Upper == "hello" && reader.Store.UpperLease!.ReadingUntilSeconds == 2.5, "newline consumes without extending");
            reader.Tick(2.5, Fit); check(reader.Upper is null, "original deadline expires");
        });
        Test("two-line-correction-uses-current-version-leases", check =>
        {
            var reader = Reader(); reader.Submit(Input("old."), Fit);
            string next = "new corrected line one with text. second corrected line with text.";
            reader.Submit(Input(next, 2, .1), Fit); reader.Tick(2.5, Fit);
            check(reader.Upper == next[..32] && reader.Lower == next.Substring(32, 32), "new correction can fill both free slots");
        });
        Test("source-revision-freezes-and-withdraw-clears", check =>
        {
            var reader = Reader(); reader.Submit(Input("visible"), Fit);
            check(reader.Invalidate(1, .1), "source invalidated");
            check(reader.Upper == "visible" && reader.Store.UpperLease!.ReadingUntilSeconds == 2.5, "visible reading retained");
            reader.Submit(Input("revised.", 2, .2) with { SourceRevision = 2, SourceSpan = new(0, 64, 2) }, Fit);
            check(reader.Upper == "visible", "correction waits");
            reader.Tick(2.5, Fit); check(reader.Upper == "revised.", "correction resumes");
            check(reader.Invalidate(1, 2.6, true) && reader.Upper is null, "explicit withdrawal clears");
        });
        Test("stale-input-and-stop", check =>
        {
            var reader = Reader(); reader.Submit(Input("latest", 2), Fit);
            check(!reader.Submit(Input("old", 1, .1), Fit).Accepted && reader.Upper == "latest", "old input ignored");
            reader.Stop(); reader.Tick(10, Fit); check(reader.Upper is null && reader.Lower is null, "no stop replay");
        });
        Test("unicode-fit-repairs-invalid-boundary", check =>
        {
            var reader = Reader(); reader.Submit(Input("👨‍👩‍👧‍👦가"), _ => 1);
            check(reader.Upper == "👨‍👩‍👧‍👦" && reader.Lower == "가", "grapheme not split by fitter");
        });
        Test("proof-fallback-does-not-drop-body", check =>
        {
            var reader = Reader(); reader.Submit(Input("first. second."), Fit);
            reader.Submit(Input("changed. second.", 2, .1) with { SourceSpan = null }, Fit);
            reader.Tick(2.5, Fit); check(reader.Upper == "changed. second.", "unknown range receives full correction");
            check(reader.Store.Counters.GetValueOrDefault(CaptionScheduleReason.Reused) == 0, "no guessed reuse");
        });
        Test("bounded-queue-and-full-restore", check =>
        {
            var reader = Reader(); reader.Submit(Input(new string('x', 100)), Fit);
            for (ulong i = 2; i <= 7; i++) reader.Submit(Input($"sentence{i}.", at: .1 * i, segment: i), Fit);
            check(reader.Store.PendingUnits == 4 && reader.Store.Counters.GetValueOrDefault(CaptionScheduleReason.QueueCapacity) == 2, "overload accounted");
            reader.Tick(11, Fit); check(reader.Store.PendingUnits == 0, "waiting age bounded");
            reader.Stop(); check(reader.Store.StoredCharacters == 0, "stop releases retained text");
        });
        Test("separator-flag-cannot-shorten-lexical-reading", check =>
        {
            var store = new CaptionScheduleStore(); store.Begin(new("reader", 1, 1));
            var token = store.Submit(Input("abc")).Token!;
            store.Display(token, 0, 1, 0, 0, 2.5);
            check(store.Display(token, 1, 1, 0, .5, 2.5, true, true).Reason == CaptionScheduleReason.InvalidInput, "only whitespace may keep deadline");
            check(store.Snapshot(token.Identity)!.DisplayedThrough == 1, "invalid flag preserves coverage");
        });
        Test("leading-crlf-does-not-occupy-empty-slot", check =>
        {
            var reader = Reader(); reader.Submit(Input("\r\nhello\r\nworld"), text => text.IndexOf("\r\n", StringComparison.Ordinal) is var i && i >= 0 ? i + 2 : text.Length);
            check(reader.Upper == "hello" && reader.Lower == "world", "no blank slot or embedded CRLF");
            check(reader.Store.Counters.GetValueOrDefault(CaptionScheduleReason.SeparatorConsumed) == 1, "leading separator explicitly consumed");
            var input = Input("abc", segment: 2, at: .1);
            var token = reader.Store.Submit(input).Token!;
            check(reader.Store.ConsumeSeparator(token, 1).Reason == CaptionScheduleReason.InvalidInput, "lexical text cannot be consumed as separator");
        });
        Test("session-change-requires-explicit-begin", check =>
        {
            var reader = Reader(); reader.Submit(Input("visible"), Fit);
            reader.ValidateSession("reader", 1, 2, true);
            check(reader.Upper is null && reader.Lower is null, "old epoch cleared");
            check(reader.Submit(Input("new") with { Identity = new("reader", 1, 2, 1, 0) }, Fit).Reason == CaptionScheduleReason.NoSession, "new epoch not implicitly started");
        });
        Test("full-restore-keeps-other-final-queue", check =>
        {
            var reader = Reader(); reader.Submit(Input(new string('x', 100)), Fit);
            reader.Submit(Input("suffix.", 2, .1) with { Identity = new("reader", 1, 1, 1, 32), SourceSpan = new(32, 64, 1) }, Fit);
            for (ulong segment = 2; segment <= 5; segment++)
                reader.Submit(Input($"other{segment}.", at: segment * .1, segment: segment) with { IsFinal = true }, Fit);
            check(reader.Store.PendingUnits == 4, "four unrelated final inputs retained");
            reader.Submit(Input("restored whole.", 3, .6) with { IsFinal = true }, Fit);
            check(reader.Store.PendingUnits == 4, "restore inherits active work without dropping another final");
            reader.Tick(2.75, Fit);
            check(reader.Upper == "restored whole." && reader.Lower == "other2.", "restore and next final drain in order");
        });
        Test("virtual-thirty-minutes-with-reuse", check =>
        {
            var reader = Reader();
            for (ulong segment = 1; segment <= 300; segment++)
            {
                double at = (segment - 1) * 6;
                string old = $"이전 문장{segment}. 같은 뒷문장.";
                reader.Submit(Input(old, at: at, segment: segment), Fit);
                reader.Submit(Input($"새 문장{segment}. 같은 뒷문장.", 2, at + .1, segment), Fit);
                reader.Tick(at + 2.49, Fit);
                check(reader.Upper == old, "old line protected before deadline");
                reader.Tick(at + 2.5, Fit);
                check(reader.Upper == $"새 문장{segment}. " && reader.Lower is null, "only changed head assigned");
                check(reader.Store.Counters.GetValueOrDefault(CaptionScheduleReason.Reused) == segment, "rear sentence reuse counted once");
                check(reader.Store.PendingUnits == 0 && reader.Store.TrackedUnits <= 64 && reader.Store.DisplayedPieces <= 256 && reader.Store.StoredCharacters <= 131072, "reader and evidence stay bounded");
                reader.Tick(at + 5, Fit);
                check(reader.Upper is null && reader.Lower is null, "reading slots drain without replay");
            }
        });
        return cases.ToArray();
    }
}
