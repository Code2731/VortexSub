using System.Globalization;

namespace EchoSub.Desktop;

// Bounded live reader. Direct diagnostic inputs use a separate instance from deck delivery.
public sealed class CaptionScheduledLines
{
    public const int MaximumStepsPerTick = 8;
    private sealed record Row(ulong PieceId, CaptionIdentity Identity, int Start, string Raw, bool Open);
    private readonly Row?[] rows = new Row?[2];
    private CaptionScheduleContext? context;
    private CaptionReuseProof? proof;
    private CaptionScheduleToken? analyzed;
    private CaptionScheduleToken? firstLineToken;
    private CaptionScheduleToken? blockedToken;
    private string? blockedReason;
    private double blockedSince, queuedSince, lastNow;
    private int blockedCharacters;
    public event Action<CaptionReadingObservation>? Observed;
    public CaptionScheduledLines()
    {
        Store.Observed += notice =>
        {
            if (notice.Reason is CaptionScheduleReason.QueueCapacity or CaptionScheduleReason.QueueAge)
                Observed?.Invoke(new("Dropped", notice.Reason.ToString(), notice.Identity, notice.AtSeconds, 0, notice.PendingUnits, notice.InputCharacters));
        };
    }
    public CaptionScheduleStore Store { get; } = new();
    public bool OwnsContext(string? product, ulong session, ulong epoch) => context == new CaptionScheduleContext(product, session, epoch);
    public string? Upper => rows[0]?.Raw.TrimEnd('\r', '\n');
    public string? Lower => rows[1]?.Raw.TrimEnd('\r', '\n');
    public CaptionIdentity? UpperIdentity => rows[0]?.Identity;
    public CaptionIdentity? LowerIdentity => rows[1]?.Identity;
    public CaptionIdentity? Identity => Store.Next?.Token.Identity ?? LowerIdentity ?? UpperIdentity;
    public bool Begin(CaptionScheduleContext owner)
    {
        if (!Store.Begin(owner)) return false;
        EndBlocked(lastNow);
        context = owner; Array.Clear(rows); proof = null; analyzed = null; lastNow = 0; return true;
    }
    public void Stop() { EndBlocked(lastNow); Store.Stop(); context = null; Array.Clear(rows); proof = null; analyzed = null; }
    public void ValidateSession(string? product, ulong session, ulong epoch, bool running)
    {
        if (!running || context != new CaptionScheduleContext(product, session, epoch)) Stop();
    }
    public bool Invalidate(ulong segment, double now, bool withdraw = false)
    {
        if (context is null || !Store.Invalidate(context, segment, now, withdraw)) return false;
        lastNow = now;
        if (blockedToken?.Identity.Segment == segment) EndBlocked(now);
        for (int slot = 0; slot < 2; slot++)
            if (rows[slot]?.Identity.Segment == segment)
                rows[slot] = withdraw ? null : rows[slot]! with { Open = false };
        proof = null; analyzed = null; return true;
    }
    public CaptionScheduleResult Submit(CaptionScheduleInput input, Func<string, int> fit)
    {
        var result = Store.Submit(input);
        if (!result.Accepted)
            Observed?.Invoke(new("InputRejected", result.Reason.ToString(), input.Identity, lastNow, 0, Store.PendingUnits, input.Target.Length));
        if (result.Accepted) Tick(input.AtSeconds, fit);
        return result;
    }
    public void Tick(double now, Func<string, int> fit)
    {
        if (!Store.Advance(now)) return;
        lastNow = now;
        try { TickCore(now, fit); }
        finally { RecordBlocked(now); }
    }
    private void TickCore(double now, Func<string, int> fit)
    {
        for (int slot = 0; slot < 2; slot++)
            if (rows[slot]?.PieceId != Lease(slot)?.PieceId) rows[slot] = null;
        for (int step = 0; step < MaximumStepsPerTick; step++)
        {
            var next = Store.Next;
            if (next?.Latest is not { } input) return;
            if (next.DisplayedThrough == input.Target.Length)
            { if (!Store.Complete(next.Token).Accepted) return; continue; }
            if (analyzed != next.Token)
            {
                analyzed = next.Token;
                proof = CaptionSentenceReuse.Analyze(next).Proof ?? CaptionSentenceReuse.AnalyzeLeadingPrefix(next).Proof;
            }
            var reuse = proof?.Ranges.FirstOrDefault(range => range.Start == next.DisplayedThrough);
            if (reuse is not null)
            {
                var result = Store.Reuse(proof!, reuse.Start, reuse.End);
                if (result.Accepted)
                {
                    EndBlocked(now);
                    Observed?.Invoke(new("Reused", proof!.Kind.ToString(), input.Identity, now,
                        Math.Max(0, now - input.AtSeconds), Store.PendingUnits, reuse.End - reuse.Start,
                        ReusedCharacters: reuse.End - reuse.Start));
                    continue;
                }
                // Evidence may have retired. Keep the unread text and fall back to display.
                proof = null;
            }
            int end = proof?.Ranges.Where(range => range.Start > next.DisplayedThrough)
                .Select(range => range.Start).DefaultIfEmpty(input.Target.Length).Min() ?? input.Target.Length;
            string remainder = input.Target[next.DisplayedThrough..end];
            int open = rows[1]?.Open == true ? 1 : rows[0]?.Open == true && rows[1] is null ? 0 : -1;
            if (open >= 0 && rows[open] is { } row && row.Identity == input.Identity &&
                row.Start + row.Raw.Length == next.DisplayedThrough &&
                input.Target.AsSpan(row.Start, row.Raw.Length).SequenceEqual(row.Raw))
            {
                string combined = row.Raw + remainder;
                int length = Fit(combined, fit);
                int added = length - row.Raw.Length;
                if (added > 0)
                {
                    bool separator = string.IsNullOrWhiteSpace(remainder[..added]);
                    var result = Store.Display(next.Token, next.DisplayedThrough, added, open, now,
                        separator ? Lease(open)!.ReadingUntilSeconds : now + CaptionScheduleStore.ReadingSeconds,
                        append: true, retainReadingDeadline: separator);
                    if (!result.Accepted) return;
                    EndBlocked(now);
                    rows[open] = row with { Raw = combined[..length], Open = length == combined.Length && !Closed(combined[..length]) };
                    continue;
                }
                rows[open] = row with { Open = false };
            }
            int slot = rows[0] is null && rows[1] is null ? 0 : rows[1] is null ? 1 : -1;
            bool retainedLower = false;
            if (slot < 0 && rows[0] is null && rows[1] is { Open: false } && proof is not null &&
                Store.CanDisplayAboveRetainedLower(next.Token, proof, next.DisplayedThrough, Fit(remainder, fit)))
            { slot = 0; retainedLower = true; }
            if (slot < 0) return;
            int separators = 0;
            while (separators < remainder.Length && char.IsWhiteSpace(remainder[separators])) separators++;
            if (separators > 0)
            {
                if (!Store.ConsumeSeparator(next.Token, separators).Accepted) return;
                continue;
            }
            int count = Fit(remainder, fit);
            var display = Store.Display(next.Token, next.DisplayedThrough, count, slot, now,
                now + CaptionScheduleStore.ReadingSeconds, retainedLowerProof: retainedLower ? proof : null);
            if (!display.Accepted) return;
            EndBlocked(now);
            rows[slot] = new(Lease(slot)!.PieceId, input.Identity, next.DisplayedThrough,
                remainder[..count], count == remainder.Length && !Closed(remainder[..count]));
            if (firstLineToken != next.Token)
            {
                firstLineToken = next.Token;
                Observed?.Invoke(new("FirstLine", retainedLower ? "RetainedLowerCorrection" : null, input.Identity, now, Math.Max(0, now - input.AtSeconds), Store.PendingUnits, count));
            }
        }
    }
    private void RecordBlocked(double now)
    {
        var next = Store.Next;
        if (next?.Latest is not { } input || next.DisplayedThrough == input.Target.Length)
        { EndBlocked(now); return; }
        string reason = next.Comparison is not null ? "Correction" :
            rows[0] is not null && rows[1] is not null ? "BothLinesProtected" :
            rows[1] is not null ? "LowerLineProtected" : rows[0] is not null ? "UpperLineProtected" : "UnreadTail";
        if (blockedToken == next.Token && blockedReason == reason) return;
        EndBlocked(now);
        blockedToken = next.Token; blockedReason = reason; blockedSince = now; queuedSince = input.AtSeconds;
        blockedCharacters = input.Target.Length - next.DisplayedThrough;
        Observed?.Invoke(new("Blocked", reason, input.Identity, now, Math.Max(0, now - queuedSince), Store.PendingUnits, blockedCharacters));
    }
    private void EndBlocked(double now)
    {
        if (blockedToken is not null)
            Observed?.Invoke(new("BlockEnded", blockedReason, blockedToken.Identity, now,
                Math.Max(0, now - queuedSince), Store.PendingUnits, blockedCharacters, Math.Max(0, now - blockedSince)));
        blockedToken = null; blockedReason = null;
    }
    private CaptionScheduleLease? Lease(int slot) => slot == 0 ? Store.UpperLease : Store.LowerLease;
    private static int Fit(string text, Func<string, int> fit)
    {
        int proposed = Math.Clamp(fit(text), 1, text.Length);
        if (CaptionScheduleInputs.Boundary(text, proposed)) return proposed;
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        int index = Array.BinarySearch(boundaries, proposed);
        index = index >= 0 ? index : ~index - 1;
        return index > 0 ? boundaries[index] : boundaries.Length > 1 ? boundaries[1] : text.Length;
    }
    private static bool Closed(string text) => text.EndsWith('\n') || text.EndsWith('\r') ||
        text.TrimEnd().EndsWith('.') || text.TrimEnd().EndsWith('。') || text.TrimEnd().EndsWith('!') || text.TrimEnd().EndsWith('?');
}
