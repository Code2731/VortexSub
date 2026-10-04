using System.Text.Json;
using EchoSub.Desktop;

public static class CaptionDeliverySmoke
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool passed, string name)
        {
            if (!passed) throw new IOException("Caption delivery: " + name);
            checks++;
        }
        HistoryRecord Record(ulong segment, string target, ulong revision = 1) =>
            new(1, 1, segment, revision, revision, 0, 16000, 0, 1, "Final", "source " + segment,
                null, "Done", target, null, revision, "fixture");
        var deck = new CaptionDeck { LineReadingManaged = true };
        var lines = new CaptionLines();
        var observations = new List<CaptionReadingObservation>();
        lines.Observed += observations.Add;
        int Fit(string text) => text.IndexOf('\n') is var end && end >= 0 ? end + 1 : text.Length;
        void Feed(IEnumerable<HistoryRecord> records, double now) =>
            lines.Apply(deck.Update(records, "fixture", 1, 1, true, now), now, Fit);
        Feed([Record(3, "third"), Record(1, "first\nprotected"), Record(2, "second")], 0);
        Check(lines.Upper == "first" && lines.PendingUnits == 2, "one snapshot preserves all distinct completed segments in order");
        Feed([Record(1, "first\nprotected"), Record(2, "second revised", 2), Record(3, "third")], .1);
        Check(lines.PendingUnits == 2, "same-unit revision coalesces without a duplicate queue item");
        lines.Apply(deck.Tick(3.4), 3.4, Fit);
        Check(lines.Upper == "second revised", "middle segment is actually displayed using its latest revision");
        lines.Apply(deck.Tick(5.2), 5.2, Fit);
        Check(lines.Upper == "second revised" && lines.Lower == "third", "last segment follows into free lower while middle stays protected");
        Feed([Record(1, "first\nprotected"), Record(2, "second revised", 2), Record(3, "third")], 8);
        Check(lines.Upper is null && lines.PendingUnits == 0, "repeated snapshot does not replay consumed captions");
        Check(observations.Count(o => o.Kind == "FirstLine") == 3 &&
            observations.All(o => o.Kind != "Dropped"), "within-budget burst has zero dropped units");
        Check(observations.Single(o => o.Kind == "FirstLine" && o.Identity?.Segment == 2).WaitSeconds == 3.4,
            "coalescing preserves the first observation time");

        deck.Clear(); lines.Clear(); observations.Clear();
        Feed([Record(1, "protected\nbarrier")], 0);
        Feed(Enumerable.Range(2, CaptionLines.MaximumPendingUnits + 1).Select(i => Record((ulong)i, "unit " + i)), .1);
        Check(lines.PendingUnits == CaptionLines.MaximumPendingUnits, "queue memory has a fixed unit bound");
        Check(observations.Count(o => o.Kind == "Dropped" && o.Reason == "QueueCapacity") == 1,
            "capacity overflow is observable rather than silent");
        lines.Tick(10.2, Fit);
        Check(observations.Count(o => o.Kind == "Dropped" && o.Reason == "QueueAge") == CaptionLines.MaximumPendingUnits,
            "age overflow is observable for each unshown unit");
        Check(lines.Upper is null && lines.PendingUnits == 0, "expired backlog cannot reappear");

        deck.Clear(); lines.Clear(); observations.Clear();
        var preview = Record(2, "left") with { SourceState = "Partial", Source = "Take the left path.",
            StableSource = "Take the left path.", TranslationIsPreview = true,
            TranslationSource = "Take the left path.", TranslationPrefix = "Take the left path." };
        Feed([Record(1, "protected\nbarrier"), preview], 0);
        var corrected = preview with { SourceRevision = 2, AppliedSourceRevision = 2, TranslationRequestId = 2,
            Source = "Take the right path.", StableSource = "Take the right path.",
            TranslationSource = "Take the right path.", TranslationPrefix = "Take the right path.", Translation = "right" };
        Feed([corrected], .1);
        lines.Apply(deck.Tick(3.4), 3.4, Fit);
        Check(lines.Upper == "right" && observations.All(o => o.Kind != "FirstLine" || o.Identity?.Segment != 2 || o.AtSeconds >= 2.6),
            "invalidated queued preview cannot be shown before its correction");
        lines.Apply(deck.Update([Record(2, "old")], "fixture", 1, 1, false, 3), 3, Fit);
        lines.Tick(20, Fit);
        Check(lines.Upper is null && lines.PendingUnits == 0, "stop clears displayed and waiting captions");
        lines.Clear();
        var protectedCard = new CaptionCard("source", "protected\nbarrier", false, "") { Identity = new("fixture", 1, 1, 1, 0) };
        var waitingCard = new CaptionCard("source", "new", false, "") { Identity = new("fixture", 1, 1, 2, 0),
            SourceRevision = 3, TranslationRequestId = 4 };
        lines.Update(protectedCard, 0, Fit);
        lines.Update(waitingCard, .1, Fit);
        lines.Update(waitingCard with { Translation = "stale", SourceRevision = 2, TranslationRequestId = 3 }, .2, Fit);
        lines.Tick(3.4, Fit);
        Check(lines.Upper == "new", "stale revisions cannot overwrite a queued unit");
        lines.Clear();
        var evolving = protectedCard with { Translation = "left\ndirection" };
        lines.Update(evolving, 0, Fit);
        lines.Update(evolving with { Translation = "left\ndirection plus more\nextra" }, 9, Fit);
        lines.Update(waitingCard, 9.1, Fit);
        lines.Update(evolving with { Translation = "right." }, 9.2, Fit);
        lines.Tick(12.4, Fit);
        Check(lines.Upper == "right.", "age cap cannot silently discard an active shortening correction");
        lines.Tick(14.2, Fit);
        Check(lines.Upper == "right." && lines.Lower == "new", "queued sentence follows protected correction in free lower slot");

        var buffer = new WorkerEventBuffer();
        WorkerEvent Event(ulong sequence, HistoryRecord record) => new(sequence, "translation.updated",
            JsonSerializer.SerializeToElement(new { record }));
        buffer.Publish(Event(1, Record(1, "first")));
        buffer.Publish(Event(2, Record(2, "second")));
        buffer.AcceptSnapshot(2);
        Check(!buffer.TryRead(out _), "snapshot drains the general event channel");
        Check(buffer.TryReadCaptionRecord(out var a) && a?.SegmentId == 1 &&
            buffer.TryReadCaptionRecord(out var b) && b?.SegmentId == 2,
            "snapshot acceptance preserves caption records until UI consumption");
        for (ulong sequence = 3; sequence < 3 + WorkerEventBuffer.MaximumCaptionEvents + 5; sequence++)
            buffer.Publish(Event(sequence, Record(sequence, "fixture")));
        var retained = 0;
        while (buffer.TryReadCaptionRecord(out _)) retained++;
        Check(retained == WorkerEventBuffer.MaximumCaptionEvents && buffer.DroppedCaptionEvents == 5,
            "record event overflow is bounded and counted");
        var delayed = new WorkerEventBuffer();
        delayed.AcceptSnapshot(2);
        delayed.Publish(Event(1, Record(1, "first")));
        delayed.Publish(Event(2, Record(2, "second")));
        delayed.Publish(Event(2, Record(2, "second")));
        Check(delayed.TryReadCaptionRecord(out a) && a?.SegmentId == 1 &&
            delayed.TryReadCaptionRecord(out b) && b?.SegmentId == 2 && !delayed.TryReadCaptionRecord(out _),
            "delayed snapshot-covered events are journaled once");

        lines.Clear();
        CaptionCard Card(string text) => new("source", text, true, "source") { Identity = new("fixture", 1, 1, 1, 0) };
        int FitSeparated(string text)
        {
            var end = text.IndexOfAny(['\r', '\n']);
            return end < 0 ? text.Length : end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
        }
        lines.Update(Card("first \r"), 0, FitSeparated);
        lines.Update(Card("first \r\nsecond"), .1, FitSeparated);
        Check(lines.Upper == "first " && lines.Lower == "second", "split CRLF growth preserves spaces and consumes separators once");
        Console.WriteLine($"Caption delivery: {checks} assertions PASS (ordered history, bounded events/reading; synthetic input)");
    }
}
