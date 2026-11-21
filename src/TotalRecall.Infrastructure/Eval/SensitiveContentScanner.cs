// src/TotalRecall.Infrastructure/Eval/SensitiveContentScanner.cs
//
// Guards the eval_grow accept path. Candidates are captured from REAL
// retrieval misses, so their query text and surfaced top-result content can
// carry secrets, PII, or caller-internal identifiers. Before such a candidate
// is appended to the (public, committed) benchmark corpus, it is scanned here.
//
// Detection has two layers:
//   1. Hardcoded generic detectors (emails, common secret/token shapes,
//      private-key blocks) — safe to ship in a public repo because they name
//      shapes, not any specific organization's data.
//   2. A caller-supplied list of internal terms (config: eval.grow.sensitive_terms,
//      default empty). These deliberately live in the user's LOCAL config, never
//      hardcoded here — baking internal product names into the public source
//      would itself be a leak.
//
// Reasons are category labels, never the matched value, so scanning a secret
// does not echo it back into logs or tool responses.
//
// A second consumer (git sync, later milestones) calls ScanSecrets, which runs
// only the token and private-key detectors; the detectors are selectable via
// the Detectors flags and Scan(text, terms) still runs all of them.
//
// A third consumer (the transcript archive) calls Redact, which replaces the
// secrets it finds with [redacted:<category>] markers. Redact has its own
// pattern set (it extends the sk- shape and adds bearer, URL-credential and
// secret-assignment detectors); Scan and ScanSecrets do not use it.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace TotalRecall.Infrastructure.Eval;

/// <summary>The built-in detectors a scan may run (internal terms are separate: they come from the caller).</summary>
[Flags]
public enum Detectors
{
    None = 0,
    Email = 1,
    Token = 2,
    PrivateKey = 4,
    All = Email | Token | PrivateKey,
}

/// <summary>
/// The outcome of <see cref="SensitiveContentScanner.Redact(string?)"/>: the text with every detected secret replaced by
/// a marker, the number of markers written, and the distinct categories in the order of their first marker.
/// </summary>
public sealed record RedactionResult(string Text, int Count, IReadOnlyList<string> Categories);

/// <summary>
/// Detects secrets / PII / configured internal terms in text destined for the
/// public benchmark corpus. Returns category reasons (never the matched value).
/// </summary>
public static class SensitiveContentScanner
{
    // Interpreted (not Compiled) regex: RegexOptions.Compiled emits IL at
    // runtime and is not NativeAOT-safe. These run only on eval_grow accept,
    // over short strings, so interpreted matching is more than fast enough.
    private static readonly (Detectors Flag, string Reason, Regex Pattern)[] _detectors =
    {
        (Detectors.Email, "email address",
            new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")),
        (Detectors.Token, "API token or secret",
            new Regex(@"gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_\-]{16,}|xox[baprs]-[A-Za-z0-9\-]{10,}|sk-[A-Za-z0-9]{16,}|AKIA[0-9A-Z]{16}")),
        (Detectors.PrivateKey, "private key block",
            new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
    };

    /// <summary>
    /// Scan <paramref name="text"/> for secrets, PII, and any of
    /// <paramref name="internalTerms"/> (case-insensitive substring, blanks
    /// ignored). Returns an empty list when clean.
    /// </summary>
    public static IReadOnlyList<string> Scan(string? text, IReadOnlyCollection<string>? internalTerms) =>
        Scan(text, internalTerms, Detectors.All);

    /// <summary>
    /// The same scan with only the chosen built-in <paramref name="detectors"/> (the internal terms always run).
    /// Reasons come in the order email, token, private key, internal term.
    /// </summary>
    public static IReadOnlyList<string> Scan(string? text, IReadOnlyCollection<string>? internalTerms, Detectors detectors)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();

        var reasons = new List<string>();
        foreach (var (flag, reason, pattern) in _detectors)
        {
            if ((detectors & flag) != 0 && pattern.IsMatch(text)) reasons.Add(reason);
        }

        if (internalTerms is not null)
        {
            foreach (var raw in internalTerms)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (text.Contains(raw.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    // Category label only — never echo the matched term, in case
                    // an operator put a real secret in sensitive_terms. One reason
                    // is enough regardless of how many terms matched.
                    reasons.Add("internal term");
                    break;
                }
            }
        }

        return reasons;
    }

    /// <summary>
    /// Tokens and private keys only, with no internal terms: the check git sync runs on text it is about to publish to
    /// the user's own private repository. Email addresses are not flagged (a memory may name a person).
    /// </summary>
    public static IReadOnlyList<string> ScanSecrets(string? text) =>
        Scan(text, null, Detectors.Token | Detectors.PrivateKey);

    // ------------------------------------------------------------------------------------------------------------
    // Redaction (transcript archive, spec section 4)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Written to every archived chunk; bump it when the detectors change what is redacted.</summary>
    public const int RedactionVersion = 1;

    private static readonly TimeSpan RedactMatchTimeout = TimeSpan.FromSeconds(5);
    private static readonly RedactionPatterns _redaction = new(RedactMatchTimeout);

    // Categories in table order: on an equal start the lower value names the merged marker.
    private const int CatToken = 0;
    private const int CatPrivateKey = 1;
    private const int CatBearer = 2;
    private const int CatUrlCredentials = 3;
    private const int CatSecretAssignment = 4;

    private static readonly string[] CategoryNames =
        { "token", "private-key", "bearer", "url-credentials", "secret-assignment" };

    private const string UnprocessableCategory = "unprocessable";

    /// <summary>
    /// Replace every detected secret in <paramref name="text"/> with <c>[redacted:&lt;category&gt;]</c>. Null or empty
    /// text comes back as the empty string with count 0 (<see cref="RedactionResult.Text"/> is never null). A detector that runs into its 5-second match timeout
    /// fails closed: the whole text becomes <c>[redacted:unprocessable]</c>.
    /// </summary>
    public static RedactionResult Redact(string? text) => Redact(text, _redaction);

    /// <summary>Test seam: the same redaction with a chosen regex match timeout.</summary>
    internal static RedactionResult Redact(string? text, TimeSpan matchTimeout) =>
        Redact(text, matchTimeout == RedactMatchTimeout ? _redaction : new RedactionPatterns(matchTimeout));

    private static RedactionResult Redact(string? text, RedactionPatterns patterns)
    {
        if (string.IsNullOrEmpty(text)) return new RedactionResult("", 0, Array.Empty<string>());

        var spans = new List<Span>();
        try
        {
            FindTokens(text, patterns, spans);
            FindPrivateKeys(text, patterns, spans);
            FindAuthHeaders(text, patterns.Authorization, hasScheme: true, spans);
            FindAuthHeaders(text, patterns.ApiKeyHeader, hasScheme: false, spans);
            FindBareBearers(text, patterns, spans);
            FindUrlCredentials(text, spans);
            foreach (var pattern in patterns.SecretAssignments) FindSecretAssignments(text, pattern, spans);
        }
        catch (RegexMatchTimeoutException)
        {
            return new RedactionResult("[redacted:" + UnprocessableCategory + "]", 1, new[] { UnprocessableCategory });
        }

        if (spans.Count == 0) return new RedactionResult(text, 0, Array.Empty<string>());
        return Rebuild(text, spans);
    }

    private readonly record struct Span(int Start, int End, int Category);

    private enum QuoteKind { None, Double, Single, EscapedDouble, EscapedSingle }

    private readonly record struct Value(int Start, int End, QuoteKind Quote)
    {
        public int Length => End - Start;
    }

    /// <summary>
    /// Every redaction regex, built with one match timeout. Interpreted only (NativeAOT; the non-backtracking engine
    /// has no lookbehind). Each repeated element is bounded, or unambiguous because nothing after it can match what it
    /// matches; values are read by hand (linear scans) once a pattern has found where one starts. Each regex starts
    /// with one literal (after an optional lookbehind) so the engine can skip to it with a vectorised search instead
    /// of trying every position that starts some branch of a wide alternation.
    /// </summary>
    private sealed class RedactionPatterns
    {
        private const RegexOptions Cs = RegexOptions.CultureInvariant;
        private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // After a secret name: an optional closing quote (escaped or not) and ], blanks, an optionally escaped
        // separator (:=, =>, = to ===, or a single :), blanks. The blank runs are unbounded but unambiguous: nothing
        // that follows them can be a blank, so aligned code (terraform fmt, gofmt) with any number of blanks matches.
        private const string NameTail = @"(?![A-Za-z0-9_])(?:\\?[""'])?\]?[ \t]*\\?(?::=|=>|={1,3}|:(?!:))[ \t]*";

        // After an Authorization or x-api-key header name: either a closing quote (escaped or not), an optional ] and
        // a separator that may also be a comma (Header.Set("Authorization", "...")), or an optional ] and a separator;
        // the separator is =>, or an optionally escaped : or =. Blank runs as in NameTail.
        private const string HeaderTail =
            @"(?:\\?[""']\]?[ \t]*(?:=>|,|\\?[:=])|\]?[ \t]*(?:=>|\\?[:=]))[ \t]*";

        public RedactionPatterns(TimeSpan timeout)
        {
            // The existing token shapes, plus sk- keys with a left boundary: the hyphenated sk-ant-, sk-proj-,
            // sk-svcacct-, sk-admin- forms with at least 20 characters after the prefix, any other sk- followed by 20 or
            // more of [A-Za-z0-9_-] (sk-None-, sk-or-v1-, ...; the named prefixes keep their own 20-character rule),
            // and the classic sk-<16+ alphanumerics>.
            Tokens = new[]
            {
                new Regex(@"gh[pousr]_[A-Za-z0-9]{16,}", Cs, timeout),
                new Regex(@"github_pat_[A-Za-z0-9_]{20,}", Cs, timeout),
                new Regex(@"glpat-[A-Za-z0-9_\-]{16,}", Cs, timeout),
                new Regex(@"xox[baprs]-[A-Za-z0-9\-]{10,}", Cs, timeout),
                new Regex(@"AKIA[0-9A-Z]{16}", Cs, timeout),
                new Regex(@"(?<![A-Za-z0-9])sk-(?:(?:ant|proj|svcacct|admin)-[A-Za-z0-9_\-]{20,}|(?!(?:ant|proj|svcacct|admin)-)[A-Za-z0-9_\-]{20,}|[A-Za-z0-9]{16,})", Cs, timeout),
            };

            // The BEGIN line; the END line with the same label is found by an ordinal search.
            PrivateKeyBegin = new Regex(@"-----BEGIN (?<label>[A-Z ]{0,40})PRIVATE KEY(?<block> BLOCK)?-----", Cs, timeout);

            // An Authorization or x-api-key header name and its separator (see HeaderTail). The optional opening
            // quote, scheme and value are read by hand.
            Authorization = new Regex(@"(?<![A-Za-z0-9])authorization" + HeaderTail, Ci, timeout);
            ApiKeyHeader = new Regex(@"(?<![A-Za-z0-9])x-api-key" + HeaderTail, Ci, timeout);

            // A bare Bearer and the blanks after it (a trailing run: nothing follows it in the pattern).
            BareBearer = new Regex(@"(?<![A-Za-z0-9])bearer[ \t]+", Ci, timeout);

            // A secret-named variable and its separator. A name ending in _KEY/_TOKEN/_SECRET/_PASSWORD/_PASSWD is
            // found by its suffix only, so the name's length is unlimited and the match is unambiguous; the bare names
            // need a left boundary (API_KEY, CLIENT_SECRET, ACCESS_KEY and AUTH_TOKEN end in a listed suffix). Then
            // the command-line flags, followed by = or blanks.
            SecretAssignments = new[]
            {
                new Regex(@"_key" + NameTail, Ci, timeout),
                new Regex(@"_token" + NameTail, Ci, timeout),
                new Regex(@"_secret" + NameTail, Ci, timeout),
                new Regex(@"_passw(?:or)?d" + NameTail, Ci, timeout),
                new Regex(@"(?<![A-Za-z0-9_])passw(?:or)?d" + NameTail, Ci, timeout),
                new Regex(@"(?<![A-Za-z0-9_])secret" + NameTail, Ci, timeout),
                new Regex(@"(?<![A-Za-z0-9_])token" + NameTail, Ci, timeout),
                new Regex(@"(?<![A-Za-z0-9_])apikey" + NameTail, Ci, timeout),
                new Regex(@"(?<![A-Za-z0-9_])passphrase" + NameTail, Ci, timeout),
                new Regex(@"(?<![A-Za-z0-9_\-])--(?:password|passwd|token|api-key|secret|auth-token)(?:=|[ \t]+)", Ci, timeout),
            };
        }

        public Regex[] Tokens { get; }
        public Regex PrivateKeyBegin { get; }
        public Regex Authorization { get; }
        public Regex ApiKeyHeader { get; }
        public Regex BareBearer { get; }
        public Regex[] SecretAssignments { get; }
    }

    private static void FindTokens(string text, RedactionPatterns patterns, List<Span> spans)
    {
        foreach (var pattern in patterns.Tokens)
        {
            for (var m = pattern.Match(text); m.Success; m = m.NextMatch())
                spans.Add(new Span(m.Index, m.Index + m.Length, CatToken));
        }
    }

    private static void FindPrivateKeys(string text, RedactionPatterns patterns, List<Span> spans)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var m = patterns.PrivateKeyBegin.Match(text, pos);
            if (!m.Success) return;

            var endLine = "-----END " + m.Groups["label"].Value + "PRIVATE KEY" + m.Groups["block"].Value + "-----";
            var afterBegin = m.Index + m.Length;
            var endAt = text.IndexOf(endLine, afterBegin, StringComparison.Ordinal);
            var end = endAt < 0 ? text.Length : endAt + endLine.Length;
            spans.Add(new Span(m.Index, end, CatPrivateKey));
            pos = end;
        }
    }

    private static void FindAuthHeaders(string text, Regex pattern, bool hasScheme, List<Span> spans)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var m = pattern.Match(text, pos);
            if (!m.Success) return;
            pos = m.Index + m.Length;

            var p = pos;
            var quote = OpenQuote(text, ref p);
            if (hasScheme)
            {
                var wordStart = p;
                p = SkipScheme(text, p, out var listed, out var wordEnd);
                // An unlisted word is kept as a scheme only while it is too short to be a credential itself. A longer
                // one may be a scheme-less credential followed by more words: inside quotes the whole content is then
                // the value; unquoted, the word is redacted on its own as well as the run after it. A shorter unlisted
                // word inside quotes is kept as a scheme only while the rest is long enough to be judged on its own
                // ("Key abc123" is judged whole).
                var word = new Value(wordStart, wordEnd, QuoteKind.None);
                if (!listed && p > wordStart && word.Length >= 8)
                {
                    if (quote != QuoteKind.None) p = wordStart;
                    else if (!IsExempt(text, word)) spans.Add(new Span(word.Start, word.End, CatBearer));
                }
                else if (!listed && p > wordStart && quote != QuoteKind.None && CloseQuote(text, p, quote) - p < 8)
                {
                    p = wordStart;
                }
            }

            Value value;
            if (quote != QuoteKind.None) value = new Value(p, CloseQuote(text, p, quote), quote);
            else if (!ReadValue(text, p, stopAtQuotes: true, out value)) continue;

            if (value.Length >= 8 && !IsExempt(text, value))
            {
                spans.Add(new Span(value.Start, value.End, CatBearer));
                pos = Math.Max(pos, value.End);
            }
        }
    }

    private static void FindBareBearers(string text, RedactionPatterns patterns, List<Span> spans)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var m = patterns.BareBearer.Match(text, pos);
            if (!m.Success) return;
            pos = m.Index + m.Length;

            if (ReadValue(text, pos, stopAtQuotes: true, out var value) && value.Length >= 16 && !IsExempt(text, value))
            {
                spans.Add(new Span(value.Start, value.End, CatBearer));
                pos = Math.Max(pos, value.End);
            }
        }
    }

    // scheme://user:password@host. The user information runs from after :// to the last @ before the next whitespace,
    // provided the text before that @ holds a ':'; the password lies between the first ':' and that @.
    private static void FindUrlCredentials(string text, List<Span> spans)
    {
        var i = 0;
        while ((i = text.IndexOf("://", i, StringComparison.Ordinal)) >= 0)
        {
            var segStart = i + 3;
            if (!HasScheme(text, i))
            {
                i = segStart;
                continue;
            }

            var segEnd = segStart;
            while (segEnd < text.Length && !char.IsWhiteSpace(text[segEnd])) segEnd++;

            var next = segEnd;
            var at = segEnd > segStart ? text.LastIndexOf('@', segEnd - 1, segEnd - segStart) : -1;
            if (at > segStart)
            {
                var colon = text.IndexOf(':', segStart, at - segStart);
                if (colon >= 0 && colon + 1 < at)
                {
                    var value = new Value(colon + 1, at, QuoteKind.None);
                    if (IsExempt(text, value)) next = segStart; // look inside an exempt password too
                    else spans.Add(new Span(value.Start, value.End, CatUrlCredentials));
                }
            }

            // Skipping the segment is safe: a later :// inside it shares the same last @, and its password lies inside
            // the one just found (or it has none).
            i = next;
        }
    }

    private static bool HasScheme(string text, int colonSlashSlash)
    {
        var sawLetter = false;
        for (var k = colonSlashSlash - 1; k >= 0 && colonSlashSlash - k <= 32; k--)
        {
            var c = text[k];
            if (IsAsciiLetter(c)) sawLetter = true;
            else if (!(IsAsciiDigit(c) || c == '+' || c == '.' || c == '-')) break;
        }

        return sawLetter;
    }

    private static void FindSecretAssignments(string text, Regex pattern, List<Span> spans)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var m = pattern.Match(text, pos);
            if (!m.Success) return;
            pos = m.Index + m.Length;

            if (ReadValue(text, pos, stopAtQuotes: false, out var value) && value.Length > 0 && !IsExempt(text, value))
            {
                spans.Add(new Span(value.Start, value.End, CatSecretAssignment));
                pos = Math.Max(pos, value.End);
            }
        }
    }

    // ---- value grammar ----

    /// <summary>
    /// Read the value that starts at <paramref name="pos"/>. A quoted value (", ', \" or \') is its content up to the
    /// closing quote, or to the end of the line when there is none. Otherwise a $(...) substitution (balanced
    /// parentheses), a backtick substitution or a {{ ... }} placeholder is one piece, and the value is that piece plus
    /// the rest of the run of non-whitespace characters (for bearer values the run also stops at a quote or a
    /// backslash). No value when the text ends or a blank follows.
    /// </summary>
    private static bool ReadValue(string text, int pos, bool stopAtQuotes, out Value value)
    {
        value = default;
        if (pos >= text.Length) return false;

        var p = pos;
        var quote = OpenQuote(text, ref p);
        if (quote != QuoteKind.None)
        {
            value = new Value(p, CloseQuote(text, p, quote), quote);
            return true;
        }

        var c = text[pos];
        if (char.IsWhiteSpace(c)) return false;

        var i = pos;
        if (c == '$' && pos + 1 < text.Length && text[pos + 1] == '(') i = SkipBalanced(text, pos + 1);
        else if (c == '`') i = SkipBacktick(text, pos);
        else if (c == '{') i = SkipPlaceholder(text, pos);

        while (i < text.Length && !char.IsWhiteSpace(text[i]) && !(stopAtQuotes && IsQuoteStop(text[i]))) i++;
        if (i == pos) return false;
        value = new Value(pos, i, QuoteKind.None);
        return true;
    }

    private static bool IsQuoteStop(char c) => c is '"' or '\'' or '`' or '\\';

    private static QuoteKind OpenQuote(string text, ref int p)
    {
        if (p >= text.Length) return QuoteKind.None;
        var c = text[p];
        if (c == '\\' && p + 1 < text.Length && text[p + 1] is '"' or '\'')
        {
            var kind = text[p + 1] == '"' ? QuoteKind.EscapedDouble : QuoteKind.EscapedSingle;
            p += 2;
            return kind;
        }

        if (c == '"')
        {
            p++;
            return QuoteKind.Double;
        }

        if (c == '\'')
        {
            p++;
            return QuoteKind.Single;
        }

        return QuoteKind.None;
    }

    /// <summary>Where the quoted content that starts at <paramref name="start"/> ends: at its closing quote, else at the end of the line.</summary>
    /// <remarks>
    /// The escaped-JSON closer <c>\"</c> closes only when the backslashes before the quote decode (one layer) to an even
    /// number, i.e. their count is 1, 5, 9, ...: <c>\\\"</c> (three) is an escaped quote of the inner layer inside the
    /// value, not the closer.
    /// </remarks>
    private static int CloseQuote(string text, int start, QuoteKind quote)
    {
        var backslashes = 0;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r') return i;
            switch (quote)
            {
                case QuoteKind.Double when c == '"' && text[i - 1] != '\\':
                case QuoteKind.Single when c == '\'':
                    return i;
                case QuoteKind.EscapedDouble when c == '"' && backslashes % 4 == 1:
                    return i - 1;
                case QuoteKind.EscapedSingle when c == '\\' && i + 1 < text.Length && text[i + 1] == '\'':
                    return i;
            }

            backslashes = c == '\\' ? backslashes + 1 : 0;
        }

        return text.Length;
    }

    // From the '(' of "$(": the index after the matching ')', else the end of the line.
    private static int SkipBalanced(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r') return i;
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i + 1;
        }

        return text.Length;
    }

    // From an opening backtick: the index after the closing one, else the end of the line.
    private static int SkipBacktick(string text, int open)
    {
        for (var i = open + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r') return i;
            if (c == '`') return i + 1;
        }

        return text.Length;
    }

    // {{[^}\n]{0,200}}} at pos: the index after it, else pos (the value is then an ordinary run).
    private static int SkipPlaceholder(string text, int pos)
    {
        if (pos + 1 >= text.Length || text[pos + 1] != '{') return pos;
        var i = pos + 2;
        var limit = Math.Min(text.Length, pos + 2 + 200);
        while (i < limit && text[i] != '}' && text[i] != '\n') i++;
        return i + 1 < text.Length && text[i] == '}' && text[i + 1] == '}' ? i + 2 : pos;
    }

    /// <summary>
    /// Only a reference or a placeholder is left alone, and only when it is the whole value: $NAME, ${NAME} or %NAME%
    /// when the value is unquoted or double-quoted, &lt;word-ish&gt;, {{ ... }}, true/false/null/none/undefined, or
    /// empty. No exemption by length or first character.
    /// </summary>
    private static bool IsExempt(string text, Value value)
    {
        var s = text.AsSpan(value.Start, value.Length);
        if (IsExemptSpan(s, value.Quote)) return true;

        // An unquoted run takes the punctuation that closes the surrounding structure ("token": null, or
        // {"api_key": true}); judge the value without it. The redacted span still covers the whole run. The value is
        // tried with up to four trailing characters dropped one at a time (so ${NAME}; keeps its own brace) and with
        // all of them dropped; a bounded number of checks keeps this linear.
        if (value.Quote != QuoteKind.None) return false;
        var cut = s;
        for (var k = 0; k < 4 && cut.Length > 0 && IsClosingPunctuation(cut[^1]); k++)
        {
            cut = cut[..^1];
            if (IsExemptSpan(cut, value.Quote)) return true;
        }

        var trimmed = s.TrimEnd(",;})]");
        return trimmed.Length < cut.Length && IsExemptSpan(trimmed, value.Quote);
    }

    private static bool IsClosingPunctuation(char c) => c is ',' or ';' or '}' or ')' or ']';

    private static bool IsExemptSpan(ReadOnlySpan<char> s, QuoteKind quote)
    {
        if (s.IsEmpty) return true;
        if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase)
            || s.Equals("null", StringComparison.OrdinalIgnoreCase) || s.Equals("none", StringComparison.OrdinalIgnoreCase)
            || s.Equals("undefined", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IsAnglePlaceholder(s) || IsBracePlaceholder(s)) return true;
        return quote is QuoteKind.None or QuoteKind.Double or QuoteKind.EscapedDouble && IsReference(s);
    }

    private static bool IsReference(ReadOnlySpan<char> s)
    {
        if (s.Length >= 2 && s[0] == '$' && s[1] != '{') return IsName(s[1..]);
        if (s.Length >= 4 && s[0] == '$' && s[1] == '{' && s[^1] == '}') return IsName(s[2..^1]);
        if (s.Length >= 3 && s[0] == '%' && s[^1] == '%') return IsName(s[1..^1]);
        return false;
    }

    private static bool IsName(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty || !(IsAsciiLetter(s[0]) || s[0] == '_')) return false;
        foreach (var c in s)
        {
            if (!(IsAsciiLetter(c) || IsAsciiDigit(c) || c == '_')) return false;
        }

        return true;
    }

    private static bool IsAnglePlaceholder(ReadOnlySpan<char> s)
    {
        if (s.Length < 3 || s.Length > 102 || s[0] != '<' || s[^1] != '>') return false;
        var inner = s[1..^1];
        if (!(IsAsciiLetter(inner[0]) || IsAsciiDigit(inner[0]) || inner[0] == '_')) return false;
        foreach (var c in inner)
        {
            if (!(IsAsciiLetter(c) || IsAsciiDigit(c) || c is '_' or '-' or '.' or ' ')) return false;
        }

        return true;
    }

    private static bool IsBracePlaceholder(ReadOnlySpan<char> s)
    {
        if (s.Length < 4 || s.Length > 204 || !s.StartsWith("{{") || !s.EndsWith("}}")) return false;
        return s[2..^2].IndexOfAny('}', '\n') < 0;
    }

    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    /// <summary>
    /// An Authorization scheme at <paramref name="p"/>: one word <c>[A-Za-z][A-Za-z0-9-]{0,30}</c> followed by blanks
    /// and then a non-blank character. Returns the index after the blanks (where the credential starts), else
    /// <paramref name="p"/>. <paramref name="listed"/> tells whether the word is Bearer, Basic, Token or Digest, and
    /// <paramref name="wordEnd"/> is the index after the word (<paramref name="p"/> when there is no scheme).
    /// </summary>
    private static int SkipScheme(string text, int p, out bool listed, out int wordEnd)
    {
        listed = false;
        wordEnd = p;
        if (p >= text.Length || !IsAsciiLetter(text[p])) return p;

        var end = p + 1;
        while (end < text.Length && end - p <= 31 && (IsAsciiLetter(text[end]) || IsAsciiDigit(text[end]) || text[end] == '-'))
            end++;
        if (end - p > 31 || end >= text.Length || text[end] is not (' ' or '\t')) return p;

        var after = end;
        while (after < text.Length && text[after] is ' ' or '\t') after++;
        if (after >= text.Length || char.IsWhiteSpace(text[after])) return p;

        var word = text.AsSpan(p, end - p);
        foreach (var scheme in AuthSchemes)
        {
            if (word.Equals(scheme, StringComparison.OrdinalIgnoreCase)) listed = true;
        }

        wordEnd = end;
        return after;
    }

    private static readonly string[] AuthSchemes = { "Bearer", "Basic", "Token", "Digest" };

    // ---- merge and rebuild ----

    private static RedactionResult Rebuild(string text, List<Span> spans)
    {
        spans.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Category.CompareTo(b.Category));

        var sb = new StringBuilder(text.Length);
        var categories = new List<string>();
        var count = 0;
        var pos = 0;
        var i = 0;
        while (i < spans.Count)
        {
            var first = spans[i];
            var end = first.End;
            var j = i + 1;
            while (j < spans.Count && spans[j].Start < end)
            {
                end = Math.Max(end, spans[j].End);
                j++;
            }

            var name = CategoryNames[first.Category];
            sb.Append(text, pos, first.Start - pos).Append("[redacted:").Append(name).Append(']');
            if (!categories.Contains(name)) categories.Add(name);
            count++;
            pos = end;
            i = j;
        }

        sb.Append(text, pos, text.Length - pos);
        return new RedactionResult(sb.ToString(), count, categories);
    }
}
