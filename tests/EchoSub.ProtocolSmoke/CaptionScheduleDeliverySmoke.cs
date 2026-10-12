using EchoSub.Desktop;

internal static class CaptionScheduleDeliverySmoke
{
    public static CaptionScheduleContractSmoke.Case[] Run()
    {
        var cases = new List<CaptionScheduleContractSmoke.Case>();
        void Test(string name, Action<Action<bool, string>> test)
        {
            int checks = 0; var errors = new List<string>();
            try { test((ok, detail) => { checks++; if (!ok) errors.Add(detail); }); }
            catch (Exception error) { errors.Add(error.ToString()); }
            cases.Add(new("delivery-" + name, checks, errors.ToArray()));
        }
        HistoryRecord Record(string text = "hello.", ulong request = 1, ulong revision = 1, ulong segment = 1, bool preview = false) =>
            new(1, 1, segment, revision, revision, 0, 16000, 0, 1, preview ? "Partial" : "Final", "Hello world.",
                null, "Done", text, null, request, "delivery", StableSource: preview ? "Hello" : "",
                TranslationIsPreview: preview, TranslationSource: preview ? "Hello" : "", TranslationPrefix: preview ? "Hello" : "");
        int Fit(string text) => Math.Min(32, text.Length);
        Test("deck-carries-selected-record-and-context", check =>
        {
            var deck = new CaptionDeck { LineReadingManaged = true };
            var cards = deck.Update([Record()], "delivery", 1, 1, true, 0);
            check(cards.ScheduleContext == new CaptionScheduleContext("delivery", 1, 1), "explicit context retained");
            check(cards.Delivered.Single().ScheduleInput?.SourceSpan == new CaptionSourceSpan(0, 12, 1), "source end comes from selected worker record");
            var adapter = new CaptionScheduleDelivery(); adapter.Apply(cards, 0, Fit);
            check(adapter.Reader.Upper == "hello.", "translation body rendered without status prefix");
            adapter.Apply(deck.Tick(.1), .1, Fit);
            check(adapter.Reader.Store.UpperLease!.ReadingUntilSeconds == 2.5, "deck tick does not extend reading");
        });
        Test("mixed-history-thirty-minute", check =>
        {
            var deck = new CaptionDeck { LineReadingManaged = true }; var adapter = new CaptionScheduleDelivery();
            string product = "delivery"; ulong session = 1, epoch = 1;
            int LineFit(string text) => text.IndexOf('\n') is var end && end >= 0 ? end + 1 : Math.Min(32, text.Length);
            void Apply(HistoryRecord record, double at) => adapter.Apply(deck.Update([record], product, session, epoch, true, at), at, LineFit);
            void Tick(double at) => adapter.Apply(deck.Tick(at), at, LineFit);
            for (ulong segment = 1; segment <= 300; segment++)
            {
                double at = (segment - 1) * 6;
                HistoryRecord Make(string text, ulong request = 1) => Record(text, request: request, segment: segment, preview: true) with
                { ProductSessionId = product, SessionId = session, Epoch = epoch, StableSource = "Hello world.",
                    TranslationSource = "Hello world.", TranslationPrefix = "Hello world." };
                var original = Make("old head.\nsame tail.");
                Apply(original, at); Apply(Make("new head.\nsame tail.", 2) with { TranslationIsPreview = false, SourceState = "Final" }, at + .1);
                switch ((segment - 1) % 6)
                {
                    case 0:
                        Tick(at + 2.5);
                        check(adapter.Reader.Upper == "new head." && adapter.Reader.Lower == "same tail.", "selected correction retains lower");
                        break;
                    case 1:
                        Apply(Make("latest head.\nsame tail.", 3) with { TranslationIsPreview = false, SourceState = "Final" }, at + .2);
                        Apply(original, at + .3); Tick(at + 2.5);
                        check(adapter.Reader.Upper == "latest head.", "old history cannot overwrite selected correction");
                        break;
                    case 2:
                        string status = new[] { "Failed", "Discarded", "Skipped" }[(int)((segment - 1) / 6 % 3)];
                        Apply(Make("", 3) with { SourceRevision = 2, AppliedSourceRevision = 2, SourceState = status,
                            TranslationState = "None", TranslationIsPreview = false }, at + .2);
                        Apply(original, at + .3); Tick(at + 2.5);
                        check(adapter.Reader.Upper is null && adapter.Reader.Lower is null, "terminal record clears and rejects old history");
                        break;
                    case 3:
                        adapter.Apply(deck.Update([], product, session, epoch, false, at + .2), at + .2, LineFit);
                        Tick(at + .3);
                        check(adapter.Reader.Upper is null && adapter.Reader.Store.StoredCharacters == 0, "stop clears pending correction");
                        session++;
                        Apply(Make("new session."), at + .4); Apply(original, at + .5);
                        check(adapter.Reader.Upper == "new session.", "old session history excluded after restart");
                        break;
                    case 4:
                        if ((segment - 1) / 6 % 2 == 0) epoch++;
                        else product = "delivery-" + segment;
                        Apply(Make("new context."), at + .2); Apply(original, at + .3);
                        check(adapter.Reader.Upper == "new context.", "epoch or product transition excludes old history");
                        break;
                    case 5:
                        var changed = Make("revised head.\nrevised tail.", 3) with { SourceRevision = 2, AppliedSourceRevision = 2,
                            Source = "Revised source.", TranslationIsPreview = false, SourceState = "Final" };
                        Apply(changed, at + .2); Tick(at + 2.5);
                        check(adapter.Reader.Upper is null && adapter.Reader.Lower == "same tail.", "source change protects previous lower");
                        Tick(at + 2.75);
                        check(adapter.Reader.Upper == "revised head." && adapter.Reader.Lower == "revised tail.", "source correction delivered completely");
                        break;
                }
                Tick(at + 5.75);
                check(adapter.Reader.Upper is null && adapter.Reader.Lower is null, "history cycle drains without replay");
                check(adapter.Reader.Store.PendingUnits == 0 && adapter.Reader.Store.TrackedUnits <= 64 &&
                    adapter.Reader.Store.DisplayedPieces <= 256 && adapter.Reader.Store.StoredCharacters <= 131072, "history delivery remains bounded");
            }
            adapter.Apply(deck.Update([], product, session, epoch, false, 1800), 1800, LineFit);
            Tick(1801);
            check(adapter.Reader.Store.StoredCharacters == 0 && adapter.Reader.Upper is null && adapter.Reader.Lower is null, "stopped history cannot replay at later tick");
        });
        Test("retained-preview-keeps-original-translation-identity", check =>
        {
            var deck = new CaptionDeck { LineReadingManaged = true }; var adapter = new CaptionScheduleDelivery();
            var first = Record("안녕", preview: true);
            adapter.Apply(deck.Update([first], "delivery", 1, 1, true, 0), 0, Fit);
            var pending = first with { SourceRevision = 2, AppliedSourceRevision = 2, TranslationState = "Pending", TranslationRequestId = null, Source = "Hello world. Next.", Translation = "" };
            var cards = deck.Update([pending], "delivery", 1, 1, true, .4);
            check(cards.Delivered.Single().SourceRevision == 2 && cards.Delivered.Single().ScheduleInput?.SourceRevision == 1, "ASR row and selected translation revision separated");
            adapter.Apply(cards, .4, Fit);
            check(adapter.Reader.Upper == "안녕" && adapter.Reader.Store.UpperLease!.ReadingUntilSeconds == 2.5, "retained preview does not reassign or reset deadline");
        });
        Test("partial-unit-and-full-final", check =>
        {
            var deck = new CaptionDeck { LineReadingManaged = true }; var adapter = new CaptionScheduleDelivery();
            var preview = Record("첫 부분.", preview: true) with { Source = "Hello world.", StableSource = "Hello world.", TranslationSource = "world.", TranslationPrefix = "Hello world." };
            var cards = deck.Update([preview], "delivery", 1, 1, true, 0);
            check(cards.Delivered.Single().ScheduleInput?.Identity.UnitStart == 6, "partial source start retained");
            adapter.Apply(cards, 0, Fit);
            adapter.Apply(deck.Update([Record("전체 최종.", request: 2)], "delivery", 1, 1, true, .1), .1, Fit);
            check(adapter.Reader.Upper == "첫 부분.", "full final waits for protected preview");
            adapter.Reader.Tick(2.5, Fit);
            check(adapter.Reader.Upper == "전체 최종.", "whole final returns to source start zero");
        });
        Test("malformed-span-never-guessed", check =>
        {
            var deck = new CaptionDeck { LineReadingManaged = true }; var adapter = new CaptionScheduleDelivery();
            var bad = Record(preview: true) with { TranslationPrefix = "not a source prefix" };
            adapter.Apply(deck.Update([bad], "delivery", 1, 1, true, 0), 0, Fit);
            check(adapter.Reader.Upper is null && adapter.Counters.GetValueOrDefault(CaptionDeliveryReason.MissingInput) == 1, "invalid translated source range rejected");
        });
        Test("identity-and-body-mismatch", check =>
        {
            var deck = new CaptionDeck { LineReadingManaged = true }; var original = deck.Update([Record()], "delivery", 1, 1, true, 0);
            var adapter = new CaptionScheduleDelivery(); var card = original.Delivered.Single();
            adapter.Apply(original with { Delivered = [card with { Identity = new("other", 1, 1, 1, 0) }] }, 0, Fit);
            check(adapter.Counters.GetValueOrDefault(CaptionDeliveryReason.IdentityMismatch) == 1 && adapter.Reader.Upper is null, "display identity must match selected record");
            adapter.Apply(original with { ResetReading = false, Delivered = [card with { Translation = "forged" }] }, .1, Fit);
            check(adapter.Counters.GetValueOrDefault(CaptionDeliveryReason.BodyMismatch) == 1 && adapter.Reader.Upper is null, "rendered label cannot supply arbitrary target");
        });
        foreach (var status in new[] { "Failed", "Discarded", "Skipped" }) Test("terminal-" + status, check =>
        {
            var deck = new CaptionDeck { LineReadingManaged = true }; var adapter = new CaptionScheduleDelivery();
            adapter.Apply(deck.Update([Record()], "delivery", 1, 1, true, 0), 0, Fit);
            adapter.Apply(deck.Update([Record(request: 2, revision: 2) with { SourceState = status, TranslationState = "None", Translation = "" }], "delivery", 1, 1, true, .1), .1, Fit);
            check(adapter.Reader.Upper is null && adapter.Reader.Lower is null, "terminal source clears matching slots");
        });
        Test("epoch-and-stop-reset", check =>
        {
            var deck = new CaptionDeck { LineReadingManaged = true }; var adapter = new CaptionScheduleDelivery();
            adapter.Apply(deck.Update([Record()], "delivery", 1, 1, true, 0), 0, Fit);
            adapter.Apply(deck.Update([Record("new epoch.") with { Epoch = 2 }], "delivery", 1, 2, true, .1), .1, Fit);
            check(adapter.Reader.Upper == "new epoch.", "new epoch clears old reading immediately");
            adapter.Apply(deck.Update([], "delivery", 1, 2, false, .2), .2, Fit);
            adapter.Apply(deck.Tick(3), 3, Fit); adapter.Reader.Tick(3, Fit);
            check(adapter.Reader.Upper is null && adapter.Reader.Store.TrackedUnits == 0, "stopped deck cannot replay retained events");
        });
        Test("missing-legacy-metadata-and-invalid-clock", check =>
        {
            var deck = new CaptionDeck { LineReadingManaged = true }; var cards = deck.Update([Record()], "delivery", 1, 1, true, 0);
            var adapter = new CaptionScheduleDelivery();
            adapter.Apply(cards with { Delivered = [cards.Delivered.Single() with { ScheduleInput = null }] }, 0, Fit);
            check(adapter.Counters.GetValueOrDefault(CaptionDeliveryReason.MissingInput) == 1, "old diagnostic card lacks proof, no guessing");
            adapter.Apply(cards, double.NaN, Fit);
            check(adapter.Counters.GetValueOrDefault(CaptionDeliveryReason.InvalidTime) == 1, "invalid receipt clock rejected");
            adapter.Apply(cards with { OrderedDelivery = false }, 0, Fit);
            check(adapter.Counters.GetValueOrDefault(CaptionDeliveryReason.UnsupportedOrder) == 1, "unordered snapshots do not fabricate delivery events");
        });
        Test("diagnostics-rejection-and-clear-resume", check =>
        {
            var deck = new CaptionDeck { LineReadingManaged = true }; var cards = deck.Update([Record()], "delivery", 1, 1, true, 0);
            var adapter = new CaptionScheduleDelivery(); var events = new List<CaptionReadingObservation>(); adapter.Observed += events.Add;
            adapter.Apply(cards with { Delivered = [cards.Delivered.Single() with { ScheduleInput = null }] }, 0, Fit);
            check(events.Single().Kind == "InputRejected" && events.Single().Reason == "DeliveryMissingInput", "adapter rejection reaches unified event stream");
            adapter.Apply(cards, .1, Fit); adapter.Clear();
            adapter.Apply(cards with { ResetReading = false }, .2, Fit);
            check(adapter.Reader.Upper == "hello.", "same context resumes after explicit clear");
            adapter.Apply(new(null, null), .3, Fit);
            check(adapter.Reader.Upper is null && adapter.Reader.Store.TrackedUnits == 0, "empty application clear stops scheduled reader");
        });
        Test("diagnostics-block-duration-deduplicated", check =>
        {
            var reader = new CaptionScheduledLines(); reader.Begin(new("delivery", 1, 1));
            var events = new List<CaptionReadingObservation>(); reader.Observed += events.Add;
            CaptionScheduleInput Input(string text, ulong request, double at) => new(new("delivery", 1, 1, 1, 0), 1, request, false, new(0, 64, 1), text, at);
            reader.Submit(Input("이전 문장. 같은 뒷문장.", 1, 0), Fit);
            reader.Submit(Input("새 문장. 같은 뒷문장.", 2, .1), Fit);
            for (int i = 2; i < 25; i++) reader.Tick(i / 10d, Fit);
            check(events.Count(e => e.Kind == "Blocked") == 1, "same block is not emitted each tick");
            reader.Tick(2.5, Fit);
            check(events.Single(e => e.Kind == "BlockEnded").BlockedSeconds == 2.4, "block duration uses one desktop clock");
            check(events.Single(e => e.Kind == "Reused").ReusedCharacters == "같은 뒷문장.".Length, "reuse emits only exact count");
            var lease = reader.Store.UpperLease;
            reader.Tick(2.6, Fit); check(reader.Store.UpperLease == lease, "logging cannot extend reading protection");
            reader.Submit(Input("old", 1, 2.7), Fit);
            check(events.Last().Kind == "InputRejected" && events.Last().Reason == "StaleVersion", "store rejection reaches unified event stream");
        });
        Test("diagnostics-stop-closes-active-block", check =>
        {
            var reader = new CaptionScheduledLines(); reader.Begin(new("delivery", 1, 1));
            var events = new List<CaptionReadingObservation>(); reader.Observed += events.Add;
            CaptionScheduleInput Input(string text, ulong request, double at) => new(new("delivery", 1, 1, 1, 0), 1, request, false, new(0, 64, 1), text, at);
            reader.Submit(Input("이전 문장.", 1, 0), Fit); reader.Submit(Input("교정 문장.", 2, .1), Fit);
            reader.Tick(.6, Fit); reader.Stop(); reader.Stop();
            check(events.Count(e => e.Kind == "BlockEnded") == 1, "stop closes block once");
            check(events.Single(e => e.Kind == "BlockEnded").BlockedSeconds == .5, "stop records duration through last valid clock");
        });
        return cases.ToArray();
    }
}
