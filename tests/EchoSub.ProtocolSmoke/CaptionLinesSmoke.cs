using EchoSub.Desktop;

internal static class CaptionLinesSmoke
{
    public static void Run()
    {
        int count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new IOException("Caption lines: " + name);
            count++;
        }
        CaptionCard Card(string? text, ulong segment = 1, ulong epoch = 1) =>
            new("source", text, true, "source") { Identity = new("session", 1, epoch, segment, 0) };
        int Fit(string text) => Math.Min(4, text.Length);
        var lines = new CaptionLines();
        lines.Update(Card("ABCD"), 0, Fit);
        Check(lines.Upper == "ABCD" && lines.Lower is null, "first line immediate");
        for (int n = 1; n <= 40; n++)
        {
            var priorLower = lines.Lower ?? "";
            lines.Update(Card("ABCD" + new string('X', n)), n * .025, Fit);
            Check(lines.Upper == "ABCD" && (lines.Lower ?? "").StartsWith(priorLower) && lines.Lower?.Length <= 4,
                "character addition retains closed line and appends open line " + n);
        }
        lines.Update(Card(null), 1.1, Fit);
        Check(lines.Upper == "ABCD" && lines.Lower == "XXXX", "pending input does not blank lines");
        lines.Update(null, 1.2, Fit);
        Check(lines.Upper == "ABCD" && lines.Lower == "XXXX", "input expiry does not blank lines");
        lines.Tick(2.51, Fit);
        Check(lines.Upper is null && lines.Lower == "XXXX", "oldest line expires first");
        lines.Tick(3.4, Fit);
        Check(lines.Upper is null && lines.Lower is null, "second line expires without resurrection");
        lines.Clear(); lines.Update(Card("ABCDEFGH"), 0, Fit);
        lines.Update(Card("WXYZQRST"), .1, Fit);
        Check(lines.Upper == "ABCD" && lines.Lower == "EFGH", "prefix correction does not clear reading lines");
        lines.Tick(3.4, Fit);
        Check(lines.Upper == "WXYZ" && lines.Lower == "QRST", "correction applied atomically after reading");
        lines.Clear(); lines.Update(Card("ABCD"), 0, Fit);
        lines.Update(Card("OLD", 2), .1, Fit);
        lines.Update(Card("NEW", 3), .2, Fit);
        lines.Tick(2.6, Fit);
        Check(lines.Upper is null && lines.Lower == "OLD" && lines.Identity?.Segment == 2, "newer line stays lower until its reading deadline");
        lines.Tick(5.2, Fit);
        Check(lines.Upper == "NEW" && lines.Identity?.Segment == 3, "following pending unit is shown after reading");
        lines.ValidateSession("session", 1, 2, true);
        Check(lines.Upper is null && lines.Lower is null, "epoch change clears lines");
        lines.Update(Card("ABCD", epoch: 2), 3, Fit);
        lines.ValidateSession("session", 1, 2, false);
        Check(lines.Upper is null && lines.Lower is null, "pause clears lines");
        lines.Tick(10, Fit);
        Check(lines.Upper is null && lines.Lower is null, "stop never resurrects old input");
        lines.Update(Card("ABCD"), 11, Fit);
        lines.Update(Card(null, epoch: 3), 11.1, Fit);
        Check(lines.Upper is null, "new epoch with pending translation cannot retain old lines");
        int FitSeparated(string text)
        {
            var separator = text.IndexOfAny(['\r', '\n']);
            return separator >= 0 ? separator + (text[separator] == '\r' && separator + 1 < text.Length &&
                text[separator + 1] == '\n' ? 2 : 1) : text.Length;
        }
        foreach (var separator in new[] { "\n", "\r\n", "\r" })
        {
            lines.Clear(); lines.Update(Card("first" + separator + "second"), 0, FitSeparated);
            Check(lines.Upper == "first" && lines.Lower == "second", "separator consumes input without rendering " + separator.Length);
            lines.Clear(); lines.Update(Card("first"), 0, FitSeparated);
            lines.Update(Card("first" + separator + "second"), .1, FitSeparated);
            Check(lines.Upper == "first" && lines.Lower == "second", "appended separator closes open line " + separator.Length);
        }
        lines.Clear(); lines.Update(Card("\r\n\nfirst\n\nsecond"), 0, FitSeparated);
        Check(lines.Upper == "first" && lines.Lower == "second", "leading and empty lines do not consume reading slots");
        lines.Clear(); lines.Update(Card("abcdefghijklmnop"), 0, Fit);
        Check(lines.Upper == "abcd" && lines.Lower == "efgh", "long input fills bounded two slots");
        lines.Tick(2.6, Fit);
        Check(lines.Upper is null && lines.Lower == "efgh", "upper waits rather than showing newer text above retained lower line");
        lines.Tick(3.4, Fit);
        Check(lines.Upper == "ijkl" && lines.Lower == "mnop", "remaining suffix drains without paragraph replacement");
        lines.Tick(6.8, Fit);
        Check(lines.Upper is null && lines.Lower is null, "consumed long input never replays");
        int FitSentence(string text) { var end = text.IndexOf('.'); return end >= 0 ? end + 1 : text.Length; }
        lines.Clear(); lines.Update(Card("문 앞에서 기다려. 왼쪽 길로 가세요."), 0, FitSentence);
        lines.Update(Card("문 앞에서 기다려. 오른쪽 길로 가세요."), .1, FitSentence);
        Check(lines.Upper == "문 앞에서 기다려." && lines.Lower == " 왼쪽 길로 가세요.", "assembly preserves protected clauses");
        lines.Tick(3.4, FitSentence);
        Check(lines.Upper == "오른쪽 길로 가세요." && lines.Lower is null, "assembly omits unchanged finished clause and retains direction correction");
        lines.Update(Card("문 앞에서 기다려. 오른쪽 길로 가세요."), 3.5, FitSentence);
        lines.Tick(6, FitSentence);
        Check(lines.Upper is null && lines.Lower is null, "assembled correction does not replay on identical final");
        lines.Clear(); lines.Update(Card("문 앞에서 기다려. 문을 열어 주세요."), 0, FitSentence);
        lines.Update(Card("문 앞에서 기다리지 마. 문을 열지 마세요."), .1, FitSentence);
        lines.Tick(3.4, FitSentence);
        Check(lines.Upper == "문 앞에서 기다리지 마." && lines.Lower == " 문을 열지 마세요.", "changed first clause and negation are never elided");
        lines.Clear(); lines.Update(Card("문 앞에서 기다려. 왼쪽 길로 가세요."), 0, FitSentence);
        lines.Update(Card("문 앞에서 기다려."), .1, FitSentence);
        lines.Tick(3.4, FitSentence);
        Check(lines.Upper == "문 앞에서 기다려.", "shortening restores final rather than hiding removed clause correction");
        Check(CaptionAssembly.ReusablePrefix("숫자는 13입니다.", "숫자는 30입니다.") == 0, "changed number cannot be elided");
        Check(CaptionAssembly.ReusablePrefix("문을 열어 주세요.", "문을 열지 마세요.") == 0, "changed negation cannot be elided");
        Check(CaptionAssembly.ReusablePrefix("Do it only if the shield is down.", "Do it only if the shield is up.") == 0, "changed condition cannot be elided");
        Check(CaptionAssembly.ReusablePrefix("Value 3.14 is safe.", "Value 3.15 is safe.") == 0, "decimal point is not a reusable sentence boundary");
        Check(CaptionAssembly.ReusablePrefix("Dr. Smith goes left.", "Dr. Smith goes right.") == 0, "title abbreviation is not a reusable sentence boundary");
        Check(CaptionAssembly.ReusablePrefix("Wait... go left.", "Wait... go right.") == 0, "ellipsis is not a completed sentence");
        Check(CaptionAssembly.ReusablePrefix("Wait。\uFE0F left.", "Wait。\uFE0F right.") == 0, "sentence reuse cannot split punctuation variation selector");
        Check(CaptionAssembly.ReusablePrefix("문 앞에서 기다려. 왼쪽으로 가세요.", "문 앞에서 기다려. 오른쪽으로 가세요.") == "문 앞에서 기다려. ".Length,
            "only exact complete preceding clause is reused");
        Check(CaptionAssembly.ReusablePrefix("👨‍👩‍👧‍👦 기다려. 왼쪽.", "👨‍👩‍👧‍👦 기다려. 오른쪽.") == "👨‍👩‍👧‍👦 기다려. ".Length,
            "reuse boundary never splits a grapheme");
        lines.Clear(); lines.Update(Card("문 앞에서 기다려. 왼쪽 길로 가세요."), 0, FitSentence);
        lines.Update(Card("문 앞에서 기다려. 오른쪽 길로 가세요.", 2), .1, FitSentence);
        lines.Tick(3.4, FitSentence);
        Check(lines.Upper == "문 앞에서 기다려.", "new segment cannot inherit reusable prior clause");
        lines.Clear();
        lines.Update(new CaptionCard("", "문 앞에서 기다려. 왼쪽 길로 가세요.", true, ""), 0, FitSentence);
        lines.Update(new CaptionCard("", "문 앞에서 기다려. 오른쪽 길로 가세요.", true, ""), .1, FitSentence);
        lines.Tick(3.4, FitSentence);
        Check(lines.Upper == "문 앞에서 기다려.", "unknown identity cannot reuse prior target clauses");
        var repeatedClauses = string.Concat(Enumerable.Repeat("Wait here. ", 2000));
        const string original = "문 앞에서 기다려. 왼쪽 길로 가세요.";
        const string correction = "문 앞에서 기다려. 오른쪽 길로 가세요.";
        lines.Clear(); lines.Update(Card(original), 0, FitSentence);
        lines.Update(Card("다음 구간.") with { Identity = new("session", 1, 1, 1, 20) }, .1, FitSentence);
        lines.Update(Card(correction), .2, FitSentence);
        lines.Tick(3.4, FitSentence);
        Check(lines.Upper == "오른쪽 길로 가세요." && lines.Lower is null,
            "return to displayed unit cancels pending unit reset and reuses finished prefix");
        lines.Clear(); lines.Update(Card(original), 0, FitSentence);
        lines.Update(Card("다음 구간.", 2), .1, FitSentence);
        lines.Update(Card(original), .2, FitSentence);
        lines.Tick(3.4, FitSentence);
        Check(lines.Upper == "다음 구간." && lines.Identity?.Segment == 2, "return to identical target preserves unrelated pending segment");
        lines.Clear(); lines.Update(Card(original), 0, FitSentence);
        foreach (var (target, time) in new[] { (correction, .1), ("문 앞에서 기다려. 뒤쪽 길로 가세요.", .2),
            ("문 앞에서 기다려. 문을 열지 마세요.", .3) }) lines.Update(Card(target), time, FitSentence);
        Check(lines.Upper == "문 앞에서 기다려." && lines.Lower == " 왼쪽 길로 가세요.", "rapid corrections preserve unread original");
        lines.Tick(3.4, FitSentence);
        Check(lines.Upper == "문을 열지 마세요." && lines.Lower is null, "rapid corrections show only latest negation");
        lines.Clear(); lines.Update(Card(original), 0, FitSentence);
        lines.Update(Card(correction), .1, FitSentence);
        lines.Update(Card(original), .2, FitSentence);
        lines.Tick(3.4, FitSentence);
        Check(lines.Upper is null && lines.Lower is null, "reverted correction never resurrects");
        foreach (var empty in new CaptionCard?[] { null, Card(null), Card("") })
        {
            lines.Clear(); lines.Update(Card(original), 0, FitSentence);
            lines.Update(Card(correction), .1, FitSentence);
            lines.Update(empty, .2, FitSentence);
            lines.Tick(3.4, FitSentence);
            Check(lines.Upper is null && lines.Lower is null, "invalid pending correction cannot resume " + (empty?.Translation ?? "null"));
            lines.Update(Card("새 결과."), 3.5, FitSentence);
            Check(lines.Upper == "새 결과.", "fresh target recovers after invalid pending input");
        }
        foreach (var transition in new[] { new CaptionIdentity("session", 1, 2, 1, 0),
            new CaptionIdentity("session", 2, 1, 1, 0), new CaptionIdentity("other", 1, 1, 1, 0) })
        {
            lines.Clear(); lines.Update(Card(original), 0, FitSentence);
            lines.Update(Card(correction), .1, FitSentence);
            lines.ValidateSession(transition.ProductSession, transition.Session, transition.Epoch, true);
            lines.Tick(4, FitSentence);
            Check(lines.Upper is null && lines.Lower is null, "context transition discards pending correction " + transition);
            lines.Update(Card("새 결과.") with { Identity = transition }, 4.1, FitSentence);
            Check(lines.Upper == "새 결과." && lines.Identity == transition, "context transition admits only new target " + transition);
        }
        lines.Clear(); lines.Update(Card(original), 0, FitSentence);
        lines.Update(Card(correction), .1, FitSentence);
        lines.ValidateSession("session", 1, 1, false);
        lines.Tick(4, FitSentence);
        Check(lines.Upper is null && lines.Lower is null, "stop discards pending correction");
        Check(CaptionAssembly.ReusablePrefix(repeatedClauses + "Left.", repeatedClauses + "Right.") <= CaptionAssembly.MaximumComparisonCharacters,
            "assembly comparison remains bounded");
        Console.WriteLine($"Caption lines: {count} assertions PASS (production class, virtual clock; no screen claim)");
    }
}
