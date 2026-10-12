using EchoSub.Desktop;

internal static class CaptionScheduleContractSmoke
{
    public sealed record Case(string Name, int Checks, string[] Failures);
    public sealed record Report(bool Passed, int Checks, Case[] Cases, string[] Excluded);
    private static readonly CaptionScheduleContext Owner = new("contract", 1, 1);
    private static CaptionScheduleInput Input(string text = "ABCD", ulong segment = 1, ulong revision = 1,
        ulong request = 1, double at = 0, bool final = false, int start = 0, int end = 32, bool known = true) =>
        new(new("contract", 1, 1, segment, start), revision, request, final,
            known ? new(start, end, revision) : null, text, at);
    private static CaptionScheduleStore Store() { var store = new CaptionScheduleStore(); store.Begin(Owner); return store; }
    public static Report Run()
    {
        var results = new List<Case>();
        void Test(string name, Action<Action<bool, string>> run)
        {
            int checks = 0;
            var failures = new List<string>();
            try { run((ok, detail) => { checks++; if (!ok) failures.Add(detail); }); }
            catch (Exception error) { failures.Add(error.GetType().Name + ": " + error.Message); }
            results.Add(new(name, checks, failures.ToArray()));
        }
        void Bounds(CaptionScheduleStore store, Action<bool, string> check)
        {
            check(store.PendingUnits <= 4, "waiting <= 4");
            check(store.TrackedUnits <= 64, "units <= 64");
            check(store.DisplayedPieces <= 256, "pieces <= 256");
            check(store.StoredCharacters <= 131072, "text <= 131072 UTF-16 units");
        }
        CaptionScheduleToken Add(CaptionScheduleStore store, CaptionScheduleInput input, Action<bool, string> check)
        {
            var result = store.Submit(input); check(result.Accepted && result.Token is not null, "input accepted");
            return result.Token!;
        }
        Test("identity-and-clock", check =>
        {
            var store = new CaptionScheduleStore();
            check(store.Submit(Input()).Reason == CaptionScheduleReason.NoSession, "session required");
            check(!store.Begin(Owner with { Session = 0 }), "zero session rejected");
            check(store.Begin(Owner), "explicit Begin");
            check(store.Submit(Input() with { Identity = new("old", 9, 9, 1, 0), AtSeconds = 1e9 }).Reason == CaptionScheduleReason.WrongContext, "old context rejected");
            var token = Add(store, Input(at: 1), check);
            check(store.Submit(Input(at: double.NaN)).Reason == CaptionScheduleReason.InvalidTime, "NaN rejected");
            check(store.Submit(Input(at: .9)).Reason == CaptionScheduleReason.InvalidTime, "time reversal rejected");
            check(store.Snapshot(token.Identity)?.Latest?.AtSeconds == 1, "wrong context cannot advance clock or replace input");
        });
        Test("source-range-and-input-limits", check =>
        {
            var store = Store();
            foreach (var input in new[] { Input(revision: 0), Input(request: 0), Input(segment: 0), Input(start: -1), Input(start: 16385, known: false),
                Input(end: 0), Input(end: 16385), Input(" "), Input(new string('가', 16385)),
                Input() with { SourceSpan = new(1, 32, 1) }, Input() with { SourceSpan = new(0, 32, 2) } })
                check(store.Submit(input).Reason == CaptionScheduleReason.InvalidInput, "invalid input rejected");
            Add(store, Input(new string('가', 16384)), check); Bounds(store, check);
        });
        Test("record-source-span-adapter", check =>
        {
            var record = new HistoryRecord(1, 1, 1, 1, 1, 0, 16000, 0, 1, "Partial", "hello world", null,
                "Done", "안녕", null, 1, "contract", StableSource: "hello world", TranslationIsPreview: true,
                TranslationSource: "world", TranslationPrefix: "hello world");
            var input = CaptionScheduleInputs.FromRecord(record, 0);
            check(input?.SourceSpan == new CaptionSourceSpan(6, 11, 1), "half-open UTF-16 source span");
            check(input?.Identity.UnitStart == 6, "identity matches source start");
            check(CaptionScheduleInputs.FromRecord(record with { TranslationPrefix = "hi" }, 0) is null, "negative/mismatched source span rejected");
            check(CaptionScheduleInputs.FromRecord(record with { AppliedSourceRevision = 2 }, 0) is null, "unapplied target rejected");
            check(CaptionScheduleInputs.FromRecord(record with { TranslationSource = "" }, 0)?.SourceSpan is null, "legacy span is unknown");
            check(CaptionScheduleInputs.FromRecord(record with { SourceState = "Final", TranslationIsPreview = false }, 0)?.IsFinal == true, "final span from full source");
            check(CaptionScheduleInputs.FromRecord(record with { Source = "", TranslationIsPreview = false, SourceState = "Final" }, 0) is null, "empty source rejected");
            check(CaptionScheduleInputs.FromRecord(record with { Translation = new string('x', 16385) }, 0) is null, "adapter bounds before grapheme parsing");
            var unicode = record with { Source = "A👨‍👩‍👧‍👦가", TranslationPrefix = "A👨‍👩‍👧‍👦가", TranslationSource = "가" };
            check(CaptionScheduleInputs.FromRecord(unicode, 0)?.Identity.UnitStart == unicode.Source.Length - 1, "grapheme source boundary");
            check(CaptionScheduleInputs.FromRecord(unicode with { TranslationSource = unicode.Source[2..] }, 0) is null, "surrogate/ZWJ interior rejected");
        });
        Test("version-and-final-contract", check =>
        {
            var store = Store(); var token = Add(store, Input(revision: 2, request: 3, final: true), check);
            check(store.Submit(Input(revision: 1, request: 9, at: .1)).Reason == CaptionScheduleReason.StaleVersion, "old source revision");
            check(store.Submit(Input(revision: 2, request: 2, at: .2)).Reason == CaptionScheduleReason.StaleVersion, "old request");
            check(store.Submit(Input(revision: 2, request: 3, final: true, at: .3)).Reason == CaptionScheduleReason.Unchanged, "exact replay does not refresh queue age");
            check(store.Submit(Input("changed", revision: 2, request: 3, final: true, at: .4)).Reason == CaptionScheduleReason.VersionConflict, "conflicting same version");
            check(store.Submit(Input(revision: 3, request: 4, at: .5)).Reason == CaptionScheduleReason.FinalRegression, "final cannot become draft");
            check(store.Next?.Token == token, "rejections keep existing input");
        });
        Test("snapshot-and-replacement", check =>
        {
            var store = Store(); var old = Add(store, Input(), check); var snapshot = store.Next!;
            var next = Add(store, Input("NEW", request: 2, at: .1), check);
            check(snapshot.Latest?.Target == "ABCD", "snapshot remains immutable");
            check(store.Next?.Latest?.Target == "NEW" && store.PendingUnits == 1, "same unit has one latest unread input");
            check(store.Display(old, 0, 1, 0, .2, 2.7).Reason == CaptionScheduleReason.StaleToken, "old token cannot display");
            check(store.Complete(next).Reason == CaptionScheduleReason.IncompleteDisplay, "unshown target cannot complete");
        });
        Test("display-and-reading-protection", check =>
        {
            var store = Store(); var token = Add(store, Input(), check);
            check(store.Display(token, 0, 2, 0, 0, 2.5).Accepted, "upper assigned");
            check(store.Display(token, 2, 2, 1, 0, 2.5).Accepted, "lower assigned");
            check(store.LowerLease?.ReadingUntilSeconds == 2.75, "lower stagger preserves order");
            check(store.Complete(token).Accepted, "actual coverage completes");
            var next = Add(store, Input("WXYZ", revision: 2, request: 2, at: .1), check);
            check(store.Display(next, 0, 4, 0, .2, 2.7).Reason == CaptionScheduleReason.ReadingProtected, "correction cannot replace protected text");
            check(store.Next?.Comparison?.Reason == CaptionScheduleReason.TargetChanged, "correction candidate retained");
            check(store.Snapshot(token.Identity)?.Displayed[0].Text == "AB", "shown history retained");
            store.Advance(2.5);
            check(store.UpperLease is null && store.LowerLease is not null, "independent fixed leases");
            check(store.Display(next, 0, 4, 0, 2.5, 5).Reason == CaptionScheduleReason.ReadingProtected, "related lower lease blocks correction");
            store.Advance(2.75);
            check(store.Display(next, 0, 4, 0, 2.75, 5.25).Accepted, "correction after read");
            check(store.Complete(next).Accepted, "correction completes"); Bounds(store, check);
        });
        Test("append-and-unread-tail", check =>
        {
            var store = Store(); var token = Add(store, Input("ABxx"), check);
            check(store.Display(token, 0, 2, 0, 0, 2.5).Accepted, "shown prefix");
            var next = Add(store, Input("ABzz", request: 2, at: .1), check);
            check(store.Next?.DisplayedThrough == 2 && store.Next.Comparison is null, "only unread tail changed");
            check(store.Display(next, 2, 2, 0, .1, 2.6, append: true).Accepted, "append without prefix replacement");
            check(store.Next?.Displayed.Count == 1 && store.Next.Displayed[0].Text == "ABzz", "append coalesces one ledger piece");
            check(store.StoredCharacters == 8, "latest + shown payload accounting");
            check(store.Complete(next).Accepted, "append coverage");
        });
        Test("source-revision-does-not-clear-or-extend-reading", check =>
        {
            var store = Store(); var token = Add(store, Input(), check);
            store.Display(token, 0, 2, 0, 0, 2.5);
            store.Invalidate(Owner, 1, .064);
            check(store.UpperLease?.ReadingUntilSeconds == 2.5, "source revision keeps deadline");
            check(store.Next is null && store.Snapshot(token.Identity)?.Latest is null, "old unread tail cancelled");
            check(store.Snapshot(token.Identity)?.Displayed[0].SourceSpan == new CaptionSourceSpan(0, 32, 1),
                "displayed source span survives unread cancellation");
            store.Invalidate(Owner, 1, .2);
            check(store.UpperLease?.ReadingUntilSeconds == 2.5, "repeat invalidation does not extend deadline");
            check(store.Display(token, 2, 2, 0, .3, 2.8, append: true).Reason == CaptionScheduleReason.StaleToken, "cancelled input cannot append");
            var next = Add(store, Input("NEW", revision: 2, request: 2, at: .4), check);
            check(store.Next?.Comparison?.Reason == CaptionScheduleReason.SourceRevised, "source revision requires comparison");
            check(store.Display(next, 0, 3, 1, .4, 2.9).Reason == CaptionScheduleReason.ReadingProtected, "new correction waits related old line");
        });
        Test("withdraw-and-session-reset", check =>
        {
            var store = Store(); var token = Add(store, Input(), check);
            store.Display(token, 0, 4, 0, 0, 2.5);
            check(!store.Invalidate(Owner with { Epoch = 2 }, 1, 100), "old owner cannot withdraw or poison clock");
            store.Invalidate(Owner, 1, .1, withdraw: true);
            check(store.UpperLease is null, "explicit terminal withdrawal clears");
            check(store.Submit(Input(request: 2, at: .2)).Reason == CaptionScheduleReason.Withdrawn, "terminal tombstone blocks revival");
            store.Begin(Owner); var fresh = Add(store, Input(), check);
            check(fresh.Generation != token.Generation, "Begin changes token generation even for same identity");
            check(store.Display(token, 0, 4, 0, 0, 2.5).Reason == CaptionScheduleReason.StaleToken, "old session token rejected");
            store.Stop(); check(store.TrackedUnits == 0 && store.StoredCharacters == 0, "stop releases stored state");
            check(store.Submit(Input()).Reason == CaptionScheduleReason.NoSession, "stop cannot restart implicitly");
        });
        Test("disjoint-and-overlapping-source-units", check =>
        {
            var store = Store(); Add(store, Input("later", start: 8, end: 12), check);
            var first = Add(store, Input("earlier", request: 2, start: 0, end: 4, at: .1), check);
            check(store.PendingUnits == 2, "known disjoint units retained");
            check(store.Snapshot(first.Identity)?.Comparison is null, "disjoint is not overlap");
            var full = Add(store, Input("whole", request: 3, final: true, end: 12, at: .2), check);
            check(store.PendingUnits == 1, "full overlapping final replaces unread suffix");
            check(store.Snapshot(full.Identity)?.Comparison?.Reason == CaptionScheduleReason.SourceOverlap, "explicit overlap reason");
            store = Store(); Add(store, Input("old", start: 8, end: 12), check);
            var unknown = Add(store, Input("full", revision: 2, request: 2, final: true, end: 12, at: .1), check);
            check(store.Snapshot(unknown.Identity)?.Comparison?.Reason == CaptionScheduleReason.UnknownSourceRange, "different source revisions cannot prove span relation");
        });
        Test("queue-capacity-and-age", check =>
        {
            var store = Store(); var first = Add(store, Input(segment: 1), check);
            for (ulong i = 2; i <= 5; i++) { Add(store, Input(segment: i), check); Bounds(store, check); }
            check(store.PendingUnits == 4 && store.Counters.GetValueOrDefault(CaptionScheduleReason.QueueCapacity) == 1, "bounded queue reports one drop");
            check(store.Submit(Input(at: 1)).Reason == CaptionScheduleReason.CancelledVersion, "same cancelled version cannot re-enter");
            check(store.Display(first, 0, 4, 0, 1, 3.5).Reason == CaptionScheduleReason.StaleToken, "dropped token rejected");
            store = Store(); Add(store, Input(), check);
            store.Advance(10); check(store.PendingUnits == 1, "ten second boundary");
            check(store.Submit(Input(at: 10)).Reason == CaptionScheduleReason.Unchanged, "duplicate keeps original since");
            store.Advance(10.001); check(store.PendingUnits == 0, "age budget does not reset");
            check(store.Counters.GetValueOrDefault(CaptionScheduleReason.QueueAge) == 1, "age drop reported");
        });
        Test("overlap-protects-other-source-unit", check =>
        {
            var store = Store(); var suffix = Add(store, Input("suffix", start: 8, end: 12), check);
            check(store.Display(suffix, 0, 6, 0, 0, 2.5).Accepted, "partial unit shown");
            check(store.Complete(suffix).Accepted, "partial unit drained");
            var full = Add(store, Input("whole", request: 2, final: true, end: 12, at: .1), check);
            check(store.Display(full, 0, 5, 1, .1, 2.6).Reason == CaptionScheduleReason.ReadingProtected,
                "overlapping whole correction waits related partial unit");
            store.Advance(2.5);
            check(store.Display(full, 0, 5, 0, 2.5, 5).Accepted, "overlapping final drains after read");
            check(store.Complete(full).Accepted, "overlap final completes");
        });
        Test("active-unit-restoration-keeps-unrelated-final-inputs", check =>
        {
            var store = Store(); var partial = Add(store, Input("suffix-tail", start: 8, end: 16), check);
            store.Display(partial, 0, 6, 0, 0, 2.5);
            for (ulong i = 2; i <= 5; i++) Add(store, Input("later", segment: i, at: .1, final: true), check);
            var full = Add(store, Input("whole-final", request: 2, final: true, end: 16, at: .2), check);
            check(store.Next?.Token == full && store.PendingUnits == 4, "restored full input inherits active work position");
            check(store.Counters.GetValueOrDefault(CaptionScheduleReason.QueueCapacity) == 0, "restoration does not drop unrelated finals");
            foreach (ulong i in new ulong[] { 2, 3, 4, 5 })
                check(store.Snapshot(new("contract", 1, 1, i, 0))?.HasUnread == true, "unrelated final retained");
            check(store.Submit(Input("revived-preview", request: 3, start: 8, end: 16, at: .3)).Reason == CaptionScheduleReason.FinalRegression,
                "whole final seals other preview units");
        });
        Test("late-segment-is-explicit", check =>
        {
            var store = Store(); var token = Add(store, Input("new", segment: 2, final: true), check);
            store.Display(token, 0, 3, 0, 0, 2.5); store.Complete(token);
            check(store.Submit(Input("late-final", at: .1, final: true)).Reason == CaptionScheduleReason.LateUnit,
                "late segment differs from retired history");
            check(store.UpperLease?.Identity == token.Identity, "late result cannot clear current reading");
            check(store.Counters.GetValueOrDefault(CaptionScheduleReason.LateUnit) == 1, "late result counted");
        });
        Test("full-restoration-cannot-complete-from-old-prefix-coverage", check =>
        {
            var store = Store(); var first = Add(store, Input("original.", end: 8), check);
            store.Display(first, 0, 9, 0, 0, 2.5); store.Complete(first);
            var suffix = Add(store, Input("wrong.", request: 2, start: 8, end: 16, at: .1), check);
            store.Display(suffix, 0, 6, 1, .1, 2.6); store.Complete(suffix);
            var full = Add(store, Input("original.", request: 3, final: true, end: 16, at: .2), check);
            check(store.Next?.DisplayedThrough == 0, "source overlap resets target coverage even when original target text matches");
            check(store.Complete(full).Reason == CaptionScheduleReason.IncompleteDisplay, "old prefix coverage cannot hide final deletion correction");
            store.Advance(2.75);
            check(store.Display(full, 0, 9, 0, 2.75, 5.25).Accepted, "full correction actually reassigned");
            check(store.Complete(full).Accepted, "restored full input completes after assignment");
        });
        Test("same-segment-units-retire-as-a-group", check =>
        {
            var store = Store();
            for (int start = 0; start < 64; start++)
            {
                Add(store, Input("x", request: (ulong)start + 1, start: start, end: start + 1), check);
                Bounds(store, check);
            }
            check(store.Submit(Input(segment: 2, at: .1)).Reason == CaptionScheduleReason.StorageCapacity,
                "cannot retire part of a segment with live units");
            var full = Add(store, Input(new string('x', 64), request: 100, final: true, end: 64, at: .2), check);
            check(store.PendingUnits == 1, "full final replaces the pending source units");
            check(store.Display(full, 0, 64, 0, .2, 2.7).Accepted, "whole final assigned");
            check(store.Complete(full).Accepted, "whole final completed");
            store.Advance(3); Add(store, Input(segment: 2, at: 3), check);
            check(store.TrackedUnits == 1, "whole old segment retired together");
            check(store.Submit(Input(request: 101, at: 3.1, start: 8, end: 9)).Reason == CaptionScheduleReason.Retired,
                "retired sibling unit cannot revive");
        });
        Test("atomic-unit-capacity-and-prefix-retirement", check =>
        {
            var store = Store(); var active = Add(store, Input("AAAA"), check);
            store.Display(active, 0, 1, 0, 0, 2.5);
            for (ulong i = 2; i <= 64; i++) { Add(store, Input(segment: i, at: i / 1000d), check); Bounds(store, check); }
            var prior = store.Next; int chars = store.StoredCharacters; var lease = store.UpperLease;
            check(store.Submit(Input(segment: 65, at: .1)).Reason == CaptionScheduleReason.StorageCapacity, "cannot retire prefix across live active unit");
            check(store.PendingUnits == 4 && store.StoredCharacters == chars && store.Next?.Token == prior?.Token && store.UpperLease == lease,
                "failed reservation keeps unread input and protected lease");
            check(store.Display(active, 1, 3, 0, .1, 2.6, append: true).Accepted, "active survives rejected reservation");
            check(store.Complete(active).Accepted, "active drains"); store.Advance(3);
            Add(store, Input(segment: 65, at: 3), check);
            check(store.TrackedUnits == 64 && store.Snapshot(active.Identity) is null, "retire whole prefix after reading");
            check(store.Submit(Input(at: 3.1)).Reason == CaptionScheduleReason.Retired, "retirement watermark blocks revival");
        });
        Test("utf16-display-boundaries-and-order", check =>
        {
            var store = Store(); var token = Add(store, Input("👨‍👩‍👧‍👦é가"), check);
            check(store.Display(token, 0, 1, 0, 0, 2.5).Reason == CaptionScheduleReason.InvalidInput, "surrogate/ZWJ cannot split");
            check(store.Display(token, 0, 11, 0, 0, 2.5).Accepted, "whole family grapheme");
            check(store.Display(token, 11, 1, 0, 0, 2.5, append: true).Reason == CaptionScheduleReason.InvalidInput, "combining mark cannot split");
            check(store.Display(token, 11, 2, 1, 0, 2.5).Accepted, "whole combining grapheme");
            check(store.Display(token, 13, 1, 0, .1, 2.6, append: true).Reason == CaptionScheduleReason.ReadingProtected,
                "cannot append a noncontiguous tail above lower line");
            check(store.Complete(token).Reason == CaptionScheduleReason.IncompleteDisplay, "missing tail remains unread");
        });
        Test("payload-memory-retirement", check =>
        {
            var store = Store();
            for (ulong i = 1; i <= 40; i++)
            {
                double at = i * 3;
                store.Advance(at); var token = Add(store, Input(new string('가', 16384), segment: i, at: at, final: true), check);
                check(store.Display(token, 0, 16384, 0, at, at + 2.5).Accepted, "bounded maximum payload display");
                check(store.Complete(token).Accepted, "maximum payload covered"); Bounds(store, check);
            }
            check(store.Counters.GetValueOrDefault(CaptionScheduleReason.Retired) > 0, "text pressure retires inactive prefix groups");
        });
        Test("bounded-ledger-and-incomplete-history", check =>
        {
            var store = Store();
            for (ulong segment = 1; segment <= 5; segment++)
                for (ulong request = 1; request <= 70; request++)
                {
                    double at = ((segment - 1) * 70 + request) * 3;
                    store.Advance(at); string text = $"line-{request}.";
                    var token = Add(store, Input(text, segment: segment, request: request, at: at, final: true), check);
                    check(store.Display(token, 0, text.Length, 0, at, at + 2.5).Accepted, "revision assigned after read");
                    check(store.Complete(token).Accepted, "coverage survives history retirement");
                    check(store.Snapshot(token.Identity)!.Displayed.Count <= 64, "per-unit ledger bound"); Bounds(store, check);
                }
            check(store.DisplayedPieces == 256, "global ledger bound");
            check(store.Counters.GetValueOrDefault(CaptionScheduleReason.HistoryTrimmed) > 0, "history trimming reported");
            var key = new CaptionIdentity("contract", 1, 1, 5, 0);
            check(store.Snapshot(key)?.HistoryComplete == false, "trimmed history cannot prove complete LCS comparison");
        });
        Test("virtual-30-minutes", check =>
        {
            var store = Store();
            for (ulong i = 1; i <= 600; i++)
            {
                double at = (i - 1) * 3; store.Advance(at);
                var token = Add(store, Input("sentence.", segment: i, at: at, final: true), check);
                check(store.Display(token, 0, 9, 0, at, at + 2.5).Accepted, "ordered caption assigned");
                check(store.Complete(token).Accepted, "ordered caption completed"); Bounds(store, check);
            }
            store.Advance(1803);
            check(store.TrackedUnits == 64 && store.PendingUnits == 0 && store.UpperLease is null, "virtual session drained and bounded");
            check(store.Counters.GetValueOrDefault(CaptionScheduleReason.QueueCapacity) == 0, "no normal load capacity drop");
        });
        results.AddRange(CaptionSentenceReuseSmoke.Run());
        results.AddRange(CaptionScheduledLinesSmoke.Run());
        results.AddRange(CaptionScheduleDeliverySmoke.Run());
        results.AddRange(OverlayPlacementSmoke.Run());
        return new(results.All(result => result.Failures.Length == 0), results.Sum(result => result.Checks), results.ToArray(),
            ["production scheduler wiring", "candidate rendering", "natural speech/model inference", "general semantic quality", "real 30-minute session", "macOS device"]);
    }
}
