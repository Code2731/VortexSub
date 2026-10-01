namespace EchoSub.Desktop;

// Snapshot records carry the worker-validated identity; presentation never merges events.
public sealed class CaptionPresentation
{
    private (string? Product, ulong Session, ulong Epoch, ulong Segment, ulong Revision)? key;
    private double since;
    private HistoryRecord? preview;
    private double previewSince;
    public bool IsExpired(double nowSeconds) => preview is not null
        ? nowSeconds - previewSince >= 5 : key is not null && nowSeconds - since >= 5;
    public void Clear() { key = null; preview = null; }

    public (string? Source, string? Translation) Update(IEnumerable<HistoryRecord> records,
        string? productSession, ulong internalSession, ulong epoch, bool running, double nowSeconds)
    {
        var record = running ? records.LastOrDefault(r => r.ProductSessionId == productSession &&
            r.SessionId == internalSession && r.Epoch == epoch &&
            r.SourceState is "Partial" or "FinalPending" or "Final" &&
            r.AppliedSourceRevision is > 0 && r.AppliedSourceRevision <= r.SourceRevision &&
            !string.IsNullOrWhiteSpace(r.Source)) : null;
        if (record is null) { Clear(); return (null, null); }
        if (preview is not null && (preview.ProductSessionId != record.ProductSessionId ||
            preview.SessionId != record.SessionId || preview.Epoch != record.Epoch ||
            preview.SegmentId != record.SegmentId ||
            !record.Source.StartsWith(preview.StableSource, StringComparison.Ordinal))) preview = null;
        var next = (record.ProductSessionId, record.SessionId, record.Epoch, record.SegmentId, record.AppliedSourceRevision!.Value);
        if (key != next) { key = next; since = nowSeconds; }
        var prefix = record.SourceState == "Partial" ? "[인식 중] " : record.SourceState == "FinalPending" ? "[확정 처리 중] " : "";
        var eligible = record.SourceState == "Final" && !record.TranslationIsPreview ||
            record.SourceState == "Partial" && record.TranslationIsPreview && record.StableSource.Length > 0 &&
            record.Source.StartsWith(record.StableSource, StringComparison.Ordinal);
        var translation = eligible && record.AppliedSourceRevision == record.SourceRevision &&
            record.TranslationState == "Done" && record.TranslationRequestId is > 0 &&
            !string.IsNullOrWhiteSpace(record.Translation) ? record.Translation : null;
        if (translation is not null && record.TranslationIsPreview)
        {
            if (preview is null || preview.TranslationRequestId != record.TranslationRequestId ||
                preview.SourceRevision != record.SourceRevision)
            { preview = record; previewSince = nowSeconds; }
            if (nowSeconds - previewSince >= 5) return (null, null);
            translation = "[임시 번역] " + translation;
        }
        else if (preview is not null && record.TranslationState is "None" or "Pending" &&
            nowSeconds - previewSince < 5)
        {
            // Keep an already displayed, matching caption while the next revision
            // is decoded/translated. This never accepts a stale worker response.
            return ("[인식 중] " + preview.Source, "[임시 번역] " + preview.Translation);
        }
        else
        {
            preview = null;
            if (nowSeconds - since >= 5) return (null, null);
        }
        return (prefix + record.Source, translation);
    }

    public static string HistoryText(HistoryRecord record) =>
        $"[{record.ProductSessionId ?? record.SessionId.ToString()}/{record.Epoch}/{record.SegmentId} · {record.SessionAudioStartSeconds ?? record.AudioStartSeconds:F3}~{record.SessionAudioEndSeconds ?? record.AudioEndSeconds:F3}초 · {record.SourceState}] {record.SourceReason}\n{record.Source}\n" +
        $"{(record.TranslationIsPreview ? "임시 번역" : "번역")} {record.TranslationState}{(record.TranslationReason is null ? "" : " · " + record.TranslationReason)}" +
        (record.TranslationState == "Done" ? "\n" + record.Translation : "");
}
