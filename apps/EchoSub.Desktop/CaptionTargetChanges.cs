using System.Text.Json.Serialization;

namespace EchoSub.Desktop;

public sealed record CaptionTargetChange(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("session_id")] ulong SessionId,
    [property: JsonPropertyName("epoch")] ulong Epoch,
    [property: JsonPropertyName("segment_id")] ulong SegmentId,
    [property: JsonPropertyName("source_revision")] ulong SourceRevision,
    [property: JsonPropertyName("request_id")] ulong RequestId,
    [property: JsonPropertyName("previous_source_revision")] ulong? PreviousSourceRevision,
    [property: JsonPropertyName("previous_request_id")] ulong? PreviousRequestId,
    [property: JsonPropertyName("unit_start_utf16")] int UnitStart,
    [property: JsonPropertyName("previous_unit_start_utf16")] int? PreviousUnitStart,
    [property: JsonPropertyName("unit_identity_available")] bool UnitIdentityAvailable,
    [property: JsonPropertyName("at_s")] double AtSeconds,
    [property: JsonPropertyName("unit_first_observed_s")] double UnitFirstObservedSeconds,
    [property: JsonPropertyName("target_changed")] bool TargetChanged,
    [property: JsonPropertyName("shared_prefix_utf16")] int SharedPrefix,
    [property: JsonPropertyName("removed_utf16")] int Removed,
    [property: JsonPropertyName("appended_utf16")] int Appended,
    [property: JsonPropertyName("same_unit_removed_utf16")] int? SameUnitRemoved,
    [property: JsonPropertyName("source_guard_invalidated")] bool SourceGuardInvalidated);

// Diagnostic only: one bounded prior candidate. Never controls admission or display.
public sealed class CaptionTargetChanges
{
    private HistoryRecord? previous;
    private double firstObserved;
    private bool guardInvalidated;

    public void Clear() { previous = null; guardInvalidated = false; firstObserved = 0; }
    public static int UnitStart(HistoryRecord record) => record.TranslationIsPreview
        && record.TranslationSource.Length > 0 ? record.TranslationPrefix.Length - record.TranslationSource.Length : 0;
    private static bool SameContext(HistoryRecord a, HistoryRecord b) => a.ProductSessionId == b.ProductSessionId
        && a.SessionId == b.SessionId && a.Epoch == b.Epoch;
    private static bool SameSegment(HistoryRecord a, HistoryRecord b) => SameContext(a, b) && a.SegmentId == b.SegmentId;

    // Prefix invalidation is a lexical signal, not proof of semantic contradiction.
    public bool ObserveSource(HistoryRecord record)
    {
        if (previous is null) return false;
        if (!SameContext(previous, record)) { Clear(); return false; }
        if (!SameSegment(previous, record) || record.SourceRevision < previous.SourceRevision
            || record.AppliedSourceRevision is null or 0 || guardInvalidated || !previous.TranslationIsPreview) return false;
        var guard = previous.TranslationPrefix.Length > 0 ? previous.TranslationPrefix : previous.StableSource;
        if (guard.Length == 0 || record.Source.StartsWith(guard, StringComparison.Ordinal)) return false;
        guardInvalidated = true;
        return true;
    }

    public CaptionTargetChange? Observe(HistoryRecord record, double nowSeconds)
    {
        if (!double.IsFinite(nowSeconds) || nowSeconds < 0) return null;
        ObserveSource(record);
        if (record.TranslationState != "Done" || record.TranslationRequestId is null or 0
            || record.AppliedSourceRevision != record.SourceRevision || string.IsNullOrWhiteSpace(record.Translation)
            || !(record.SourceState == "Final" && !record.TranslationIsPreview
                || record.SourceState == "Partial" && record.TranslationIsPreview
                && record.StableSource.Length > 0 && record.Source.StartsWith(record.StableSource, StringComparison.Ordinal))) return null;
        if (previous is not null && SameContext(previous, record)
            && (record.SegmentId < previous.SegmentId || SameSegment(previous, record)
                && (record.SourceRevision < previous.SourceRevision || record.TranslationRequestId <= previous.TranslationRequestId))) return null;
        var sameSegment = previous is not null && SameSegment(previous, record);
        var start = UnitStart(record);
        var unitKnown = !record.TranslationIsPreview || record.TranslationSource.Length > 0;
        var previousUnitKnown = previous is null || !previous.TranslationIsPreview || previous.TranslationSource.Length > 0;
        var kind = previous is null ? "Initial" : !sameSegment ? "SegmentTransition"
            : previous.TranslationIsPreview && !record.TranslationIsPreview ? "FinalRestoration"
            : !previous.TranslationIsPreview && record.TranslationIsPreview ? "PreviewRestart"
            : !unitKnown || !previousUnitKnown ? "UnidentifiedRevision"
            : start != UnitStart(previous) ? guardInvalidated || start < UnitStart(previous) ? "SourceUnitReset" : "UnitTransition"
            : record.TranslationIsPreview ? "SameUnitRevision" : "FinalRevision";
        var withinUnit = kind is "SameUnitRevision" or "FinalRevision";
        if (!withinUnit) firstObserved = nowSeconds;
        // Raw replacement excludes comparisons across segment/session boundaries.
        var prior = sameSegment ? previous!.Translation : "";
        int shared = 0;
        while (shared < prior.Length && shared < record.Translation.Length && prior[shared] == record.Translation[shared]) shared++;
        var change = new CaptionTargetChange(kind, record.SessionId, record.Epoch, record.SegmentId,
            record.SourceRevision, record.TranslationRequestId.Value,
            sameSegment ? previous!.SourceRevision : null, sameSegment ? previous!.TranslationRequestId : null,
            start, sameSegment ? UnitStart(previous!) : null, unitKnown, nowSeconds, firstObserved,
            prior != record.Translation, shared, prior.Length - shared, record.Translation.Length - shared,
            withinUnit ? prior.Length - shared : null, sameSegment && guardInvalidated);
        previous = record;
        guardInvalidated = false;
        return change;
    }
}

public sealed record CaptionTargetObservation(string Phase, string Slot, HistoryRecord Record,
    double AtSeconds, CaptionTargetChange? Change, double? NotBeforeSeconds = null);
