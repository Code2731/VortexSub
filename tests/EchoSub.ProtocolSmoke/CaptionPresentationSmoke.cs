using EchoSub.Desktop;

internal static class CaptionPresentationSmoke
{
    public static void Run()
    {
        var record = new HistoryRecord(1, 2, 3, 4, 4, 0, 16000, 0, 1, "Final", "Source", null,
            "Pending", "", null, 7, "uuid");
        var presentation = new CaptionPresentation();
        (string? Source, string? Translation) Show(HistoryRecord r, double time, bool running = true) =>
            presentation.Update([r], "uuid", 1, 2, running, time);
        void Check(bool condition, string label)
        {
            if (!condition) throw new IOException("Caption fixture failed: " + label);
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
        Console.WriteLine("Caption presentation: 14 fixture assertions PASS (no rendered UI)");
    }
}
