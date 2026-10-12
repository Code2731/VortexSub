namespace EchoSub.Desktop;

public enum CaptionDeliveryReason { UnsupportedOrder, MissingInput, IdentityMismatch, BodyMismatch, InvalidTime }
// Adapter for deck-selected records. It never reconstructs worker identity from a rendered label.
public sealed class CaptionScheduleDelivery
{
    private CaptionScheduleContext? context;
    private readonly Dictionary<CaptionDeliveryReason, ulong> counters = [];
    public CaptionScheduledLines Reader { get; } = new();
    public event Action<CaptionReadingObservation>? Observed;
    public CaptionScheduleDelivery() { Reader.Observed += observation => Observed?.Invoke(observation); }
    public IReadOnlyDictionary<CaptionDeliveryReason, ulong> Counters => new Dictionary<CaptionDeliveryReason, ulong>(counters);
    private void Reject(CaptionDeliveryReason reason, double now, CaptionIdentity? identity = null)
    {
        var count = counters.GetValueOrDefault(reason); counters[reason] = count == ulong.MaxValue ? count : count + 1;
        Observed?.Invoke(new("InputRejected", "Delivery" + reason, identity,
            double.IsFinite(now) && now >= 0 ? now : 0, 0, Reader.Store.PendingUnits, 0));
    }
    public void Clear() { Reader.Stop(); context = null; }
    public void Apply(CaptionCards cards, double now, Func<string, int> fit)
    {
        if (!double.IsFinite(now) || now < 0) { Reject(CaptionDeliveryReason.InvalidTime, now); return; }
        if (cards.ScheduleContext is null && cards.Current is null && cards.Delivered.Count == 0) { Clear(); return; }
        if (!cards.OrderedDelivery) { Reject(CaptionDeliveryReason.UnsupportedOrder, now); return; }
        if (cards.ScheduleContext is not { } owner) { Clear(); return; }
        if (context != owner || cards.ResetReading)
        {
            if (!Reader.Begin(owner)) { Reject(CaptionDeliveryReason.IdentityMismatch, now); return; }
            context = owner;
        }
        if (!Reader.Store.Advance(now)) { Reject(CaptionDeliveryReason.InvalidTime, now); return; }
        foreach (var segment in cards.InvalidatedSegments) Reader.Invalidate(segment, now, true);
        foreach (var segment in cards.RevisedSegments)
            if (!cards.InvalidatedSegments.Contains(segment)) Reader.Invalidate(segment, now);
        foreach (var card in cards.Delivered)
        {
            if (card.ScheduleInput is not { } input) { Reject(CaptionDeliveryReason.MissingInput, now, card.Identity); continue; }
            if (input.Identity != card.Identity || input.Identity.ProductSession != owner.ProductSession ||
                input.Identity.Session != owner.Session || input.Identity.Epoch != owner.Epoch)
            { Reject(CaptionDeliveryReason.IdentityMismatch, now, card.Identity); continue; }
            string expected = input.IsFinal ? input.Target : "[임시 번역] " + input.Target;
            if (card.Translation != expected) { Reject(CaptionDeliveryReason.BodyMismatch, now, card.Identity); continue; }
            if (cards.InvalidatedSegments.Contains(input.Identity.Segment)) continue;
            Reader.Submit(input with { AtSeconds = now }, fit);
        }
        Reader.Tick(now, fit);
    }
    public static CaptionRegressionDriver StateDriver()
    {
        var adapter = new CaptionScheduleDelivery();
        int Fit(string text) => text.IndexOfAny(['\r', '\n']) is var end && end >= 0
            ? end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1) : Math.Min(32, text.Length);
        return new(adapter.Clear, (cards, now) => adapter.Apply(cards, now, Fit), now => adapter.Reader.Tick(now, Fit),
            () => new(adapter.Reader.Upper ?? "", adapter.Reader.Lower ?? "", adapter.Reader.UpperIdentity, adapter.Reader.LowerIdentity),
            observer => adapter.Reader.Observed += observer);
    }
}
