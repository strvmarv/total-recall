using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// Builds the two FTS5 <c>MATCH</c> strings of the archive's word search (search the archive, spec section 5) from free
/// text. Every token is double-quoted and only letters and digits survive tokenisation, so no operator, column filter,
/// prefix star or unbalanced quote of the user's text can reach the FTS5 parser. Unlike <c>FtsSearch</c> it joins with
/// <c>OR</c>, not an implicit AND (an AND over a whole sentence finds nothing).
/// </summary>
public static class TranscriptQueryBuilder
{
    /// <summary>At most this many tokens go into the words query.</summary>
    public const int MaxWordTokens = 12;

    /// <summary>
    /// The whole token sequence as one quoted FTS5 phrase, or null when the query has fewer than two tokens. (Punctuation
    /// separates tokens in the query and in the indexed text alike, so <c>SQLITE_BUSY: database is locked</c> becomes
    /// <c>"sqlite busy database is locked"</c>.)
    /// </summary>
    public static string? Phrase(string query)
    {
        var tokens = Tokenize(query);
        return tokens.Count < 2 ? null : Quote(string.Join(' ', tokens));
    }

    /// <summary>
    /// Distinct tokens of two or more characters, the longest <see cref="MaxWordTokens"/> (ties by first position), in
    /// first-position order, each quoted and joined by <c> OR </c>; null when there is none.
    /// </summary>
    public static string? Words(string query)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<(string Token, int Position, int Length)>();
        foreach (var token in Tokenize(query))
        {
            var length = RuneLength(token);
            if (length < 2 || !seen.Add(token))
                continue;
            candidates.Add((token, candidates.Count, length));
        }

        if (candidates.Count == 0)
            return null;

        return string.Join(" OR ", candidates
            .OrderByDescending(c => c.Length).ThenBy(c => c.Position)
            .Take(MaxWordTokens)
            .OrderBy(c => c.Position)
            .Select(c => Quote(c.Token)));
    }

    /// <summary>
    /// Lower-cased runs of Unicode letters and digits, after NFC normalisation: a decomposed letter (<c>e</c> plus a
    /// combining accent, which is not a letter) would otherwise split a word in two and miss the indexed form.
    /// </summary>
    private static List<string> Tokenize(string query)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(query))
            return tokens;

        string normalised;
        try
        {
            normalised = query.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            normalised = query; // invalid UTF-16 (a lone surrogate): tokenised as it is, the bad unit becomes U+FFFD
        }

        var current = new StringBuilder();
        foreach (var rune in normalised.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                current.Append(Rune.ToLowerInvariant(rune).ToString());
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static int RuneLength(string s)
    {
        var n = 0;
        foreach (var _ in s.EnumerateRunes()) n++;
        return n;
    }

    private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
