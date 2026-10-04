namespace EchoSub.Desktop;

// Two reading slots: completed lines stay fixed; the open line only appends.
// Revisions coalesce within a unit; distinct units wait in a bounded reading queue.
public sealed record CaptionReadingObservation(string Kind, string? Reason, CaptionIdentity? Identity,
    double AtSeconds, double WaitSeconds, int PendingUnits, int Characters,
    double? BlockedSeconds = null, int? ReusedCharacters = null);
public sealed class CaptionLines
{
    public const double HoldSeconds = 2.5;
    public const double LineStaggerSeconds = 0.25;
    public const int MaximumPendingUnits = 4;
    public const double MaximumPendingSeconds = 10;
    public const int MaximumInputCharacters = 16384;
    public event Action<CaptionReadingObservation>? Observed;
    private sealed record Waiting(CaptionCard Card, string Body, double Since);
    private readonly List<Waiting> pending = new(MaximumPendingUnits);
    public int PendingUnits => pending.Count;
    private CaptionCard? latest;
    private string latestBody = "";
    private double admittedAt;
    private bool firstLineReported;
    private CaptionIdentity? identity;
    private string consumed = "";
    private string? rewrite;
    private int rewritePrefix;
    private bool changeUnit;
    private readonly string?[] lines = new string?[2];
    private readonly CaptionIdentity?[] lineIdentities = new CaptionIdentity?[2];
    private readonly double[] expires = new double[2];
    private int openSlot = -1;
    private CaptionIdentity? lastBlockedIdentity;
    private string? lastBlockedReason;
    private double blockedSince;
    private double blockedQueuedSince;
    private int blockedCharacters;
    public string? Upper => lines[0];
    public string? Lower => lines[1];
    public CaptionIdentity? UpperIdentity => lineIdentities[0];
    public CaptionIdentity? LowerIdentity => lineIdentities[1];
    public CaptionIdentity? Identity => identity;

    public void ValidateSession(string? product, ulong session, ulong epoch, bool running)
    {
        if (!running || identity is not null && (identity.ProductSession != product ||
            identity.Session != session || identity.Epoch != epoch)) Clear();
    }

    public void Clear()
    {
        pending.Clear(); latestBody = ""; firstLineReported = false;
        latest = null; identity = null; consumed = ""; rewrite = null; rewritePrefix = 0; changeUnit = false;
        Array.Clear(lines); Array.Clear(lineIdentities); Array.Clear(expires);
        openSlot = -1;
        lastBlockedIdentity = null; lastBlockedReason = null;
    }

    public void Apply(CaptionCards cards, double now, Func<string, int> lineLength)
    {
        if (cards.OrderedDelivery)
        {
            if (cards.ResetReading) Clear();
            foreach (var segment in cards.InvalidatedSegments) InvalidateSegment(segment, now);
            foreach (var update in cards.Delivered) Update(update, now, lineLength);
            Tick(now, lineLength);
            return;
        }
        // A deck deadline is not a reading deadline: drain the already admitted input.
        // Stop/session invalidation explicitly clears it; new inputs still supersede it.
        if (cards.Current is null && cards.InputExpired) { Tick(now, lineLength); return; }
        Update(cards.Current, now, lineLength);
    }

    public void Update(CaptionCard? card, double now, Func<string, int> lineLength)
    {
        // An empty/invalid input is distinct from an explicitly expired deck card.
        if (card is null) { pending.Clear(); latest = null; latestBody = ""; rewrite = null; rewritePrefix = 0; changeUnit = false; return; }
        if (identity is not null && card.Identity is { } next &&
            (identity.ProductSession != next.ProductSession || identity.Session != next.Session || identity.Epoch != next.Epoch))
            Clear();
        var body = Body(card.Translation);
        if (identity is not null && card.Identity is { } incomingKey && incomingKey.Segment < identity.Segment)
        {
            Report("Dropped", "LateUnit", card.Identity, now, 0, body.Length);
            return;
        }
        if (latest is { } active && active.Identity == card.Identity && Older(card, active)) return;
        if (body.Length > MaximumInputCharacters)
        {
            Report("Dropped", "InputTooLong", card.Identity, now, 0, body.Length);
            return;
        }
        if (body.Length == 0)
        {
            // A pending decode/translation is not a request to blank reading lines.
            // Stop adding from the old input; visible lines retain their fixed deadlines.
            InvalidateSegment(card.Identity?.Segment, now);
            return;
        }
        // Returning to a full/repaired unit supersedes unshown suffix units of that
        // segment. It must not cancel unrelated sentences waiting behind it.
        if (card.Identity is { } incoming)
            for (int index = pending.Count - 1; index >= 0; index--)
                if (pending[index].Card.Identity is { } queued && queued.Segment == incoming.Segment &&
                    queued.UnitStart > incoming.UnitStart)
                    RemovePending(index, "Invalidated", "SourceUnitReset", now);
        var waitingIndex = pending.FindIndex(item => item.Card.Identity == card.Identity);
        if (waitingIndex >= 0)
        {
            var waiting = pending[waitingIndex];
            if (Older(card, waiting.Card)) return;
            if (waiting.Card != card)
            {
                pending[waitingIndex] = waiting with { Card = card, Body = body };
                Report("Coalesced", null, card.Identity, now, now - waiting.Since, body.Length);
            }
            Tick(now, lineLength);
            return;
        }
        // Keep fully shown text and its deadlines for terminal punctuation alone.
        // Revision metadata still advances; a later lexical change is processed
        // normally against the text that was actually shown.
        if (latest is { } shown && identity is not null && identity == card.Identity &&
            shown.Identity == card.Identity && rewrite is null && consumed.Length == latestBody.Length &&
            CaptionAssembly.TerminalFullStopOnlyChange(latestBody, body))
        {
            latest = card;
            if (shown.Translation != card.Translation)
                Report("CosmeticRetained", "TerminalFullStop", card.Identity, now, now - admittedAt, body.Length);
            Tick(now, lineLength);
            return;
        }
        if (latest is not null && latest.Identity != card.Identity)
        {
            Tick(now, lineLength);
            if (pending.Count == MaximumPendingUnits) RemovePending(0, "Dropped", "QueueCapacity", now);
            pending.Add(new(card, body, now));
            Report("Queued", null, card.Identity, now, 0, body.Length);
            Tick(now, lineLength);
            return;
        }
        if (latest is null) { admittedAt = now; firstLineReported = false; }
        latest = card;
        latestBody = body;
        // Recompute from the displayed unit, not a superseded waiting candidate.
        // A final may return to the original unit after a later preview was pending.
        changeUnit = identity != card.Identity;
        if (changeUnit)
        {
            // Direct unit resets wait for visible lines. Queued units may use
            // a free lower slot after the prior input has been fully consumed.
            rewrite = body;
            rewritePrefix = 0;
        }
        else if (!body.StartsWith(consumed, StringComparison.Ordinal))
        {
            // A revised target prefix is a correction, not an appendable suffix.
            rewrite = body;
            rewritePrefix = identity is null ? 0 : CaptionAssembly.ReusablePrefix(consumed, body);
        }
        else { rewrite = null; rewritePrefix = 0; }
        Tick(now, lineLength);
    }

    public void Tick(double now, Func<string, int> lineLength) => Advance(now, lineLength, true);

    private void Advance(double now, Func<string, int> lineLength, bool followOnce)
    {
        for (int slot = 0; slot < 2; slot++)
            if (lines[slot] is not null && now >= expires[slot])
            {
                lines[slot] = null;
                lineIdentities[slot] = null;
                if (openSlot == slot) openSlot = -1;
            }
        for (int index = pending.Count - 1; index >= 0; index--)
            if (now - pending[index].Since > MaximumPendingSeconds)
                RemovePending(index, "Dropped", "QueueAge", now);
        var occupied = lines[0] is not null || lines[1] is not null;
        // A completed unit needs no paragraph-wide barrier. Keep its upper line
        // fixed and admit the next unit into the free lower slot. Never put a
        // newer unit above a retained lower line or bypass a pending correction.
        var canFollowUpper = lines[0] is not null && lines[1] is null &&
            latest is not null && rewrite is null && consumed.Length >= latestBody.Length;
        // Do not discard a final short tail only because earlier queueing used
        // its age budget. It can drain into upper and let the next unit follow
        // into lower. Long tails still obey the bounded overload policy.
        var retainShortTail = false;
        if (!occupied && pending.Count > 0 && latest is not null && rewrite is null &&
            now - admittedAt > MaximumPendingSeconds && consumed.Length < latestBody.Length &&
            latestBody.StartsWith(consumed, StringComparison.Ordinal))
        {
            var tail = latestBody[consumed.Length..];
            retainShortTail = lineLength(tail) >= tail.Length;
            if (retainShortTail)
                Report("TailRetained", "FitsOneLine", latest.Identity, now, now - admittedAt, tail.Length);
        }
        if ((!occupied || canFollowUpper) && pending.Count > 0 && (latest is null ||
            rewrite is null && (consumed.Length >= latestBody.Length ||
                now - admittedAt > MaximumPendingSeconds && !retainShortTail)))
        {
            if (latest is not null && consumed.Length < latestBody.Length)
                Report("Dropped", "UnreadTailAge", latest.Identity, now, now - admittedAt,
                    latestBody.Length - consumed.Length);
            var next = pending[0];
            pending.RemoveAt(0);
            latest = next.Card; latestBody = next.Body; admittedAt = next.Since;
            firstLineReported = false; changeUnit = true; rewrite = latestBody; rewritePrefix = 0;
            if (canFollowUpper)
            {
                identity = latest.Identity;
                consumed = ""; changeUnit = false; rewrite = null; openSlot = -1;
            }
        }
        if (latest is null) { RecordBlocked(now); return; }
        if (changeUnit || rewrite is not null)
        {
            if (occupied) { RecordBlocked(now); return; }
            if (!changeUnit && rewrite is not null)
                Report("CorrectionApplied", null, latest.Identity, now, now - admittedAt,
                    latestBody.Length - rewritePrefix, reusedCharacters: rewritePrefix);
            identity = latest.Identity;
            consumed = changeUnit ? "" : latestBody[..rewritePrefix];
            changeUnit = false;
            rewrite = null;
            rewritePrefix = 0;
            openSlot = -1;
        }
        var body = latestBody;
        if (!body.StartsWith(consumed, StringComparison.Ordinal)) { RecordBlocked(now); return; }
        var remainder = body[consumed.Length..];
        if (openSlot >= 0 && remainder.Length > 0 && lines[openSlot] is { } open)
        {
            var combined = open + remainder;
            var length = Math.Clamp(lineLength(combined), 1, combined.Length);
            var added = Math.Max(0, length - open.Length);
            if (added > 0)
            {
                lines[openSlot] = combined[..length].TrimEnd('\r', '\n');
                consumed += remainder[..added];
                remainder = remainder[added..];
                expires[openSlot] = Math.Max(expires[openSlot], now + HoldSeconds);
            }
            if (remainder.Length > 0 || IsClosed(combined[..Math.Max(open.Length, length)])) openSlot = -1;
        }
        for (int slot = 0; slot < 2 && remainder.Length > 0; slot++)
        {
            if (lines[slot] is not null) continue;
            // Never put a newer fragment above an older retained lower line.
            // Keep its position until read, then resume at the upper slot.
            if (slot == 0 && lines[1] is not null) break;
            var length = Math.Clamp(lineLength(remainder), 1, remainder.Length);
            var fragment = remainder[..length];
            consumed += fragment;
            remainder = remainder[length..];
            if (string.IsNullOrWhiteSpace(fragment)) { slot--; continue; }
            // Consume separators as part of input identity, never render them inside a slot.
            lines[slot] = fragment.TrimEnd('\r', '\n');
            lineIdentities[slot] = identity;
            if (!firstLineReported)
            {
                firstLineReported = true;
                Report("FirstLine", null, identity, now, now - admittedAt, fragment.Length);
            }
            openSlot = remainder.Length == 0 && !IsClosed(fragment) ? slot : -1;
            // Equal reading duration means an older line cannot outlive a newer one.
            // Stagger lines so they do not disappear as a single paragraph.
            var other = 1 - slot;
            var start = lines[other] is not null ? Math.Max(now, expires[other] - HoldSeconds + LineStaggerSeconds) : now;
            expires[slot] = start + HoldSeconds;
        }
        // After draining a short upper tail, use the free lower slot in this
        // same update. One bounded follow-up pass cannot chase an entire queue.
        if (followOnce && pending.Count > 0 && lines[0] is not null && lines[1] is null &&
            rewrite is null && consumed.Length >= latestBody.Length)
            Advance(now, lineLength, false);
        else RecordBlocked(now);
    }

    private void RecordBlocked(double now)
    {
        if (pending.Count == 0) { EndBlocked(now); return; }
        var head = pending[0];
        var reason = changeUnit ? "UnitTransition" : rewrite is not null ? "Correction" :
            lines[0] is not null && lines[1] is not null ? "BothLinesProtected" :
            lines[1] is not null ? "LowerLineProtected" :
            latest is null && lines[0] is not null ? "UpperLineProtected" : "UnreadTail";
        if (lastBlockedIdentity == head.Card.Identity && lastBlockedReason == reason) return;
        EndBlocked(now);
        lastBlockedIdentity = head.Card.Identity; lastBlockedReason = reason;
        blockedSince = now; blockedQueuedSince = head.Since; blockedCharacters = head.Body.Length;
        Report("Blocked", reason, head.Card.Identity, now, now - head.Since, head.Body.Length);
    }

    private void EndBlocked(double now)
    {
        if (lastBlockedReason is not null)
            Report("BlockEnded", lastBlockedReason, lastBlockedIdentity, now,
                now - blockedQueuedSince, blockedCharacters, Math.Max(0, now - blockedSince));
        lastBlockedIdentity = null; lastBlockedReason = null;
    }

    private void InvalidateSegment(ulong? segment, double now)
    {
        for (int index = pending.Count - 1; index >= 0; index--)
            if (segment is null || pending[index].Card.Identity?.Segment == segment)
                RemovePending(index, "Invalidated", "SourceInvalidated", now);
        if (segment is null || latest?.Identity?.Segment == segment)
        {
            latest = null; latestBody = ""; rewrite = null; rewritePrefix = 0; changeUnit = false;
        }
    }

    private void RemovePending(int index, string kind, string reason, double now)
    {
        var item = pending[index];
        pending.RemoveAt(index);
        Report(kind, reason, item.Card.Identity, now, Math.Max(0, now - item.Since), item.Body.Length);
    }

    private void Report(string kind, string? reason, CaptionIdentity? key, double now, double wait, int characters,
        double? blockedSeconds = null, int? reusedCharacters = null) =>
        Observed?.Invoke(new(kind, reason, key, now, Math.Max(0, wait), pending.Count, characters,
            blockedSeconds, reusedCharacters));

    private static bool Older(CaptionCard incoming, CaptionCard current) => incoming.SourceRevision > 0 &&
        (incoming.SourceRevision < current.SourceRevision || incoming.SourceRevision == current.SourceRevision &&
            incoming.TranslationRequestId is > 0 && incoming.TranslationRequestId < current.TranslationRequestId);

    private static bool IsClosed(string text) => text.TrimEnd().EndsWith('.') || text.TrimEnd().EndsWith('。') ||
        text.TrimEnd().EndsWith('!') || text.TrimEnd().EndsWith('?') || text.EndsWith('\n') || text.EndsWith('\r');

    private static string Body(string? text)
    {
        if (text is null) return "";
        foreach (var prefix in Prefixes)
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return text[prefix.Length..];
        return text;
    }
    private static readonly string[] Prefixes = ["[임시 번역] ", "[인식 중] ", "[확정 처리 중] "];
}
