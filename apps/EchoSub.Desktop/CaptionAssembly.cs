using System.Globalization;

namespace EchoSub.Desktop;

// Exact textual reuse only. A changed clause is always shown again in full.
public static class CaptionAssembly
{
    public const int MaximumComparisonCharacters = 16384;

    public static bool TerminalFullStopOnlyChange(string displayed, string incoming)
    {
        if (displayed == incoming || displayed.Length > MaximumComparisonCharacters ||
            incoming.Length > MaximumComparisonCharacters || displayed.Any(char.IsDigit) || incoming.Any(char.IsDigit))
            return false;
        static string WithoutStop(string text) => text.Length > 1 && text[^1] is '.' or '。'
            && char.IsLetter(text[^2]) ? text[..^1] : text;
        var body = WithoutStop(displayed);
        if (body.Length == 0 || body != WithoutStop(incoming)) return false;
        var start = body.Length;
        while (start > 0 && char.IsLetter(body[start - 1])) start--;
        var word = body[start..].ToLowerInvariant();
        // A single Latin initial and common abbreviations are not sentence stops.
        return !(word.Length == 1 && word[0] is >= 'a' and <= 'z' ||
            word is "mr" or "mrs" or "ms" or "dr" or "prof" or "vs" or "e.g" or "i.e");
    }

    public static int ReusablePrefix(string displayed, string incoming)
    {
        var limit = Math.Min(MaximumComparisonCharacters, Math.Min(displayed.Length, incoming.Length));
        var common = 0;
        while (common < limit && displayed[common] == incoming[common]) common++;
        var end = 0;
        for (int i = 0; i < common; i++)
        {
            var c = incoming[i];
            if (!".!?。！？".Contains(c)) continue;
            if (i + 1 < incoming.Length && (incoming[i + 1] == '\u200D' || CharUnicodeInfo.GetUnicodeCategory(incoming, i + 1)
                is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)) continue;
            if (c == '.')
            {
                if (i > 0 && char.IsDigit(incoming[i - 1]) && i + 1 < incoming.Length && char.IsDigit(incoming[i + 1])) continue;
                if (i > 0 && incoming[i - 1] == '.' || i + 1 < incoming.Length && incoming[i + 1] == '.') continue;
                var start = i;
                while (start > 0 && char.IsLetter(incoming[start - 1])) start--;
                var word = incoming[start..i].ToLowerInvariant();
                if (word.Length == 1 && word[0] <= 'z' && word[0] >= 'a' || word is "mr" or "mrs" or "ms" or "dr" or "prof" or "vs") continue;
            }
            if (c is '.' or '!' or '?' && i + 1 < incoming.Length && !char.IsWhiteSpace(incoming[i + 1])) continue;
            end = i + 1;
        }
        // A shortened final must remain visible: deleting an old clause is a correction.
        if (end == 0 || incoming.AsSpan(end).Trim().IsEmpty) return 0;
        while (end < common && char.IsWhiteSpace(incoming[end])) end++;
        return end;
    }
}
