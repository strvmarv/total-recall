// src/TotalRecall.Infrastructure/Memory/AutoTagger.cs
//
// Phase 1 Step 3 — keyword extraction and compact tag formatting for
// progressive summarization. Extracts keywords via TF-IDF-like frequency
// scoring with English stop-word removal. Used by BuildContext compact mode.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Memory;

public static class AutoTagger
{
    // ~150 English stop words. Hardcoded for AOT safety — no file reads, no
    // dependencies. Based on NLTK's English stop word list (most common).
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "above", "after", "again", "against", "all", "am", "an",
        "and", "any", "are", "aren", "as", "at", "be", "because", "been",
        "before", "being", "below", "between", "both", "but", "by", "can",
        "cannot", "could", "couldn", "did", "didn", "do", "does", "doesn",
        "doing", "don", "down", "during", "each", "few", "for", "from",
        "further", "had", "hadn", "has", "hasn", "have", "haven", "having",
        "he", "her", "here", "hers", "herself", "him", "himself", "his",
        "how", "i", "if", "in", "into", "is", "isn", "it", "its", "itself",
        "just", "ll", "m", "ma", "me", "mightn", "more", "most", "mustn",
        "my", "myself", "needn", "no", "nor", "not", "now", "o", "of", "off",
        "on", "once", "only", "or", "other", "our", "ours", "ourselves",
        "out", "over", "own", "re", "s", "same", "shan", "she", "should",
        "shouldn", "so", "some", "such", "t", "than", "that", "the", "their",
        "theirs", "them", "themselves", "then", "there", "these", "they",
        "this", "those", "through", "to", "too", "under", "until", "up",
        "ve", "very", "was", "wasn", "we", "were", "weren", "what", "when",
        "where", "which", "while", "who", "whom", "why", "will", "with",
        "won", "would", "wouldn", "y", "you", "your", "yours", "yourself",
        "yourselves",
    };

    /// <summary>
    /// Extract top N keywords from text by frequency, excluding stop words
    /// and short tokens. Returns empty array for very short or stop-word-only
    /// input.
    /// </summary>
    public static string[] ExtractKeywords(string text, int maxKeywords = 3, int minLength = 3)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        // Lowercase, split on non-letter/whitespace, filter
        var words = text.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?',
                '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '-', '_',
                '+', '=', '*', '&', '%', '$', '#', '@', '|', '<', '>', '`', '~' },
                StringSplitOptions.RemoveEmptyEntries);

        // Count frequencies, filtering stop words and short words
        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in words)
        {
            if (w.Length < minLength || StopWords.Contains(w)) continue;
            freq.TryGetValue(w, out var count);
            freq[w] = count + 1;
        }

        if (freq.Count == 0) return Array.Empty<string>();

        return freq
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(maxKeywords)
            .Select(kv => kv.Key)
            .ToArray();
    }

    /// <summary>
    /// Combine entry_type, user tags, and auto-extracted keywords into a
    /// compact tag string like "[correction, sql, parameterized]".
    /// Capped at ~60 chars. Falls back to entry_type only when content
    /// is empty and no user tags are provided.
    /// </summary>
    public static string FormatCompactTags(
        EntryType entryType,
        string[]? userTags,
        string? content,
        int maxChars = 60)
    {
        var entryTypeStr = EntryTypeToLower(entryType);
        var tags = new List<string> { entryTypeStr };

        if (userTags is { Length: > 0 })
        {
            foreach (var t in userTags)
            {
                var tag = t.ToLowerInvariant();
                if (!tags.Contains(tag, StringComparer.Ordinal))
                    tags.Add(tag);
            }
        }
        else if (!string.IsNullOrWhiteSpace(content))
        {
            var keywords = ExtractKeywords(content, 3);
            foreach (var k in keywords)
            {
                if (!tags.Contains(k, StringComparer.Ordinal))
                    tags.Add(k);
            }
        }

        // Build tag string
        var full = $"[{string.Join(", ", tags)}]";
        if (full.Length <= maxChars) return full;

        // Truncate: keep entry_type + as many extras as fit
        var result = $"[{entryTypeStr}";
        for (var i = 1; i < tags.Count; i++)
        {
            var candidate = $"{result}, {tags[i]}";
            if (candidate.Length + 1 > maxChars) break;
            result = candidate;
        }
        return $"{result}]";
    }

    /// <summary>
    /// AOT-safe lowercase conversion of <see cref="EntryType"/> to string.
    /// Does NOT call <c>.ToString()</c> — F# discriminated union
    /// <c>ToString()</c> relies on <c>StructuredPrintfImpl</c> which is
    /// trimmed under NativeAOT.
    /// </summary>
    internal static string EntryTypeToLower(EntryType entryType)
    {
        if (entryType.IsCorrection) return "correction";
        if (entryType.IsPreference) return "preference";
        if (entryType.IsDecision) return "decision";
        if (entryType.IsSurfaced) return "surfaced";
        if (entryType.IsImported) return "imported";
        if (entryType.IsCompacted) return "compacted";
        if (entryType.IsIngested) return "ingested";
        return "unknown";
    }
}
