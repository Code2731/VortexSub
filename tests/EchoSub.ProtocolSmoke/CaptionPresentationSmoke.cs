using EchoSub.Desktop;

internal static class CaptionPresentationSmoke
{
    public static void Run()
    {
        var record = new HistoryRecord(1, 2, 3, 4, 4, 0, 16000, 0, 1, "Final", "Source", null,
            "Pending", "", null, 7, "uuid");
        var presentation = new CaptionPresentation();
        var assertions = 0;
        (string? Source, string? Translation) Show(HistoryRecord r, double time, bool running = true) =>
            presentation.Update([r], "uuid", 1, 2, running, time);
        void Check(bool condition, string label)
        {
            if (!condition) throw new IOException("Caption fixture failed: " + label);
            assertions++;
        }
        Check(Show(record, 0) == ("Source", null), "pending source retained");
        var done = record with { TranslationState = "Done", Translation = "번역" };
        Check(Show(done, 4) == ("Source", "번역"), "translation attached to current source");
        Check(Show(done, 5) == (null, null), "translation never renews lifetime");
        Check(Show(done, 6) == (null, null), "expired caption never resurrects");
        var revised = done with { SourceRevision = 5, AppliedSourceRevision = 5 };
        Check(Show(revised, 7) == ("Source", "번역"), "new applied revision renews lifetime");
        Check(Show(revised with { TranslationState = "Failed", TranslationReason = "Deadline" }, 8) == ("Source", null), "failed source retained");
        Check(Show(revised with { TranslationState = "Bypassed" }, 8) == ("Source", null), "bypass has no duplicate translation");
        Check(Show(revised with { TranslationRequestId = null }, 8).Translation is null, "request identity required");
        Check(Show(revised with { SourceRevision = 6 }, 8).Translation is null, "pending revision cannot display stale translation");
        Check(Show(revised with { SourceState = "Partial" }, 8).Translation is null, "partial never translated");
        Check(Show(revised with { ProductSessionId = "old" }, 8) == (null, null), "old UUID rejected");
        Check(Show(revised with { Epoch = 1 }, 8) == (null, null), "old epoch rejected");
        Check(Show(revised, 8, false) == (null, null), "pause hides both lines");
        Check(CaptionPresentation.HistoryText(done).Contains("번역 Done\n번역"), "history translation text");
        var preview = revised with { SourceState = "Partial", StableSource = "Source", TranslationIsPreview = true };
        Check(Show(preview, 8).Translation == "[임시 번역] 번역", "preview explicitly provisional");
        Check(Show(preview with { SourceRevision = 6, StableSource = "", TranslationState = "None", Translation = "", TranslationIsPreview = false }, 8).Translation == "[임시 번역] 번역", "displayed preview retained while next revision waits");
        Check(Show(preview with { Source = "Corrected", StableSource = "different" }, 8).Translation is null, "corrected prefix clears displayed preview");
        Check(Show(preview with { SourceState = "FinalPending" }, 8).Translation is null, "preview hidden during final decode");
        Check(Show(preview with { SourceState = "Final" }, 8).Translation is null, "preview cannot become final implicitly");
        Show(preview, 8);
        Check(Show(preview, 13).Translation is null, "repeated preview does not renew display lifetime");
        Check(CaptionPresentation.HistoryText(preview).Contains("임시 번역 Done"), "history distinguishes preview");
        var deck = new CaptionDeck();
        var unit = preview with { Source = "Go left now. Do not open the door.", StableSource = "Go left now. Do not open the door.",
            TranslationSource = "Go left now.", TranslationPrefix = "Go left now.", Translation = "왼쪽으로 가자." };
        var cards = deck.Update([unit], "uuid", 1, 2, true, 0);
        Check(cards.Current?.Source == "[인식 중] Go left now." && cards.Previous is null, "source line matches translated unit");
        var nextUnit = unit with { TranslationSource = "Do not open the door.", TranslationPrefix = unit.Source,
            TranslationRequestId = 8, Translation = "문을 열지 마라." };
        cards = deck.Update([nextUnit], "uuid", 1, 2, true, 0.2);
        Check(cards.Previous is null && cards.Current?.Translation == "[임시 번역] 왼쪽으로 가자.", "unit transition holds current input before reading deadline");
        cards = deck.Update([nextUnit], "uuid", 1, 2, true, 0.3);
        Check(cards.Previous is null && cards.Current?.Translation == "[임시 번역] 왼쪽으로 가자.", "no second input card or premature replacement");
        cards = deck.Update([nextUnit], "uuid", 1, 2, true, 1.3);
        Check(cards.Previous is null && cards.Current?.Translation == "[임시 번역] 문을 열지 마라.", "latest unit enters same input position after deadline");
        var corrected = unit with { Source = "Go right now. Do not open the door.", StableSource = "Go right now. Do not open the door.",
            TranslationSource = "Go right now.", TranslationPrefix = "Go right now.", TranslationRequestId = 9, Translation = "오른쪽으로 가자." };
        cards = deck.Update([corrected], "uuid", 1, 2, true, 0.4);
        Check(cards.Previous is null && cards.Current?.Translation == "[임시 번역] 오른쪽으로 가자.", "earlier correction removes stale reading units");
        Check(deck.Update([corrected], "uuid", 1, 2, false, 0.5) == new CaptionCards(null, null), "pause clears both unit cards");
        deck.Update([record], "uuid", 1, 2, true, 1);
        Check(deck.Update([done], "uuid", 1, 2, true, 1.1).Current?.Translation == "번역", "first translation bypasses replacement delay");
        Check(deck.Update([done], "uuid", 1, 2, true, 5.2).Current is null, "unchanged final caption does not resurrect");
        Check(CaptionDeck.ReadingSeconds("짧음") == 4 && CaptionDeck.ReadingSeconds(new string('가', 100)) == 10, "reading duration has both bounds");
        var responsive = new CaptionDeck();
        var timings = new List<CaptionUpdateTiming>();
        responsive.Applied += timings.Add;
        responsive.Update([unit], "uuid", 1, 2, true, 0);
        var draftRevision = unit with { TranslationRequestId = 10, Translation = "왼쪽 길로 가자." };
        Check(responsive.Update([draftRevision], "uuid", 1, 2, true, 0.1).Current?.Translation == "[임시 번역] 왼쪽으로 가자.", "draft has a bounded short replacement delay");
        responsive.Update([draftRevision], "uuid", 1, 2, true, 0.15);
        Check(responsive.Tick(0.2).Current?.Translation == "[임시 번역] 왼쪽으로 가자.", "draft not replaced before short deadline");
        Check(responsive.Tick(0.3).Current?.Translation == "[임시 번역] 왼쪽으로 가자.", "draft retained through old short deadline");
        Check(responsive.Tick(1.3).Current?.Translation == "[임시 번역] 왼쪽 길로 가자.", "draft updated after current 1.25 second deadline");
        Check(Math.Abs(timings[^1].DeferredSeconds - 1.2) < 0.000001 && timings[^1].TranslationRecord?.TranslationRequestId == 10,
            "repeated snapshot preserves candidate receipt time and request identity");
        var otherSegment = unit with { SegmentId = 4, TranslationRequestId = 11, Translation = "다음 자막" };
        responsive.Update([draftRevision, otherSegment], "uuid", 1, 2, true, 1.4);
        var readingRevision = draftRevision with { TranslationRequestId = 12, Translation = "읽는 중인 이전 자막 수정" };
        responsive.Update([readingRevision, otherSegment], "uuid", 1, 2, true, 1.5);
        Check(responsive.Tick(1.6).Previous is null && responsive.Tick(1.6).Current?.Translation == "[임시 번역] 왼쪽 길로 가자.", "old history cannot repaint latest input");
        Check(responsive.Update([readingRevision, otherSegment], "uuid", 1, 2, true, 2.6).Previous is null, "new segment never creates an older reading card");
        Check(responsive.Tick(4).Current?.Translation == "[임시 번역] 다음 자막", "newest card retains minimum reading duration");
        Check(responsive.Tick(6.7).Current is null, "display ticks do not renew expiry");
        var immediate = new CaptionDeck();
        immediate.Update([unit], "uuid", 1, 2, true, 0);
        var authoritative = unit with { SourceState = "Final", TranslationIsPreview = false,
            StableSource = "", TranslationSource = "", TranslationPrefix = "", Translation = "확정 번역", TranslationRequestId = 13 };
        Check(immediate.Update([authoritative], "uuid", 1, 2, true, 0.01).Current?.Translation == "확정 번역", "final still bypasses short draft delay");
        presentation.Clear();
        Show(preview, 0);
        Show(preview with { SourceRevision = 6, StableSource = "", TranslationState = "Pending", Translation = "", TranslationIsPreview = false }, 0.1);
        Check(presentation.DisplayedTranslationRecord?.SourceRevision == preview.SourceRevision,
            "retained preview telemetry uses displayed revision rather than pending revision");
        Console.WriteLine($"Caption presentation: {assertions} fixture assertions PASS (no rendered UI)");
    }
}
