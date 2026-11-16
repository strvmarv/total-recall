// src/TotalRecall.Infrastructure/WhatsNew/SummarySanitizer.cs
//
// Reduces untrusted release text to one line of plain text of at most 200 UTF-16 units.
// Mirrors sanitizeSummary in scripts/release-notes.js; both are tested against
// scripts/fixtures/summary-sanitiser.json. Pure, no I/O, no reflection.
//
// Order: (1) ANSI sequences, (2) control characters other than whitespace, then the input cap
// (first 4,000 units), (3) markdown links flattened and <http(s)://...> unbracketed until the text
// stops changing, (4) whitespace runs become one space, (5) trim spaces, (6) cut at the last space
// at or before 200 units (one longer word is cut at 200; never half of a surrogate pair).
//
// Pass 3 is hand-scanned, not a backtracking regex: on nested hostile input the regex form grows
// about cubically. Each scan is linear and is equivalent to the regex it replaces
// (\[([^\]]*)\]\([^)]*\) and <(https?://[^<>WS]*)>, scheme ASCII-only and case-insensitive).
// The loop runs at most one pass per level of nested matches, plus a last one that changes nothing, so the
// work on hostile input is bounded by counting passes (Sanitize(text, out passes)), not by time; see the
// spec, section 2.

using System.Text;

namespace TotalRecall.Infrastructure.WhatsNew;

public static class SummarySanitizer
{
    public const int MaxInputChars = 4000;
    public const int MaxSummary = 200;

    public static string Sanitize(string? text) => Sanitize(text, out _);

    // The same, also reporting how many iterations the pass-3 loop ran (the last one, which changes nothing,
    // included; 0 when the input is null or empty). The tests bound that count instead of the time it takes.
    internal static string Sanitize(string? text, out int passes)
    {
        passes = 0;
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var s = RemoveControl(RemoveAnsi(text));

        if (s.Length > MaxInputChars)
        {
            // Never end the kept prefix on the first half of a surrogate pair.
            var keep = char.IsHighSurrogate(s[MaxInputChars - 1]) ? MaxInputChars - 1 : MaxInputChars;
            s = s.Substring(0, keep);
        }

        // Each pass that changes the text shortens it, so the loop ends.
        while (true)
        {
            passes++;
            var before = s;
            s = FlattenLinks(s);
            s = UnbracketUrls(s);
            if (ReferenceEquals(before, s)) break;
        }

        s = CollapseWhitespace(s);

        if (s.Length > MaxSummary)
        {
            var cut = s.LastIndexOf(' ', MaxSummary);
            if (cut > 0)
            {
                s = s.Substring(0, cut);
            }
            else
            {
                var splitsPair = char.IsHighSurrogate(s[MaxSummary - 1]) && char.IsLowSurrogate(s[MaxSummary]);
                s = s.Substring(0, splitsPair ? MaxSummary - 1 : MaxSummary);
            }
        }
        return s;
    }

    // The set of char.IsWhiteSpace, spelled out. U+FEFF is not whitespace.
    private static bool IsWhitespace(char c) =>
        c == ' ' || (c >= '\t' && c <= '\r') || c == '\u0085' || c == '\u00a0' || c == '\u1680'
        || (c >= '\u2000' && c <= '\u200a') || c == '\u2028' || c == '\u2029' || c == '\u202f'
        || c == '\u205f' || c == '\u3000';

    // Control characters other than the whitespace ones.
    private static bool IsRemovableControl(char c) =>
        c <= '\u0008' || (c >= '\u000e' && c <= '\u001f') || (c >= '\u007f' && c <= '\u0084')
        || (c >= '\u0086' && c <= '\u009f');

    // ESC [ parameter bytes (0x30-0x3F), intermediate bytes (0x20-0x2F), final byte (0x40-0x7E).
    private static string RemoveAnsi(string s)
    {
        if (s.IndexOf('\u001b') < 0) return s;
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\u001b' && i + 1 < s.Length && s[i + 1] == '[')
            {
                var p = i + 2;
                while (p < s.Length && s[p] >= '0' && s[p] <= '?') p++;
                while (p < s.Length && s[p] >= ' ' && s[p] <= '/') p++;
                if (p < s.Length && s[p] >= '@' && s[p] <= '~')
                {
                    i = p + 1;
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static string RemoveControl(string s)
    {
        var first = -1;
        for (var i = 0; i < s.Length; i++)
        {
            if (IsRemovableControl(s[i])) { first = i; break; }
        }
        if (first < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (!IsRemovableControl(s[i])) sb.Append(s[i]);
        }
        return sb.ToString();
    }

    // [text](url) -> text, the url ending at its first ')'. A '[' whose first ']' is not followed by
    // '(' fails, and so does every '[' before that ']': scanning continues after it. With no ')' after
    // a "](" no later link can match either, so the scan ends. Returns the same instance when unchanged.
    private static string FlattenLinks(string s)
    {
        StringBuilder? sb = null;
        var copied = 0;
        var i = 0;
        while (i < s.Length)
        {
            var open = s.IndexOf('[', i);
            if (open < 0) break;
            var close = s.IndexOf(']', open + 1);
            if (close < 0) break;
            if (close + 1 < s.Length && s[close + 1] == '(')
            {
                var end = s.IndexOf(')', close + 2);
                if (end < 0) break;
                sb ??= new StringBuilder(s.Length);
                sb.Append(s, copied, open - copied);
                sb.Append(s, open + 1, close - open - 1);
                copied = end + 1;
                i = copied;
            }
            else
            {
                i = close + 1;
            }
        }
        if (sb is null) return s;
        sb.Append(s, copied, s.Length - copied);
        return sb.ToString();
    }

    // <http(s)://run> -> http(s)://run where run holds no '<', '>' or whitespace. On failure the scan
    // continues after that '<'. Returns the same instance when unchanged.
    private static string UnbracketUrls(string s)
    {
        StringBuilder? sb = null;
        var copied = 0;
        var i = 0;
        while (i < s.Length)
        {
            var open = s.IndexOf('<', i);
            if (open < 0) break;
            var end = MatchAngleUrl(s, open);
            if (end < 0)
            {
                i = open + 1;
                continue;
            }
            sb ??= new StringBuilder(s.Length);
            sb.Append(s, copied, open - copied);
            sb.Append(s, open + 1, end - open - 1);
            copied = end + 1;
            i = copied;
        }
        if (sb is null) return s;
        sb.Append(s, copied, s.Length - copied);
        return sb.ToString();
    }

    // Index of the closing '>' for an angle-bracketed http(s) URL opening at s[open], or -1.
    private static int MatchAngleUrl(string s, int open)
    {
        var p = open + 1;
        if (p + 4 > s.Length
            || !IsAsciiLetter(s[p], 'h') || !IsAsciiLetter(s[p + 1], 't')
            || !IsAsciiLetter(s[p + 2], 't') || !IsAsciiLetter(s[p + 3], 'p'))
            return -1;
        p += 4;
        if (p < s.Length && IsAsciiLetter(s[p], 's')) p++;
        if (p + 3 > s.Length || s[p] != ':' || s[p + 1] != '/' || s[p + 2] != '/') return -1;
        p += 3;
        while (p < s.Length && s[p] != '<' && s[p] != '>' && !IsWhitespace(s[p])) p++;
        return p < s.Length && s[p] == '>' ? p : -1;
    }

    // ASCII-only, case-insensitive: U+017F and the Kelvin sign do not fold to 's' and 'k'.
    private static bool IsAsciiLetter(char c, char lower) => c == lower || c == lower - 32;

    // Every whitespace run becomes one space; leading and trailing spaces are dropped.
    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var c in s)
        {
            if (IsWhitespace(c))
            {
                pendingSpace = sb.Length > 0;
            }
            else
            {
                if (pendingSpace) sb.Append(' ');
                pendingSpace = false;
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
