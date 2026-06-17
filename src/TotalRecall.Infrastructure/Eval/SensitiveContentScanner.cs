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

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TotalRecall.Infrastructure.Eval;

/// <summary>
/// Detects secrets / PII / configured internal terms in text destined for the
/// public benchmark corpus. Returns category reasons (never the matched value).
/// </summary>
public static class SensitiveContentScanner
{
    // Interpreted (not Compiled) regex: RegexOptions.Compiled emits IL at
    // runtime and is not NativeAOT-safe. These run only on eval_grow accept,
    // over short strings, so interpreted matching is more than fast enough.
    private static readonly (string Reason, Regex Pattern)[] _detectors =
    {
        ("email address",
            new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")),
        ("API token or secret",
            new Regex(@"gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_\-]{16,}|xox[baprs]-[A-Za-z0-9\-]{10,}|sk-[A-Za-z0-9]{16,}|AKIA[0-9A-Z]{16}")),
        ("private key block",
            new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
    };

    /// <summary>
    /// Scan <paramref name="text"/> for secrets, PII, and any of
    /// <paramref name="internalTerms"/> (case-insensitive substring, blanks
    /// ignored). Returns an empty list when clean.
    /// </summary>
    public static IReadOnlyList<string> Scan(string? text, IReadOnlyCollection<string>? internalTerms)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();

        var reasons = new List<string>();
        foreach (var (reason, pattern) in _detectors)
        {
            if (pattern.IsMatch(text)) reasons.Add(reason);
        }

        if (internalTerms is not null)
        {
            foreach (var raw in internalTerms)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var term = raw.Trim();
                if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                    reasons.Add($"internal term: {term}");
            }
        }

        return reasons;
    }
}
