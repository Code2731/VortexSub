using System.Text;

namespace EchoSub.Desktop;

public sealed record CaptionIdentity(string? ProductSession, ulong Session, ulong Epoch, ulong Segment, int UnitStart);
public sealed record CaptionCard(string Source, string? Translation, bool IsDraft, string StableSource)
{
    public CaptionIdentity? Identity { get; init; }
    public ulong SourceRevision { get; init; }
    public ulong? TranslationRequestId { get; init; }
}
// Previous is retained for old diagnostic consumers. Production always supplies null.
// Reading slots and queued units are owned by CaptionLines, never by this field.
public sealed record CaptionCards(CaptionCard? Previous, CaptionCard? Current)
{
    public bool InputExpired { get; init; }
    public bool OrderedDelivery { get; init; }
    public bool ResetReading { get; init; }
    public IReadOnlyList<CaptionCard> Delivered { get; init; } = [];
    public IReadOnlyList<ulong> InvalidatedSegments { get; init; } = [];
}
public sealed record CaptionUpdateTiming(HistoryRecord? TranslationRecord, string Slot, double ObservedAtSeconds,
    double AppliedAtSeconds, double DeferredSeconds);

// Legacy card policy plus ordered delivery to the bounded line reader.
public sealed class CaptionDeck
{
    public const double MinimumReplacementSeconds = 1.25;
    // Fast inference must not overwrite an ordinary draft every few hundred milliseconds.
    // First translation, invalidated source guards and final corrections bypass this wait.
    public const double DraftReplacementSeconds = 1.25;
    public const double CosmeticReplacementSeconds = 0.75;
    public bool StabilizeCosmeticRevisions { get; set; }
    public bool LineReadingManaged { get; init; }
    public event Action<CaptionUpdateTiming>? Applied;
    public event Action<CaptionTargetObservation>? TargetObserved;
    private readonly CaptionTargetChanges candidates = new();
    public bool ShowSource { get; set; }
    private (string? Product, ulong Session, ulong Epoch)? context;
    private Entry? current;
    private const int MaximumTrackedSegments = 64;
    private readonly Dictionary<ulong, Entry> entries = new();
    private readonly List<CaptionCard> delivered = [];
    private readonly HashSet<ulong> invalidated = [];
    private ulong retiredThrough;
    private bool resetReading;

    private sealed class Entry(ulong segment, int unitStart = 0)
    {
        public ulong Segment { get; } = segment;
        public int UnitStart { get; } = unitStart;
        public CaptionPresentation Presenter { get; } = new();
        public CaptionTargetChanges DisplayChanges { get; } = new();
        public CaptionCard? Card;
        public CaptionCard? Pending;
        public HistoryRecord? PendingRecord;
        public HistoryRecord? DisplayedRecord;
        public double PendingNotBefore;
        public double PendingSince;
        public double ShownAt;
        public double ExpiresAt;
        public HistoryRecord? LastRecord;
    }

    public void Clear()
    {
        context = null; current = null; candidates.Clear(); entries.Clear();
        delivered.Clear(); invalidated.Clear(); retiredThrough = 0; resetReading = true;
    }

    public static double ReadingSeconds(string text) =>
        Math.Clamp(2 + text.EnumerateRunes().Count(r => !Rune.IsWhiteSpace(r)) / 8.0, 4, 10);

    public CaptionCards Update(IEnumerable<HistoryRecord> records, string? productSession,
        ulong internalSession, ulong epoch, bool running, double nowSeconds)
    {
        if (!running) { Clear(); return Tick(nowSeconds); }
        var nextContext = (productSession, internalSession, epoch);
        if (context != nextContext) { Clear(); context = nextContext; }
        var matching = records.Where(r => r.ProductSessionId == productSession &&
            r.SessionId == internalSession && r.Epoch == epoch).ToArray();
        if (LineReadingManaged)
        {
            foreach (var record in matching.OrderBy(r => r.SegmentId).ThenBy(r => r.SourceRevision)
                .ThenBy(r => r.TranslationRequestId)) ObserveOrdered(record, nowSeconds);
            return Tick(nowSeconds);
        }
        var latest = matching.MaxBy(r => r.SegmentId);
        if (latest is not null && (current is null || latest.SegmentId >= current.Segment))
        {
            if (candidates.ObserveSource(latest))
                TargetObserved?.Invoke(new("SourceGuardInvalidated", "candidate", latest, nowSeconds, null));
            if (candidates.Observe(latest, nowSeconds) is { } candidateChange)
                TargetObserved?.Invoke(new("Candidate", "candidate", latest, nowSeconds, candidateChange));
            var unitStart = UnitStart(latest);
            var nextUnit = latest.TranslationIsPreview && latest.TranslationState == "Done" &&
                latest.AppliedSourceRevision == latest.SourceRevision && latest.TranslationRequestId is > 0 &&
                current is not null && unitStart != current.UnitStart;
            if (current is null || latest.SegmentId != current.Segment || nextUnit)
            {
                // A new untranslated segment must not blank a still-readable caption.
                // Keep only the existing caption until its deadline or the latest translation.
                var translated = latest.TranslationState == "Done" && latest.TranslationRequestId is > 0 &&
                    latest.AppliedSourceRevision == latest.SourceRevision && !string.IsNullOrWhiteSpace(latest.Translation);
                if (current?.Card is not null && !ShowSource && !translated && !nextUnit)
                    return Tick(nowSeconds);
                var contradicted = nextUnit && current?.Card?.StableSource.Length > 0 &&
                    !latest.Source.StartsWith(current.Card.StableSource, StringComparison.Ordinal);
                if (!LineReadingManaged && current?.Card?.Translation is not null && !contradicted &&
                    nowSeconds < current.ExpiresAt && nowSeconds - current.ShownAt < MinimumReplacementSeconds)
                    return Tick(nowSeconds);
                current = new Entry(latest.SegmentId, unitStart);
            }
            Refresh(current, latest, nowSeconds);
        }
        return Tick(nowSeconds);
    }

    public CaptionCards Tick(double nowSeconds)
    {
        if (LineReadingManaged)
        {
            foreach (var entry in entries.Values.OrderBy(e => e.Segment)) ApplyPending(entry, nowSeconds);
        }
        else ApplyPending(current, nowSeconds);
        var result = new CaptionCards(null, Visible(current, nowSeconds))
        {
            InputExpired = current?.Card is not null && nowSeconds >= current.ExpiresAt,
            OrderedDelivery = LineReadingManaged,
            ResetReading = LineReadingManaged && resetReading,
            Delivered = delivered.Count == 0 ? Array.Empty<CaptionCard>() : delivered.ToArray(),
            InvalidatedSegments = invalidated.Count == 0 ? Array.Empty<ulong>() : invalidated.ToArray()
        };
        delivered.Clear(); invalidated.Clear(); resetReading = false;
        return result;
    }

    private void ApplyPending(Entry? entry, double nowSeconds)
    {
            if (entry?.Pending is { } pending && nowSeconds >= entry.PendingNotBefore &&
                nowSeconds - entry.ShownAt >= (entry.PendingNotBefore > 0 ? 0 : ReplacementSeconds(entry, pending)))
                Accept(entry, pending, entry.PendingRecord, entry.PendingSince, nowSeconds);
    }

    private void ObserveOrdered(HistoryRecord record, double nowSeconds)
    {
        if (record.SegmentId <= retiredThrough) return;
        entries.TryGetValue(record.SegmentId, out var entry);
        if (entry?.LastRecord is { } previous && (previous == record ||
            record.SourceRevision < previous.SourceRevision ||
            record.SourceRevision == previous.SourceRevision &&
            record.TranslationRequestId.GetValueOrDefault() < previous.TranslationRequestId.GetValueOrDefault())) return;
        if (candidates.ObserveSource(record))
            TargetObserved?.Invoke(new("SourceGuardInvalidated", "candidate", record, nowSeconds, null));
        if (candidates.Observe(record, nowSeconds) is { } candidateChange)
            TargetObserved?.Invoke(new("Candidate", "candidate", record, nowSeconds, candidateChange));
        var unitStart = UnitStart(record);
        var translated = record.TranslationState == "Done" && record.TranslationRequestId is > 0 &&
            record.AppliedSourceRevision == record.SourceRevision && !string.IsNullOrWhiteSpace(record.Translation);
        if (entry?.Card?.StableSource is { Length: > 0 } guard &&
            !record.Source.StartsWith(guard, StringComparison.Ordinal))
        {
            invalidated.Add(record.SegmentId);
            delivered.RemoveAll(card => card.Identity?.Segment == record.SegmentId);
        }
        if (record.SourceState is "Failed" or "Discarded" or "Skipped") invalidated.Add(record.SegmentId);
        if (entry is null || translated && unitStart != entry.UnitStart)
        {
            entry = new Entry(record.SegmentId, unitStart);
            entries[record.SegmentId] = entry;
            while (entries.Count > MaximumTrackedSegments)
            {
                var oldest = entries.Keys.Min();
                entries.Remove(oldest); retiredThrough = Math.Max(retiredThrough, oldest);
            }
        }
        entry.LastRecord = record;
        if (current is null || entry.Segment >= current.Segment) current = entry;
        Refresh(entry, record, nowSeconds);
    }

    private void Refresh(Entry entry, HistoryRecord record, double nowSeconds)
    {
        var (source, translation) = entry.Presenter.Update([record], context!.Value.Product,
            context.Value.Session, context.Value.Epoch, true, nowSeconds, expire: false);
        if (source is null)
        {
            if (LineReadingManaged) invalidated.Add(record.SegmentId);
            entry.Card = entry.Pending = null; entry.PendingRecord = null; return;
        }
        var stable = translation?.StartsWith("[임시 번역] ", StringComparison.Ordinal) == true
            ? entry.Presenter.PreviewPrefix : "";
        var next = new CaptionCard(source, translation,
            record.SourceState != "Final" || record.TranslationIsPreview || stable.Length > 0, stable)
        {
            Identity = new(record.ProductSessionId, record.SessionId, record.Epoch, record.SegmentId, entry.UnitStart),
            SourceRevision = record.SourceRevision, TranslationRequestId = record.TranslationRequestId
        };
        if (next == entry.Card) { entry.Pending = null; entry.PendingRecord = null; return; }
        // A contradicted prefix must disappear immediately, regardless of reading time.
        var corrected = entry.Card?.StableSource.Length > 0 &&
            !record.Source.StartsWith(entry.Card.StableSource, StringComparison.Ordinal);
        var lostTranslation = entry.Card?.Translation is not null && translation is null;
        var firstTranslation = entry.Card?.Translation is null && translation is not null;
        var finalized = entry.Card?.IsDraft == true && !next.IsDraft;
        // Growth preserves the existing reading prefix. The line presenter appends it;
        // paragraph replacement throttling would add latency without protecting text.
        var extendsTranslation = entry.Card?.Identity == next.Identity &&
            entry.Card?.Translation is { } priorText && translation is not null &&
            ReadingText(translation).Length > ReadingText(priorText).Length &&
            ReadingText(translation).StartsWith(ReadingText(priorText), StringComparison.Ordinal);
        var displayedRecord = entry.Presenter.DisplayedTranslationRecord;
        var cosmetic = StabilizeCosmeticRevisions && !corrected && !lostTranslation && !firstTranslation && !finalized
            && IsCosmeticRevision(entry.DisplayedRecord, displayedRecord);
        if (cosmetic && nowSeconds - entry.ShownAt < CosmeticReplacementSeconds)
        {
            if (entry.PendingRecord?.TranslationRequestId != displayedRecord!.TranslationRequestId)
                TargetObserved?.Invoke(new("CosmeticDeferred", "current",
                    displayedRecord, nowSeconds, null, entry.ShownAt + CosmeticReplacementSeconds));
            // One latest candidate, fixed deadline from the shown caption; no second inference/agreement.
            if (entry.Pending != next) entry.PendingSince = nowSeconds;
            entry.Pending = next;
            entry.PendingRecord = displayedRecord;
            entry.PendingNotBefore = entry.ShownAt + CosmeticReplacementSeconds;
            return;
        }
        if (entry.Card is not null && !corrected && !lostTranslation && !firstTranslation && !finalized && !extendsTranslation &&
            nowSeconds - entry.ShownAt < ReplacementSeconds(entry, next))
        {
            if (entry.Pending != next) entry.PendingSince = nowSeconds;
            entry.Pending = next;
            entry.PendingRecord = entry.Presenter.DisplayedTranslationRecord;
            entry.PendingNotBefore = 0;
        }
        else Accept(entry, next, entry.Presenter.DisplayedTranslationRecord,
            entry.Pending == next ? entry.PendingSince : nowSeconds, nowSeconds);
    }

    private double ReplacementSeconds(Entry entry, CaptionCard card) =>
        LineReadingManaged ? 0 : ReferenceEquals(entry, current) && card.IsDraft ? DraftReplacementSeconds : MinimumReplacementSeconds;

    private static bool IsCosmeticRevision(HistoryRecord? prior, HistoryRecord? next)
    {
        if (prior is null || next is null || !prior.TranslationIsPreview || !next.TranslationIsPreview
            || prior.SourceRevision > next.SourceRevision || prior.TranslationRequestId >= next.TranslationRequestId
            || prior.SessionId != next.SessionId || prior.Epoch != next.Epoch || prior.SegmentId != next.SegmentId
            || prior.ProductSessionId != next.ProductSessionId || UnitStart(prior) != UnitStart(next)
            || prior.TranslationSource.Length == 0 || prior.TranslationSource != next.TranslationSource
            || prior.TranslationPrefix != next.TranslationPrefix || prior.Translation == next.Translation
            || prior.Translation.Any(char.IsDigit) || next.Translation.Any(char.IsDigit)) return false;
        // Ignore exactly one terminal full stop. Keep ellipses, questions, exclamations and lexical changes.
        static string Body(string text) => text.Length > 1 && text[^1] is '.' or '。'
            && text[^2] is not ('.' or '。') ? text[..^1] : text;
        return Body(prior.Translation) == Body(next.Translation);
    }

    private void Accept(Entry entry, CaptionCard card, HistoryRecord? translationRecord, double observedAt, double nowSeconds)
    {
        // Source revisions cannot keep an unchanged translation alive indefinitely.
        if (entry.Card is null || entry.Card.Translation != card.Translation ||
            card.Translation is null && entry.Card.Source != card.Source)
            entry.ExpiresAt = nowSeconds + ReadingSeconds(ReadingText(card.Translation ?? card.Source));
        entry.Card = card;
        entry.Pending = null;
        entry.PendingRecord = null;
        entry.PendingNotBefore = 0;
        entry.DisplayedRecord = translationRecord;
        entry.ShownAt = nowSeconds;
        if (translationRecord is not null && entry.DisplayChanges.Observe(translationRecord, nowSeconds) is { } change)
            TargetObserved?.Invoke(new("Displayed", "current",
                translationRecord, nowSeconds, change));
        if (LineReadingManaged && card.Translation is { Length: > 0 })
        {
            var replacement = delivered.FindIndex(prior => prior.Identity == card.Identity);
            if (replacement >= 0) delivered[replacement] = card;
            else delivered.Add(card);
        }
        Applied?.Invoke(new(translationRecord, "current",
            observedAt, nowSeconds, Math.Max(0, nowSeconds - observedAt)));
    }

    private static CaptionCard? Visible(Entry? entry, double nowSeconds) =>
        entry is not null && nowSeconds < entry.ExpiresAt ? entry.Card : null;
    private static int UnitStart(HistoryRecord record) => CaptionTargetChanges.UnitStart(record);

    private static string ReadingText(string text)
    {
        foreach (var prefix in new[] { "[임시 번역] ", "[인식 중] ", "[확정 처리 중] " })
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return text[prefix.Length..];
        return text;
    }
}
