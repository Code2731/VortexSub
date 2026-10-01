using System.Text;

namespace EchoSub.Desktop;

public sealed record CaptionCard(string Source, string? Translation, bool IsDraft, string StableSource);
public sealed record CaptionCards(CaptionCard? Previous, CaptionCard? Current);

// Two display slots, one latest deferred update. No transcript backlog in the UI.
public sealed class CaptionDeck
{
    public const double MinimumReplacementSeconds = 1.25;
    public bool ShowSource { get; set; }
    private (string? Product, ulong Session, ulong Epoch)? context;
    private Entry? previous;
    private Entry? current;

    private sealed class Entry(ulong segment)
    {
        public ulong Segment { get; } = segment;
        public CaptionPresentation Presenter { get; } = new();
        public CaptionCard? Card;
        public CaptionCard? Pending;
        public double ShownAt;
        public double ExpiresAt;
    }

    public void Clear() { context = null; previous = current = null; }

    public static double ReadingSeconds(string text) =>
        Math.Clamp(2 + text.EnumerateRunes().Count(r => !Rune.IsWhiteSpace(r)) / 8.0, 4, 10);

    public CaptionCards Update(IEnumerable<HistoryRecord> records, string? productSession,
        ulong internalSession, ulong epoch, bool running, double nowSeconds)
    {
        if (!running) { Clear(); return new(null, null); }
        var nextContext = (productSession, internalSession, epoch);
        if (context != nextContext) { Clear(); context = nextContext; }
        var matching = records.Where(r => r.ProductSessionId == productSession &&
            r.SessionId == internalSession && r.Epoch == epoch).ToArray();
        if (previous is not null)
        {
            // An expired old segment cannot reappear when a late HTTP response arrives.
            if (nowSeconds >= previous.ExpiresAt) previous = null;
            else if (matching.LastOrDefault(r => r.SegmentId == previous.Segment) is { } older)
                Refresh(previous, older, nowSeconds);
        }
        var latest = matching.MaxBy(r => r.SegmentId);
        if (latest is not null && (current is null || latest.SegmentId >= current.Segment))
        {
            if (current is null || latest.SegmentId != current.Segment)
            {
                // Keep the reading slot until its deadline. If both slots are occupied,
                // replace only the newest draft; intermediate captions are not queued.
                if (previous is null && current?.Card is not null && nowSeconds < current.ExpiresAt &&
                    (ShowSource || !string.IsNullOrWhiteSpace(current.Card.Translation)))
                    previous = current;
                current = new Entry(latest.SegmentId);
            }
            Refresh(current, latest, nowSeconds);
        }
        return Tick(nowSeconds);
    }

    public CaptionCards Tick(double nowSeconds)
    {
        if (previous is not null && (nowSeconds >= previous.ExpiresAt ||
            !ShowSource && string.IsNullOrWhiteSpace(previous.Card?.Translation))) previous = null;
        foreach (var entry in new[] { previous, current })
            if (entry?.Pending is { } pending && nowSeconds - entry.ShownAt >= MinimumReplacementSeconds)
                Accept(entry, pending, nowSeconds);
        return new(Visible(previous, nowSeconds), Visible(current, nowSeconds));
    }

    private void Refresh(Entry entry, HistoryRecord record, double nowSeconds)
    {
        var (source, translation) = entry.Presenter.Update([record], context!.Value.Product,
            context.Value.Session, context.Value.Epoch, true, nowSeconds, expire: false);
        if (source is null) { entry.Card = entry.Pending = null; return; }
        var stable = translation?.StartsWith("[임시 번역] ", StringComparison.Ordinal) == true
            ? record.StableSource.Length > 0 ? record.StableSource : entry.Card?.StableSource ?? ""
            : "";
        var next = new CaptionCard(source, translation,
            record.SourceState != "Final" || record.TranslationIsPreview || stable.Length > 0, stable);
        if (next == entry.Card) { entry.Pending = null; return; }
        // A contradicted prefix must disappear immediately, regardless of reading time.
        var corrected = entry.Card?.StableSource.Length > 0 &&
            !record.Source.StartsWith(entry.Card.StableSource, StringComparison.Ordinal);
        var lostTranslation = entry.Card?.Translation is not null && translation is null;
        var firstTranslation = entry.Card?.Translation is null && translation is not null;
        var finalized = entry.Card?.IsDraft == true && !next.IsDraft;
        if (entry.Card is not null && !corrected && !lostTranslation && !firstTranslation && !finalized &&
            nowSeconds - entry.ShownAt < MinimumReplacementSeconds)
            entry.Pending = next;
        else Accept(entry, next, nowSeconds);
    }

    private static void Accept(Entry entry, CaptionCard card, double nowSeconds)
    {
        // Source revisions cannot keep an unchanged translation alive indefinitely.
        if (entry.Card is null || entry.Card.Translation != card.Translation ||
            card.Translation is null && entry.Card.Source != card.Source)
            entry.ExpiresAt = nowSeconds + ReadingSeconds(ReadingText(card.Translation ?? card.Source));
        entry.Card = card;
        entry.Pending = null;
        entry.ShownAt = nowSeconds;
    }

    private static CaptionCard? Visible(Entry? entry, double nowSeconds) =>
        entry is not null && nowSeconds < entry.ExpiresAt ? entry.Card : null;

    private static string ReadingText(string text)
    {
        foreach (var prefix in new[] { "[임시 번역] ", "[인식 중] ", "[확정 처리 중] " })
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return text[prefix.Length..];
        return text;
    }
}
