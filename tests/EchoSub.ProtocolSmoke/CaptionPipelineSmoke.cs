using EchoSub.Desktop;

internal static class CaptionPipelineSmoke
{
    public static void Run()
    {
        var count = 0;
        void Check(bool passed, string name)
        {
            if (!passed) throw new IOException("Caption pipeline: " + name);
            count++;
        }
        HistoryRecord Record(string text, ulong request = 1) => new(1, 1, 1, request, request,
            0, 16000, 0, 1, "Final", "fixture source", null, "Done", text, null, request, "fixture");
        var deck = new CaptionDeck();
        var lines = new CaptionLines();
        int Fit(string text) => Math.Min(4, text.Length);
        const string target = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var emitted = new List<string>();
        for (int step = 0; step <= 300; step++)
        {
            var upper = lines.Upper;
            var lower = lines.Lower;
            var now = step * .1;
            var cards = deck.Update([Record(target)], "fixture", 1, 1, true, now);
            if (step == 100) Check(cards.Current is null && cards.InputExpired,
                "deck explicitly marks ordinary input expiry");
            lines.ValidateSession("fixture", 1, 1, true);
            lines.Apply(cards, now, Fit);
            lines.Tick(now, Fit);
            if (lines.Upper is not null && lines.Upper != upper) emitted.Add(lines.Upper);
            if (lines.Lower is not null && lines.Lower != lower) emitted.Add(lines.Lower);
        }
        Check(string.Concat(emitted) == target, "input card expiry cannot discard unread long-text suffix");
        Check(lines.Upper is null && lines.Lower is null, "long input finishes without replay");
        deck.Clear(); lines.Clear();
        var preview = Record("문 앞", 1) with { SourceState = "Partial", StableSource = "fixture source",
            TranslationIsPreview = true, TranslationSource = "fixture source", TranslationPrefix = "fixture source" };
        lines.Apply(deck.Update([preview], "fixture", 1, 1, true, 0), 0, _ => 100);
        var extended = preview with { SourceRevision = 2, AppliedSourceRevision = 2,
            TranslationRequestId = 2, Translation = "문 앞에서 기다려" };
        var next = deck.Update([extended], "fixture", 1, 1, true, .1);
        Check(next.Current?.Translation == "[임시 번역] 문 앞에서 기다려", "safe append bypasses whole-caption replacement wait");
        lines.Apply(next, .1, _ => 100);
        Check(lines.Upper == "문 앞에서 기다려", "append reaches open reading line immediately");
        var corrected = extended with { SourceRevision = 3, AppliedSourceRevision = 3,
            TranslationRequestId = 3, Translation = "문 뒤에서 기다려" };
        Check(deck.Update([corrected], "fixture", 1, 1, true, .2).Current == next.Current,
            "prefix rewrite still respects draft replacement wait");
        var final = corrected with { SourceState = "Final", TranslationIsPreview = false };
        lines.Apply(deck.Update([final], "fixture", 1, 1, true, .3), .3, _ => 100);
        Check(lines.Upper == "문 앞에서 기다려", "final correction does not erase reading line early");
        lines.Tick(2.7, _ => 100);
        Check(lines.Upper == "문 뒤에서 기다려", "final correction arrives after reading protection");
        lines.ValidateSession("fixture", 1, 1, false);
        lines.Apply(deck.Update([final], "fixture", 1, 1, false, 3), 3, Fit);
        lines.Tick(20, Fit);
        Check(lines.Upper is null && lines.Lower is null, "stop discards retained unread input");
        deck.Clear(); lines.Clear();
        lines.Apply(deck.Update([Record(target)], "fixture", 1, 1, true, 0), 0, Fit);
        var newer = Record("NEW", 2) with { SegmentId = 2 };
        lines.Apply(deck.Update([newer], "fixture", 1, 1, true, 1.3), 1.3, Fit);
        Check(lines.Upper == "ABCD" && lines.Lower == "EFGH", "next segment preserves currently read lines");
        lines.Apply(deck.Tick(3.4), 3.4, Fit);
        Check(lines.Upper == "IJKL" && lines.Lower == "MNOP",
            "next segment cannot silently discard an admitted unread tail");
        lines.ValidateSession("fixture", 1, 2, true);
        lines.Apply(deck.Update([newer with { Epoch = 2, Translation = "FRESH" }], "fixture", 1, 2, true, 4), 4, Fit);
        Check(lines.Upper == "FRES" && lines.Lower == "H", "new epoch cannot replay retained older input");
        deck.Clear(); lines.Clear();
        lines.Apply(deck.Update([Record(target)], "fixture", 1, 1, true, 0), 0, Fit);
        var failedSource = Record(target) with { SourceState = "Failed", SourceReason = "FixtureFailure" };
        var invalid = deck.Update([failedSource], "fixture", 1, 1, true, .1);
        Check(invalid.Current is null && !invalid.InputExpired, "source failure is not ordinary expiry");
        lines.Apply(invalid, .1, Fit);
        Check(lines.Upper == "ABCD" && lines.Lower == "EFGH", "invalid input keeps only already-visible reading lines");
        lines.Tick(10, Fit);
        Check(lines.Upper is null && lines.Lower is null, "invalid input cannot drain older unread suffix");
        var lineDeck = new CaptionDeck { LineReadingManaged = true };
        lines.Clear();
        lines.Apply(lineDeck.Update([Record("old")], "fixture", 1, 1, true, 0), 0, Fit);
        var arriving = Record("next", 2) with { SegmentId = 2 };
        var admitted = lineDeck.Update([arriving], "fixture", 1, 1, true, .1);
        Check(admitted.Current?.Identity?.Segment == 2, "next unit is admitted immediately when lines own reading protection");
        lines.Apply(admitted, .1, Fit);
        Check(lines.Upper == "old" && lines.Lower == "next", "immediate admission fills lower without overwriting protected upper");
        lines.Apply(lineDeck.Tick(2.6), 2.6, Fit);
        Check(lines.Upper is null && lines.Lower == "next", "one-shot next-unit arrival keeps lower position without another history update");
        lineDeck.Clear(); lines.Clear();
        lines.Apply(lineDeck.Update([Record("old")], "fixture", 1, 1, true, 0), 0, Fit);
        lines.Apply(lineDeck.Update([arriving], "fixture", 1, 1, true, .1), .1, Fit);
        var last = arriving with { SegmentId = 3, TranslationRequestId = 3, Translation = "last" };
        lines.Apply(lineDeck.Update([last], "fixture", 1, 1, true, .2), .2, Fit);
        lines.Apply(lineDeck.Tick(2.6), 2.6, Fit);
        Check(lines.Lower == "next" && lines.Identity?.Segment == 2, "rapid distinct units preserve middle sentence");
        lines.Apply(lineDeck.Tick(5.2), 5.2, Fit);
        Check(lines.Upper == "last" && lines.Identity?.Segment == 3, "rapid final unit follows middle sentence");
        lineDeck.Clear(); lines.Clear();
        lines.Apply(lineDeck.Update([preview], "fixture", 1, 1, true, 0), 0, _ => 100);
        var revisedCard = lineDeck.Update([corrected], "fixture", 1, 1, true, .1);
        Check(revisedCard.Current?.Translation == "[임시 번역] 문 뒤에서 기다려",
            "line-managed draft rewrite has no redundant input delay");
        lines.Apply(revisedCard, .1, _ => 100);
        Check(lines.Upper == "문 앞", "line protection still holds old prefix during admitted rewrite");
        lines.Tick(2.6, _ => 100);
        Check(lines.Upper == "문 뒤에서 기다려", "latest draft rewrite appears after line deadline");
        foreach (var managed in new[] { false, true })
        {
            var cosmeticDeck = new CaptionDeck { LineReadingManaged = managed, StabilizeCosmeticRevisions = true };
            cosmeticDeck.Update([preview], "fixture", 1, 1, true, 0);
            var punctuation = preview with { SourceRevision = 2, AppliedSourceRevision = 2,
                TranslationRequestId = 2, Translation = "문 앞." };
            cosmeticDeck.Update([punctuation], "fixture", 1, 1, true, .1);
            Check(cosmeticDeck.Tick(.74).Current?.Translation == "[임시 번역] 문 앞", "cosmetic option retains its own short deadline " + managed);
            Check(cosmeticDeck.Tick(.75).Current?.Translation == "[임시 번역] 문 앞.", "cosmetic deadline has no additional draft wait " + managed);
        }
        foreach (var repairedTarget in new[] { "왼쪽 길을 가세요. 아니요. 오른쪽 길을 가세요.", "왼쪽 길을 가세요, 아니, 오른쪽 길을 가세요." })
        {
            var repairDeck = new CaptionDeck { LineReadingManaged = true };
            var repairLines = new CaptionLines();
            var firstRepair = Record("왼쪽으로 가세요") with { SourceState = "Partial", Source = "Take the left path.",
                StableSource = "Take the left", TranslationIsPreview = true, TranslationSource = "Take the left", TranslationPrefix = "Take the left" };
            repairLines.Apply(repairDeck.Update([firstRepair], "fixture", 1, 1, true, 0), 0, _ => 100);
            var repaired = firstRepair with { SourceRevision = 2, AppliedSourceRevision = 2, TranslationRequestId = 2,
                Source = "Take the left path. No. Take the right path.", StableSource = "Take the left path. No. Take the right path.",
                TranslationSource = "Take the left path. No. Take the right path.", TranslationPrefix = "Take the left path. No. Take the right path.",
                Translation = repairedTarget };
            repairLines.Apply(repairDeck.Update([repaired], "fixture", 1, 1, true, .2), .2, _ => 100);
            var repairFinal = repaired with { SourceState = "Final", TranslationIsPreview = false, TranslationSource = "", TranslationPrefix = "" };
            repairLines.Apply(repairDeck.Update([repairFinal], "fixture", 1, 1, true, .3), .3, _ => 100);
            Check(repairLines.Upper == "왼쪽으로 가세요", "actual repair target preserves unread initial line " + repairedTarget);
            repairLines.Apply(repairDeck.Tick(2.6), 2.6, _ => 100);
            Check(repairLines.Upper == repairedTarget, "one-shot repair final drains after reading deadline " + repairedTarget);
            repairLines.Apply(repairDeck.Tick(5.2), 5.2, _ => 100);
            Check(repairLines.Upper is null && repairLines.Lower is null, "identical repair final is not replayed " + repairedTarget);
            repairLines.ValidateSession("fixture", 1, 2, true);
            repairLines.Apply(repairDeck.Update([repairFinal with { Epoch = 2, Translation = "새 세션" }], "fixture", 1, 2, true, 6), 6, _ => 100);
            Check(repairLines.Upper == "새 세션", "repair state cannot leak into new epoch " + repairedTarget);
        }
        int FitSentence(string text) { var end = text.IndexOf('.'); return end < 0 ? text.Length : end + 1; }
        lineDeck.Clear(); lines.Clear();
        var whole = preview with { Translation = "문 앞에서 기다려. 왼쪽 길로 가세요." };
        lines.Apply(lineDeck.Update([whole], "fixture", 1, 1, true, 0), 0, FitSentence);
        var tail = whole with { SourceRevision = 2, AppliedSourceRevision = 2, TranslationRequestId = 2,
            Source = "fixture source next", StableSource = "fixture source next", TranslationSource = "next",
            TranslationPrefix = "fixture source next", Translation = "다음 구간." };
        var tailCards = lineDeck.Update([tail], "fixture", 1, 1, true, .1);
        Check(tailCards.Current?.Identity?.UnitStart > 0, "history admits pending suffix unit");
        lines.Apply(tailCards, .1, FitSentence);
        var returned = whole with { SourceRevision = 3, AppliedSourceRevision = 3, TranslationRequestId = 3,
            Translation = "문 앞에서 기다려. 오른쪽 길로 가세요." };
        var returnedCards = lineDeck.Update([returned], "fixture", 1, 1, true, .2);
        Check(returnedCards.Current?.Identity?.UnitStart == 0, "replayed full preview returns to original unit");
        lines.Apply(returnedCards, .2, FitSentence);
        lines.Apply(lineDeck.Tick(3.4), 3.4, FitSentence);
        Check(lines.Upper == "오른쪽 길로 가세요." && lines.Lower is null, "history unit recovery elides unchanged clause");
        foreach (var transition in new[] { (Product: "fixture", Session: 1UL, Epoch: 2UL),
            (Product: "fixture", Session: 2UL, Epoch: 1UL), (Product: "other", Session: 1UL, Epoch: 1UL) })
        {
            lineDeck.Clear(); lines.Clear();
            lines.Apply(lineDeck.Update([whole], "fixture", 1, 1, true, 0), 0, FitSentence);
            lines.Apply(lineDeck.Update([returned], "fixture", 1, 1, true, .1), .1, FitSentence);
            lines.ValidateSession(transition.Product, transition.Session, transition.Epoch, true);
            lines.Apply(lineDeck.Update([returned], transition.Product, transition.Session, transition.Epoch, true, .2), .2, FitSentence);
            lines.Tick(4, FitSentence);
            Check(lines.Upper is null && lines.Lower is null, "delayed prior-context history cannot restore pending target " + transition);
            var fresh = Record("새 결과.") with { ProductSessionId = transition.Product, SessionId = transition.Session, Epoch = transition.Epoch };
            lines.Apply(lineDeck.Update([returned, fresh], transition.Product, transition.Session, transition.Epoch, true, 4.1), 4.1, FitSentence);
            Check(lines.Upper == "새 결과.", "mixed history admits only current context " + transition);
        }
        Console.WriteLine($"Caption pipeline: {count} assertions PASS (production deck + lines, synthetic history)");
    }
}

