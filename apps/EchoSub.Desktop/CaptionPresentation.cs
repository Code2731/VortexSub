namespace EchoSub.Desktop;

// Snapshot records carry the worker-validated identity; presentation never merges events.
public sealed class CaptionPresentation
{
    private (string? Product, ulong Session, ulong Epoch, ulong Segment, ulong Revision)? key;
    private double since;
    public bool IsExpired(double nowSeconds) => key is not null && nowSeconds - since >= 5;

    public (string? Source, string? Translation) Update(IEnumerable<HistoryRecord> records,
        string? productSession, ulong internalSession, ulong epoch, bool running, double nowSeconds)
    {
        var record = running ? records.LastOrDefault(r => r.ProductSessionId == productSession &&
            r.SessionId == internalSession && r.Epoch == epoch &&
            r.SourceState is "Partial" or "FinalPending" or "Final" &&
            r.AppliedSourceRevision is > 0 && r.AppliedSourceRevision <= r.SourceRevision &&
            !string.IsNullOrWhiteSpace(r.Source)) : null;
        if (record is null) return (null, null);
        var next = (record.ProductSessionId, record.SessionId, record.Epoch, record.SegmentId, record.AppliedSourceRevision!.Value);
        if (key != next) { key = next; since = nowSeconds; }
        if (nowSeconds - since >= 5) return (null, null);
        var prefix = record.SourceState == "Partial" ? "[인식 중] " : record.SourceState == "FinalPending" ? "[확정 처리 중] " : "";
        var eligible = record.SourceState == "Final" && !record.TranslationIsPreview ||
            record.SourceState == "Partial" && record.TranslationIsPreview && record.StableSource.Length > 0 &&
            record.Source.StartsWith(record.StableSource, StringComparison.Ordinal);
        var translation = eligible && record.AppliedSourceRevision == record.SourceRevision &&
            record.TranslationState == "Done" && record.TranslationRequestId is > 0 &&
            !string.IsNullOrWhiteSpace(record.Translation) ? record.Translation : null;
        if (translation is not null && record.TranslationIsPreview) translation = "[임시 번역] " + translation;
        return (prefix + record.Source, translation);
    }

    public static string HistoryText(HistoryRecord record) =>
        $"[{record.ProductSessionId ?? record.SessionId.ToString()}/{record.Epoch}/{record.SegmentId} · {record.SessionAudioStartSeconds ?? record.AudioStartSeconds:F3}~{record.SessionAudioEndSeconds ?? record.AudioEndSeconds:F3}초 · {record.SourceState}] {record.SourceReason}\n{record.Source}\n" +
        $"{(record.TranslationIsPreview ? "임시 번역" : "번역")} {record.TranslationState}{(record.TranslationReason is null ? "" : " · " + record.TranslationReason)}" +
        (record.TranslationState == "Done" ? "\n" + record.Translation : "");
}
