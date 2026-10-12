using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EchoSub.Desktop;

public enum CaptionReuseReason
{
    Ready, NoHistory, InvalidInput, HistoryIncomplete, UnknownSourceRange, SourceChanged,
    SourceOverlap, FragmentLimit, NoMatch, DeletionOrReorder, AmbiguousSentence
}
public sealed record CaptionReuseRange(int Start, int End);
public enum CaptionReuseKind { SameSourceSentences, DeliveredLeadingPrefix }

// Only the bounded comparator creates proofs. The store rechecks the evidence before use.
public sealed class CaptionReuseProof
{
    public CaptionScheduleToken Token { get; }
    public string EvidenceHash { get; }
    public IReadOnlyList<CaptionReuseRange> Ranges { get; }
    public CaptionReuseKind Kind { get; }
    internal CaptionReuseProof(CaptionScheduleToken token, string hash, CaptionReuseRange[] ranges,
        CaptionReuseKind kind = CaptionReuseKind.SameSourceSentences)
    { Token = token; EvidenceHash = hash; Ranges = Array.AsReadOnly(ranges); Kind = kind; }
}
public sealed record CaptionReuseAnalysis(CaptionReuseReason Reason, CaptionReuseProof? Proof,
    int OldFragments = 0, int NewFragments = 0, int MatrixCells = 0);

// Stateless, exact ordinal sentence comparison. No semantic normalization or model calls.
public static class CaptionSentenceReuse
{
    public const int MaximumFragments = 64;
    public const int MaximumComparedCells = MaximumFragments * MaximumFragments;
    private sealed record Fragment(int Start, int End, bool Closed);
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
        { "Mr", "Mrs", "Ms", "Dr", "Prof", "Sr", "Jr", "St", "vs", "etc", "e.g", "i.e" };

    public static CaptionReuseAnalysis Analyze(CaptionScheduleSnapshot snapshot)
    {
        var input = snapshot.Latest;
        if (!snapshot.HasUnread || input is null || input.Identity != snapshot.Token.Identity ||
            input.Target.Length is 0 or > CaptionScheduleStore.MaximumInputCharacters ||
            snapshot.Displayed.Count > CaptionScheduleStore.MaximumPiecesPerUnit ||
            input.SourceSpan is { } span && (span.Start != input.Identity.UnitStart || span.End <= span.Start ||
                span.End > CaptionScheduleStore.MaximumInputCharacters || span.SourceRevision != input.SourceRevision))
            return new(CaptionReuseReason.InvalidInput, null);
        if (!snapshot.HistoryComplete) return new(CaptionReuseReason.HistoryIncomplete, null);
        if (input.SourceSpan is null) return new(CaptionReuseReason.UnknownSourceRange, null);
        if (snapshot.Comparison?.Reason is CaptionScheduleReason.SourceOverlap or CaptionScheduleReason.UnknownSourceRange)
            return new(CaptionReuseReason.SourceOverlap, null);
        if (snapshot.Comparison?.Reason == CaptionScheduleReason.SourceRevised)
            return new(CaptionReuseReason.SourceChanged, null);
        // Keep the prior version as the baseline after a changed leading sentence is displayed.
        var prior = snapshot.Displayed.Where(piece => piece.SourceRevision != input.SourceRevision || piece.RequestId != input.RequestId)
            .OrderByDescending(piece => piece.SourceRevision).ThenByDescending(piece => piece.RequestId).FirstOrDefault();
        if (prior is null) return new(CaptionReuseReason.NoHistory, null);
        var pieces = snapshot.Displayed.Where(piece => piece.SourceRevision == prior.SourceRevision && piece.RequestId == prior.RequestId)
            .OrderBy(piece => piece.TargetStart).ToArray();
        if (pieces.Any(piece => piece.SourceSpan is null)) return new(CaptionReuseReason.UnknownSourceRange, null);
        if (prior.SourceRevision != input.SourceRevision || prior.RequestId >= input.RequestId ||
            pieces.Any(piece => piece.SourceSpan != input.SourceSpan)) return new(CaptionReuseReason.SourceChanged, null);
        int through = 0;
        var body = new StringBuilder();
        var ids = new HashSet<ulong>();
        foreach (var piece in pieces)
        {
            if (piece.Identity != input.Identity || piece.Id == 0 || !ids.Add(piece.Id) ||
                piece.TargetStart != through || piece.TargetEnd <= through ||
                piece.TargetEnd > CaptionScheduleStore.MaximumInputCharacters ||
                piece.Text.Length != piece.TargetEnd - piece.TargetStart)
                return new(CaptionReuseReason.HistoryIncomplete, null);
            body.Append(piece.Text); through = piece.TargetEnd;
        }
        string old = body.ToString();
        var before = Split(old); var after = Split(input.Target);
        if (before is null || after is null) return new(CaptionReuseReason.FragmentLimit, null);
        int cells = before.Count * after.Count;
        CaptionReuseAnalysis Fail(CaptionReuseReason reason) => new(reason, null, before.Count, after.Count, cells);
        if (before.Count == 0) return Fail(CaptionReuseReason.NoHistory);
        if (after.Count < before.Count) return Fail(CaptionReuseReason.DeletionOrReorder);
        // A duplicate sentence has no unique positional evidence. Do not guess the occurrence.
        bool Duplicate(string text, List<Fragment> parts) => parts.Where(part => part.Closed)
            .Select(part => text[part.Start..part.End]).GroupBy(value => value, StringComparer.Ordinal).Any(group => group.Count() > 1);
        if (Duplicate(old, before) || Duplicate(input.Target, after)) return Fail(CaptionReuseReason.AmbiguousSentence);
        // Whitespace normalization is used only to reject moved sentences, never to prove reuse.
        for (int i = 0; i < before.Count; i++)
            for (int j = 0; j < after.Count; j++)
                if (i != j && before[i].Closed && after[j].Closed &&
                    old.AsSpan(before[i].Start, before[i].End - before[i].Start).Trim()
                        .SequenceEqual(input.Target.AsSpan(after[j].Start, after[j].End - after[j].Start).Trim()))
                    return Fail(CaptionReuseReason.DeletionOrReorder);
        bool Equal(int i, int j) => before[i].Closed && after[j].Closed &&
            old.AsSpan(before[i].Start, before[i].End - before[i].Start)
                .SequenceEqual(input.Target.AsSpan(after[j].Start, after[j].End - after[j].Start));
        var lcs = new int[before.Count + 1, after.Count + 1];
        for (int i = before.Count - 1; i >= 0; i--)
            for (int j = after.Count - 1; j >= 0; j--)
                lcs[i, j] = Equal(i, j) ? 1 + lcs[i + 1, j + 1] : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var ranges = new List<CaptionReuseRange>();
        int a = 0, b = 0;
        while (a < before.Count && b < after.Count)
        {
            if (Equal(a, b))
            {
                if (a != b) return Fail(CaptionReuseReason.DeletionOrReorder);
                ranges.Add(new(after[b].Start, after[b].End)); a++; b++;
            }
            else if (lcs[a + 1, b] >= lcs[a, b + 1]) a++;
            else b++;
        }
        if (ranges.Count == 0) return Fail(CaptionReuseReason.NoMatch);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { snapshot.Token, input, Pieces = pieces }))));
        return new(CaptionReuseReason.Ready, new(snapshot.Token, hash, ranges.ToArray()), before.Count, after.Count, cells);
    }

    // Across source corrections, prove only an exact closed prefix at the same full-source origin.
    // This is delivery evidence, not evidence that the two source sentences have equal meaning.
    public static CaptionReuseAnalysis AnalyzeLeadingPrefix(CaptionScheduleSnapshot snapshot)
    {
        var input = snapshot.Latest;
        if (!snapshot.HasUnread || input is null || input.Identity != snapshot.Token.Identity ||
            input.Identity.UnitStart != 0 || input.Target.Length is 0 or > CaptionScheduleStore.MaximumInputCharacters ||
            snapshot.Displayed.Count > CaptionScheduleStore.MaximumPiecesPerUnit)
            return new(CaptionReuseReason.InvalidInput, null);
        if (!snapshot.HistoryComplete) return new(CaptionReuseReason.HistoryIncomplete, null);
        if (input.SourceSpan is not { Start: 0 } span || span.End <= 0 ||
            span.End > CaptionScheduleStore.MaximumInputCharacters || span.SourceRevision != input.SourceRevision)
            return new(CaptionReuseReason.UnknownSourceRange, null);
        var prior = snapshot.Displayed.Where(piece => piece.SourceRevision < input.SourceRevision ||
            piece.SourceRevision == input.SourceRevision && piece.RequestId < input.RequestId)
            .OrderByDescending(piece => piece.SourceRevision).ThenByDescending(piece => piece.RequestId).FirstOrDefault();
        if (prior is null) return new(CaptionReuseReason.NoHistory, null);
        var pieces = snapshot.Displayed.Where(piece => piece.SourceRevision == prior.SourceRevision && piece.RequestId == prior.RequestId)
            .OrderBy(piece => piece.TargetStart).ToArray();
        int through = 0;
        var body = new StringBuilder();
        var ids = new HashSet<ulong>();
        foreach (var piece in pieces)
        {
            if (piece.SourceSpan is not { Start: 0 } source || source != prior.SourceSpan || source.SourceRevision != piece.SourceRevision ||
                source.End <= 0 || source.End > CaptionScheduleStore.MaximumInputCharacters)
                return new(CaptionReuseReason.UnknownSourceRange, null);
            if (piece.Identity != input.Identity || piece.Id == 0 || !ids.Add(piece.Id) ||
                piece.TargetStart != through || piece.TargetEnd <= through ||
                piece.TargetEnd > CaptionScheduleStore.MaximumInputCharacters || piece.Text.Length != piece.TargetEnd - through)
                return new(CaptionReuseReason.HistoryIncomplete, null);
            body.Append(piece.Text); through = piece.TargetEnd;
        }
        string old = body.ToString();
        var before = Split(old); var after = Split(input.Target);
        if (before is null || after is null) return new(CaptionReuseReason.FragmentLimit, null);
        if (after.Count < before.Count) return new(CaptionReuseReason.DeletionOrReorder, null, before.Count, after.Count);
        int end = 0;
        for (int i = 0; i < Math.Min(before.Count, after.Count); i++)
        {
            var a = before[i]; var b = after[i];
            if (!a.Closed || !b.Closed || a.Start != b.Start || a.End != b.End ||
                !old.AsSpan(a.Start, a.End - a.Start).SequenceEqual(input.Target.AsSpan(b.Start, b.End - b.Start))) break;
            end = b.End;
        }
        if (end == 0) return new(CaptionReuseReason.NoMatch, null, before.Count, after.Count);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { snapshot.Token, input, Pieces = pieces, Kind = CaptionReuseKind.DeliveredLeadingPrefix }))));
        return new(CaptionReuseReason.Ready, new(snapshot.Token, hash, [new(0, end)],
            CaptionReuseKind.DeliveredLeadingPrefix), before.Count, after.Count);
    }

    private static List<Fragment>? Split(string text)
    {
        var parts = new List<Fragment>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is not ('.' or '!' or '?' or '。' or '！' or '？' or '\n')) continue;
            if (c == '.')
            {
                if (i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1])) continue;
                int word = i;
                while (word > 0 && i - word < 16 && (char.IsLetter(text[word - 1]) || text[word - 1] == '.')) word--;
                string token = text[word..i];
                if (token.Length == 1 && char.IsLetter(token[0]) || Abbreviations.Contains(token)) continue;
            }
            int end = i + 1;
            while (end < text.Length && text[end] is '.' or '!' or '?' or '。' or '！' or '？' or '"' or '\'' or '”' or '’' or '」' or '』') end++;
            if (c is '.' or '!' or '?' && end < text.Length && !char.IsWhiteSpace(text[end])) continue;
            while (end < text.Length && char.IsWhiteSpace(text[end])) end++;
            if (!CaptionScheduleInputs.Boundary(text, end)) continue;
            parts.Add(new(start, end, true));
            if (parts.Count > MaximumFragments) return null;
            start = end; i = end - 1;
        }
        if (start < text.Length) parts.Add(new(start, text.Length, false));
        return parts.Count > MaximumFragments ? null : parts;
    }
}
