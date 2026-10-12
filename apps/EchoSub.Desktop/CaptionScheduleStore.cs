using System.Globalization;

namespace EchoSub.Desktop;

// Scheduler input/state contract. Candidate overlay diagnostics use it; default cards use CaptionLines.
public sealed record CaptionScheduleContext(string? ProductSession, ulong Session, ulong Epoch);
// Half-open UTF-16 source offsets are comparable only within one source revision.
public sealed record CaptionSourceSpan(int Start, int End, ulong SourceRevision);
public sealed record CaptionScheduleInput(CaptionIdentity Identity, ulong SourceRevision, ulong RequestId,
    bool IsFinal, CaptionSourceSpan? SourceSpan, string Target, double AtSeconds);
public sealed record CaptionScheduleToken(CaptionIdentity Identity, ulong Generation);
public enum CaptionScheduleReason
{
    Accepted, Unchanged, NoSession, WrongContext, InvalidInput, InvalidTime, StaleVersion,
    VersionConflict, CancelledVersion, FinalRegression, Withdrawn, Retired, LateUnit,
    QueueCapacity, QueueAge, StorageCapacity, HistoryTrimmed,
    TargetChanged, SourceRevised, SourceOverlap, UnknownSourceRange, HistoryIncomplete,
    StaleToken, ReadingProtected, AppendOrder, DisplayOrder, IncompleteDisplay, Completed, Stopped,
    InvalidReuseProof, Reused, SeparatorConsumed
}
public sealed record CaptionScheduleResult(bool Accepted, CaptionScheduleReason Reason, CaptionScheduleToken? Token = null);
public sealed record CaptionDisplayedPiece(ulong Id, CaptionIdentity Identity, ulong SourceRevision, ulong RequestId,
    int TargetStart, int TargetEnd, string Text, int Slot, double AssignedAtSeconds, double LastGrowthSeconds,
    double ReadingUntilSeconds, CaptionSourceSpan? SourceSpan = null, bool Reused = false, bool Separator = false);
public sealed record CaptionScheduleLease(ulong PieceId, CaptionIdentity Identity, double ReadingUntilSeconds);
public sealed record CaptionComparisonCandidate(CaptionScheduleToken Token, CaptionScheduleReason Reason, double SinceSeconds);
public sealed record CaptionScheduleSnapshot(CaptionScheduleToken Token, CaptionScheduleInput? Latest,
    bool HasUnread, int DisplayedThrough, bool HistoryComplete, CaptionComparisonCandidate? Comparison,
    IReadOnlyList<CaptionDisplayedPiece> Displayed);
public sealed record CaptionScheduleNotice(CaptionScheduleReason Reason, CaptionIdentity? Identity, double AtSeconds,
    int PendingUnits, int StoredCharacters, int InputCharacters = 0);

public static class CaptionScheduleInputs
{
    public static CaptionScheduleInput? FromRecord(HistoryRecord record, double atSeconds)
    {
        if (!double.IsFinite(atSeconds) || atSeconds < 0 || record.SessionId == 0 || record.Epoch == 0 || record.SegmentId == 0 ||
            record.ProductSessionId?.Length > 128 || string.IsNullOrWhiteSpace(record.Source) ||
            record.Source.Length > CaptionScheduleStore.MaximumInputCharacters ||
            record.Translation.Length > CaptionScheduleStore.MaximumInputCharacters ||
            record.TranslationPrefix.Length > CaptionScheduleStore.MaximumInputCharacters ||
            record.TranslationSource.Length > CaptionScheduleStore.MaximumInputCharacters ||
            record.SourceRevision == 0 || record.TranslationRequestId is null or 0 ||
            record.AppliedSourceRevision != record.SourceRevision || record.TranslationState != "Done" ||
            record.SourceState is not ("Partial" or "Final") || string.IsNullOrWhiteSpace(record.Translation)) return null;
        int start = 0, end = record.Source.Length;
        CaptionSourceSpan? span;
        if (record.TranslationIsPreview)
        {
            // Old records may lack the translated source unit. Mark them unknown, not offset zero proof.
            if (record.TranslationSource.Length == 0) span = null;
            else
            {
                if (!record.Source.StartsWith(record.TranslationPrefix, StringComparison.Ordinal) ||
                    !record.TranslationPrefix.EndsWith(record.TranslationSource, StringComparison.Ordinal)) return null;
                end = record.TranslationPrefix.Length;
                start = end - record.TranslationSource.Length;
                if (!Boundary(record.Source, start) || !Boundary(record.Source, end)) return null;
                span = new(start, end, record.SourceRevision);
            }
        }
        else
        {
            if (record.SourceState != "Final") return null;
            span = new(0, end, record.SourceRevision);
        }
        return new(new(record.ProductSessionId, record.SessionId, record.Epoch, record.SegmentId, start),
            record.SourceRevision, record.TranslationRequestId.Value,
            record.SourceState == "Final" && !record.TranslationIsPreview, span, record.Translation, atSeconds);
    }

    internal static bool Boundary(string text, int index) => index == 0 || index == text.Length ||
        index > 0 && index < text.Length && Array.BinarySearch(StringInfo.ParseCombiningCharacters(text), index) >= 0;
}

// Single UI-thread owner. Text payloads are bounded; notifications retain no text/journal.
public sealed class CaptionScheduleStore
{
    public const int MaximumInputCharacters = 16384;
    public const int MaximumStoredCharacters = 131072;
    public const int MaximumUnits = 64;
    public const int MaximumPendingUnits = 4;
    public const int MaximumDisplayedPieces = 256;
    public const int MaximumPiecesPerUnit = 64;
    public const double MaximumPendingSeconds = 10;
    public const double ReadingSeconds = 2.5;
    private sealed class Unit
    {
        public CaptionScheduleInput? Latest;
        public ulong Revision, Request, Generation;
        public bool Final, Withdrawn, Unread, HistoryComplete = true;
        public int Through;
        public double Since;
        public CaptionComparisonCandidate? Comparison;
        public readonly List<CaptionDisplayedPiece> Pieces = [];
        public int Characters => (Latest?.Target.Length ?? 0) + Pieces.Sum(piece => piece.Text.Length);
    }
    private sealed record Reservation(List<ulong> Segments, List<ulong> Pieces);
    private readonly Dictionary<CaptionIdentity, Unit> units = [];
    private readonly List<CaptionIdentity> waiting = [];
    private readonly CaptionScheduleLease?[] leases = new CaptionScheduleLease?[2];
    private readonly Dictionary<CaptionScheduleReason, ulong> counters = [];
    private CaptionScheduleContext? context;
    private CaptionIdentity? active;
    private ulong generation, pieceId, retiredThrough, admittedThrough;
    private double clock;
    public event Action<CaptionScheduleNotice>? Observed;
    public int PendingUnits => waiting.Count;
    public int TrackedUnits => units.Count;
    public int DisplayedPieces => units.Values.Sum(unit => unit.Pieces.Count);
    public int StoredCharacters => units.Values.Sum(unit => unit.Characters);
    public CaptionScheduleLease? UpperLease => leases[0];
    public CaptionScheduleLease? LowerLease => leases[1];
    public IReadOnlyDictionary<CaptionScheduleReason, ulong> Counters => new Dictionary<CaptionScheduleReason, ulong>(counters);

    public bool Begin(CaptionScheduleContext next)
    {
        if (next.Session == 0 || next.Epoch == 0 || next.ProductSession?.Length > 128) return false;
        Clear(); context = next; clock = 0; return true;
    }
    public void Stop() { Clear(); context = null; Notice(CaptionScheduleReason.Stopped, null); }
    private void Clear()
    {
        units.Clear(); waiting.Clear(); Array.Clear(leases); active = null;
        retiredThrough = admittedThrough = 0;
        // Generation is never reset. A token from an earlier Begin cannot revive an input.
    }
    private bool Matches(CaptionIdentity key) => context is not null && key.ProductSession == context.ProductSession &&
        key.Session == context.Session && key.Epoch == context.Epoch;
    private bool Time(double at)
    {
        if (!double.IsFinite(at) || at < clock) return false;
        clock = at;
        for (int slot = 0; slot < 2; slot++)
            if (leases[slot] is { } lease && at >= lease.ReadingUntilSeconds) leases[slot] = null;
        return true;
    }
    private void Notice(CaptionScheduleReason reason, CaptionIdentity? key, int inputCharacters = 0)
    {
        var count = counters.GetValueOrDefault(reason);
        counters[reason] = count == ulong.MaxValue ? count : count + 1;
        Observed?.Invoke(new(reason, key, clock, PendingUnits, StoredCharacters, inputCharacters));
    }
    private CaptionScheduleResult Reject(CaptionScheduleReason reason, CaptionIdentity? key)
    { Notice(reason, key); return new(false, reason); }
    private CaptionScheduleToken Token(CaptionIdentity key, Unit unit) => new(key, unit.Generation);
    private bool Pinned(ulong id) => leases.Any(lease => lease?.PieceId == id);

    public CaptionScheduleResult Submit(CaptionScheduleInput input)
    {
        var key = input.Identity;
        if (context is null) return Reject(CaptionScheduleReason.NoSession, key);
        if (!Matches(key)) return Reject(CaptionScheduleReason.WrongContext, key);
        if (key.Segment == 0 || key.UnitStart < 0 || key.UnitStart >= MaximumInputCharacters ||
            input.IsFinal && key.UnitStart != 0 || input.SourceRevision == 0 || input.RequestId == 0 ||
            string.IsNullOrWhiteSpace(input.Target) || input.Target.Length > MaximumInputCharacters ||
            input.SourceSpan is { } span && (span.Start != key.UnitStart || span.End <= span.Start ||
                span.End > MaximumInputCharacters || span.SourceRevision != input.SourceRevision))
            return Reject(CaptionScheduleReason.InvalidInput, key);
        if (!Time(input.AtSeconds)) return Reject(CaptionScheduleReason.InvalidTime, key);
        ExpireWaiting();
        if (key.Segment <= retiredThrough) return Reject(CaptionScheduleReason.Retired, key);
        if (key.Segment < admittedThrough) return Reject(CaptionScheduleReason.LateUnit, key);
        units.TryGetValue(key, out var unit);
        if (unit is not null)
        {
            if (unit.Withdrawn) return Reject(CaptionScheduleReason.Withdrawn, key);
            if (input.SourceRevision < unit.Revision || input.SourceRevision == unit.Revision && input.RequestId < unit.Request)
                return Reject(CaptionScheduleReason.StaleVersion, key);
            if (input.SourceRevision == unit.Revision && input.RequestId == unit.Request)
            {
                if (unit.Latest is null) return Reject(CaptionScheduleReason.CancelledVersion, key);
                if (input with { AtSeconds = unit.Latest.AtSeconds } != unit.Latest) return Reject(CaptionScheduleReason.VersionConflict, key);
                return new(true, CaptionScheduleReason.Unchanged, Token(key, unit));
            }
            if (unit.Final && !input.IsFinal) return Reject(CaptionScheduleReason.FinalRegression, key);
        }
        if (!input.IsFinal && units.Any(pair => pair.Key.Segment == key.Segment && pair.Key.UnitStart == 0 && pair.Value.Final))
            return Reject(CaptionScheduleReason.FinalRegression, key);
        var overlap = units.Where(pair => pair.Key.Segment == key.Segment && pair.Key != key).ToArray();
        var superseded = overlap.Where(pair => Supersedes(input, pair.Key, pair.Value)).Select(pair => pair.Key).ToArray();
        bool replacesActive = active is not null && superseded.Contains(active);
        int newWaiting = waiting.Count(queued => !superseded.Contains(queued)) +
            (active == key || replacesActive || waiting.Contains(key) ? 0 : 1);
        var victim = newWaiting > MaximumPendingUnits ? waiting.First(queued => !superseded.Contains(queued)) : null;
        int delta = input.Target.Length - (unit?.Latest?.Target.Length ?? 0) -
            (victim is null ? 0 : units[victim].Latest?.Target.Length ?? 0) -
            superseded.Sum(queued => units[queued].Latest?.Target.Length ?? 0);
        if (!Reserve(key, unit is null ? 1 : 0, delta, 0, out var reservation))
            return Reject(CaptionScheduleReason.StorageCapacity, key);
        Commit(reservation);
        if (victim is not null) CancelUnread(victim, CaptionScheduleReason.QueueCapacity);
        CaptionScheduleReason? comparison = unit?.Comparison?.Reason;
        if (overlap.Length > 0)
        {
            comparison = overlap.Any(pair => pair.Value.Latest?.SourceSpan is not { } other || input.SourceSpan is not { } next ||
                other.SourceRevision != next.SourceRevision) ? CaptionScheduleReason.UnknownSourceRange :
                overlap.Any(pair => pair.Value.Latest!.SourceSpan!.Start < input.SourceSpan!.End &&
                    input.SourceSpan.Start < pair.Value.Latest.SourceSpan.End) ? CaptionScheduleReason.SourceOverlap : comparison;
            // A full/repaired input replaces unread suffix units, not unrelated segments.
            foreach (var supersededKey in superseded)
                CancelUnread(supersededKey, comparison ?? CaptionScheduleReason.SourceOverlap);
        }
        unit ??= new Unit();
        int through = unit.Through;
        if (unit.Latest is null || through > input.Target.Length ||
            !input.Target.AsSpan(0, through).SequenceEqual(unit.Latest.Target.AsSpan(0, through))) through = 0;
        if (comparison is CaptionScheduleReason.SourceRevised or CaptionScheduleReason.SourceOverlap or CaptionScheduleReason.UnknownSourceRange)
            through = 0; // Prior unit coverage cannot prove a full overlapping correction was delivered.
        if (unit.Pieces.Count > 0 && through == 0)
            comparison ??= !unit.HistoryComplete ? CaptionScheduleReason.HistoryIncomplete :
                input.SourceSpan is null ? CaptionScheduleReason.UnknownSourceRange : CaptionScheduleReason.TargetChanged;
        if (!unit.Unread) unit.Since = input.AtSeconds;
        unit.Latest = input; unit.Revision = input.SourceRevision; unit.Request = input.RequestId;
        unit.Final = input.IsFinal; unit.Generation = ++generation; unit.Through = through;
        unit.Unread = true;
        unit.Comparison = comparison is { } reason ? new(Token(key, unit), reason,
            unit.Comparison?.SinceSeconds ?? input.AtSeconds) : null;
        units[key] = unit;
        if (replacesActive) active = key;
        if (active != key && !waiting.Contains(key))
        {
            int index = waiting.FindIndex(queued => queued.Segment > key.Segment || queued.Segment == key.Segment && queued.UnitStart > key.UnitStart);
            waiting.Insert(index < 0 ? waiting.Count : index, key);
        }
        Notice(CaptionScheduleReason.Accepted, key);
        return new(true, CaptionScheduleReason.Accepted, Token(key, unit));
    }

    private static bool Supersedes(CaptionScheduleInput input, CaptionIdentity priorKey, Unit prior) =>
        priorKey.UnitStart > input.Identity.UnitStart && (input.IsFinal && input.Identity.UnitStart == 0 ||
            input.SourceSpan is { } next && prior.Latest?.SourceSpan is { } other &&
            next.SourceRevision == other.SourceRevision && next.Start < other.End && other.Start < next.End);

    public CaptionScheduleSnapshot? Next => Snapshot(active ?? waiting.FirstOrDefault());
    public CaptionScheduleSnapshot? Snapshot(CaptionIdentity? key)
    {
        if (key is null || !units.TryGetValue(key, out var unit)) return null;
        return new(Token(key, unit), unit.Latest, unit.Unread, unit.Through, unit.HistoryComplete,
            unit.Comparison, unit.Pieces.ToArray());
    }
    private Unit? Current(CaptionScheduleToken token) => Matches(token.Identity) &&
        units.TryGetValue(token.Identity, out var unit) && unit.Generation == token.Generation && unit.Unread ? unit : null;

    // Record actual assigned text. Append does not replace an existing reading prefix.
    public CaptionScheduleResult Display(CaptionScheduleToken token, int targetStart, int length, int slot,
        double atSeconds, double readingUntilSeconds, bool append = false, bool retainReadingDeadline = false,
        CaptionReuseProof? retainedLowerProof = null)
    {
        var unit = Current(token);
        if (unit?.Latest is not { } input) return Reject(CaptionScheduleReason.StaleToken, token.Identity);
        if (slot is < 0 or > 1 || targetStart < 0 || length <= 0 || length > input.Target.Length - targetStart ||
            !double.IsFinite(readingUntilSeconds) || !retainReadingDeadline && readingUntilSeconds < atSeconds + ReadingSeconds ||
            !CaptionScheduleInputs.Boundary(input.Target, targetStart) || !CaptionScheduleInputs.Boundary(input.Target, targetStart + length))
            return Reject(CaptionScheduleReason.InvalidInput, token.Identity);
        if (!Time(atSeconds)) return Reject(CaptionScheduleReason.InvalidTime, token.Identity);
        if (Next?.Token != token || targetStart != unit.Through) return Reject(CaptionScheduleReason.DisplayOrder, token.Identity);
        bool wholeSegment = unit.Comparison?.Reason is CaptionScheduleReason.SourceRevised or
            CaptionScheduleReason.SourceOverlap or CaptionScheduleReason.UnknownSourceRange;
        bool retainedLower = slot == 0 && !append && retainedLowerProof is not null &&
            CanDisplayAboveRetainedLower(token, retainedLowerProof, targetStart, length);
        if (!retainedLower && unit.Comparison is not null && leases.Any(lease => lease is not null &&
            (wholeSegment ? lease.Identity.Segment == token.Identity.Segment : lease.Identity == token.Identity) &&
            (lease.Identity != token.Identity || !unit.Pieces.Any(piece => piece.Id == lease.PieceId &&
                piece.SourceRevision == input.SourceRevision && piece.RequestId == input.RequestId))))
            return Reject(CaptionScheduleReason.ReadingProtected, token.Identity);
        var lease = leases[slot];
        var previous = lease is null ? null : unit.Pieces.FirstOrDefault(piece => piece.Id == lease.PieceId);
        if (retainReadingDeadline && (!append || lease is null || previous is null ||
            !string.IsNullOrWhiteSpace(input.Target.Substring(targetStart, length)) ||
            readingUntilSeconds != lease.ReadingUntilSeconds))
            return Reject(CaptionScheduleReason.InvalidInput, token.Identity);
        if (lease is not null && (!append || previous is null || lease.Identity != token.Identity || previous.TargetEnd != targetStart ||
            !input.Target.AsSpan(previous.TargetStart, previous.Text.Length).SequenceEqual(previous.Text.AsSpan())))
            return Reject(CaptionScheduleReason.ReadingProtected, token.Identity);
        if (slot == 0 && leases[1] is not null && !retainedLower) return Reject(CaptionScheduleReason.AppendOrder, token.Identity);
        readingUntilSeconds = Math.Max(readingUntilSeconds, lease?.ReadingUntilSeconds ?? 0);
        if (slot == 1 && leases[0] is { } upper)
            readingUntilSeconds = Math.Max(readingUntilSeconds, upper.ReadingUntilSeconds + CaptionLines.LineStaggerSeconds);
        int delta = length;
        int pieceDelta = previous is null ? 1 : 0;
        if (!Reserve(token.Identity, 0, delta, pieceDelta, out var reservation))
            return Reject(CaptionScheduleReason.StorageCapacity, token.Identity);
        Commit(reservation);
        var text = previous is null ? input.Target.Substring(targetStart, length) : previous.Text + input.Target.Substring(targetStart, length);
        var piece = new CaptionDisplayedPiece(previous?.Id ?? ++pieceId, token.Identity, input.SourceRevision, input.RequestId,
            previous?.TargetStart ?? targetStart, targetStart + length, text, slot,
            previous?.AssignedAtSeconds ?? atSeconds, atSeconds, readingUntilSeconds, input.SourceSpan);
        if (previous is not null) unit.Pieces[unit.Pieces.FindIndex(item => item.Id == previous.Id)] = piece;
        else unit.Pieces.Add(piece);
        leases[slot] = new(piece.Id, token.Identity, readingUntilSeconds);
        unit.Through += length;
        active = token.Identity; waiting.Remove(token.Identity);
        admittedThrough = Math.Max(admittedThrough, token.Identity.Segment);
        Notice(CaptionScheduleReason.Accepted, token.Identity);
        return new(true, CaptionScheduleReason.Accepted, token);
    }
    // Only a complete, unchanged lower sentence may remain below a corrected upper fragment.
    // The proof is checked again at Display; this query never changes coverage or leases.
    public bool CanDisplayAboveRetainedLower(CaptionScheduleToken token, CaptionReuseProof proof, int targetStart, int length)
    {
        var unit = Current(token);
        if (unit?.Latest is not { } input || Next?.Token != token || proof.Token != token ||
            proof.Kind != CaptionReuseKind.SameSourceSentences || unit.Comparison?.Reason != CaptionScheduleReason.TargetChanged ||
            leases[0] is not null || leases[1] is not { } lower || lower.Identity != token.Identity ||
            targetStart != unit.Through || length <= 0 || targetStart < 0 || length > input.Target.Length - targetStart)
            return false;
        var piece = unit.Pieces.FirstOrDefault(piece => piece.Id == lower.PieceId);
        if (piece is null || piece.Slot != 1 || piece.Reused || piece.Separator || piece.SourceSpan != input.SourceSpan ||
            piece.SourceRevision != input.SourceRevision || piece.RequestId >= input.RequestId) return false;
        var verified = CaptionSentenceReuse.Analyze(Snapshot(token.Identity)!).Proof;
        if (verified is null || verified.EvidenceHash != proof.EvidenceHash || !verified.Ranges.SequenceEqual(proof.Ranges)) return false;
        return verified.Ranges.Any(range => targetStart + length == range.Start &&
            range.End - range.Start == piece.Text.Length &&
            input.Target.AsSpan(range.Start, piece.Text.Length).SequenceEqual(piece.Text));
    }
    // Reuse advances only a proven, previously displayed range. It does not touch a visible row or its lease.
    public CaptionScheduleResult Reuse(CaptionReuseProof proof, int targetStart, int targetEnd)
    {
        var token = proof.Token;
        var unit = Current(token);
        if (unit?.Latest is null) return Reject(CaptionScheduleReason.StaleToken, token.Identity);
        if (Next?.Token != token || targetStart != unit.Through)
            return Reject(CaptionScheduleReason.DisplayOrder, token.Identity);
        var snapshot = Snapshot(token.Identity)!;
        var verified = (proof.Kind == CaptionReuseKind.DeliveredLeadingPrefix
            ? CaptionSentenceReuse.AnalyzeLeadingPrefix(snapshot) : CaptionSentenceReuse.Analyze(snapshot)).Proof;
        if (verified is null || verified.EvidenceHash != proof.EvidenceHash ||
            verified.Kind != proof.Kind ||
            !verified.Ranges.SequenceEqual(proof.Ranges) ||
            !verified.Ranges.Contains(new CaptionReuseRange(targetStart, targetEnd)))
            return Reject(CaptionScheduleReason.InvalidReuseProof, token.Identity);
        int length = targetEnd - targetStart;
        if (!Reserve(token.Identity, 0, length, 1, out var reservation))
            return Reject(CaptionScheduleReason.StorageCapacity, token.Identity);
        Commit(reservation);
        // Delivery evidence for the new version is retained under the same ledger/text limits.
        // Slot -1 is evidence reuse, not a new physical line assignment or reading lease.
        unit.Pieces.Add(new(++pieceId, token.Identity, unit.Latest.SourceRevision, unit.Latest.RequestId,
            targetStart, targetEnd, unit.Latest.Target.Substring(targetStart, length), -1,
            clock, clock, clock, unit.Latest.SourceSpan, Reused: true));
        unit.Through = targetEnd;
        active = token.Identity; waiting.Remove(token.Identity);
        admittedThrough = Math.Max(admittedThrough, token.Identity.Segment);
        Notice(CaptionScheduleReason.Reused, token.Identity);
        return new(true, CaptionScheduleReason.Reused, token);
    }

    // Leading separators consume input identity without allocating a blank reading slot.
    public CaptionScheduleResult ConsumeSeparator(CaptionScheduleToken token, int length)
    {
        var unit = Current(token);
        if (unit?.Latest is not { } input) return Reject(CaptionScheduleReason.StaleToken, token.Identity);
        if (Next?.Token != token) return Reject(CaptionScheduleReason.DisplayOrder, token.Identity);
        int start = unit.Through;
        if (length <= 0 || length > input.Target.Length - start ||
            !string.IsNullOrWhiteSpace(input.Target.Substring(start, length)) ||
            !CaptionScheduleInputs.Boundary(input.Target, start + length))
            return Reject(CaptionScheduleReason.InvalidInput, token.Identity);
        if (!Reserve(token.Identity, 0, length, 1, out var reservation))
            return Reject(CaptionScheduleReason.StorageCapacity, token.Identity);
        Commit(reservation);
        unit.Pieces.Add(new(++pieceId, token.Identity, input.SourceRevision, input.RequestId,
            start, start + length, input.Target.Substring(start, length), -1, clock, clock, clock,
            input.SourceSpan, Separator: true));
        unit.Through += length;
        active = token.Identity; waiting.Remove(token.Identity);
        Notice(CaptionScheduleReason.SeparatorConsumed, token.Identity);
        return new(true, CaptionScheduleReason.SeparatorConsumed, token);
    }

    public CaptionScheduleResult Complete(CaptionScheduleToken token)
    {
        var unit = Current(token);
        if (unit?.Latest is null) return Reject(CaptionScheduleReason.StaleToken, token.Identity);
        if (unit.Through != unit.Latest.Target.Length) return Reject(CaptionScheduleReason.IncompleteDisplay, token.Identity);
        unit.Unread = false; unit.Comparison = null; waiting.Remove(token.Identity);
        if (active == token.Identity) active = null;
        Notice(CaptionScheduleReason.Completed, token.Identity);
        return new(true, CaptionScheduleReason.Completed, token);
    }

    public bool Advance(double atSeconds)
    {
        if (!Time(atSeconds)) return false;
        ExpireWaiting();
        return true;
    }
    private void ExpireWaiting()
    {
        for (int index = waiting.Count - 1; index >= 0; index--)
            if (clock - units[waiting[index]].Since > MaximumPendingSeconds)
                CancelUnread(waiting[index], CaptionScheduleReason.QueueAge);
    }
    public bool Invalidate(CaptionScheduleContext owner, ulong segment, double atSeconds, bool withdraw = false)
    {
        if (context is null || context != owner || !Time(atSeconds)) return false;
        foreach (var key in units.Keys.Where(key => key.Segment == segment).ToArray())
        {
            var unit = units[key];
            CancelUnread(key, withdraw ? CaptionScheduleReason.Withdrawn : CaptionScheduleReason.SourceRevised);
            if (withdraw)
            {
                unit.Withdrawn = true; unit.Pieces.Clear(); unit.HistoryComplete = false;
                for (int slot = 0; slot < 2; slot++) if (leases[slot]?.Identity == key) leases[slot] = null;
            }
            else unit.Comparison = new(Token(key, unit), CaptionScheduleReason.SourceRevised, atSeconds);
        }
        return true;
    }
    private void CancelUnread(CaptionIdentity key, CaptionScheduleReason reason)
    {
        var unit = units[key]; int remaining = (unit.Latest?.Target.Length ?? 0) - unit.Through;
        unit.Latest = null; unit.Unread = false; unit.Through = 0; unit.Comparison = null;
        waiting.Remove(key); if (active == key) active = null;
        Notice(reason, key, Math.Max(0, remaining));
    }

    // Preflight retirement/trimming. A rejected reservation does not mutate existing input/history.
    private bool Reserve(CaptionIdentity protectedKey, int addedUnits, int addedCharacters, int addedPieces, out Reservation plan)
    {
        plan = new([], []);
        int unitCount = TrackedUnits + addedUnits, chars = StoredCharacters + addedCharacters, pieces = DisplayedPieces + addedPieces;
        foreach (var group in units.GroupBy(pair => pair.Key.Segment).OrderBy(group => group.Key))
        {
            if (unitCount <= MaximumUnits && chars <= MaximumStoredCharacters) break;
            if (group.Key >= protectedKey.Segment || group.Any(pair => pair.Value.Unread ||
                pair.Value.Pieces.Any(piece => Pinned(piece.Id)))) break;
            plan.Segments.Add(group.Key);
            unitCount -= group.Count(); chars -= group.Sum(pair => pair.Value.Characters); pieces -= group.Sum(pair => pair.Value.Pieces.Count);
        }
        int localPieces = (units.GetValueOrDefault(protectedKey)?.Pieces.Count ?? 0) + addedPieces;
        var retiredSegments = plan.Segments;
        var candidates = units.Where(pair => !retiredSegments.Contains(pair.Key.Segment))
            .SelectMany(pair => pair.Value.Pieces).Where(piece => !Pinned(piece.Id)).OrderBy(piece => piece.LastGrowthSeconds).ThenBy(piece => piece.Id).ToArray();
        foreach (var piece in candidates.Where(piece => piece.Identity == protectedKey))
        {
            if (localPieces <= MaximumPiecesPerUnit) break;
            plan.Pieces.Add(piece.Id); chars -= piece.Text.Length; pieces--;
            localPieces--;
        }
        foreach (var piece in candidates)
        {
            if (pieces <= MaximumDisplayedPieces && chars <= MaximumStoredCharacters) break;
            if (plan.Pieces.Contains(piece.Id)) continue;
            plan.Pieces.Add(piece.Id); chars -= piece.Text.Length; pieces--;
            if (piece.Identity == protectedKey) localPieces--;
        }
        return unitCount <= MaximumUnits && chars <= MaximumStoredCharacters && pieces <= MaximumDisplayedPieces && localPieces <= MaximumPiecesPerUnit;
    }
    private void Commit(Reservation plan)
    {
        foreach (var segment in plan.Segments)
        {
            var retiredKeys = units.Keys.Where(key => key.Segment == segment).ToArray();
            foreach (var key in retiredKeys) units.Remove(key);
            retiredThrough = Math.Max(retiredThrough, segment);
            Notice(CaptionScheduleReason.Retired, retiredKeys.FirstOrDefault());
        }
        foreach (var pair in units)
            if (pair.Value.Pieces.RemoveAll(piece => plan.Pieces.Contains(piece.Id)) > 0)
            { pair.Value.HistoryComplete = false; Notice(CaptionScheduleReason.HistoryTrimmed, pair.Key); }
    }
}
