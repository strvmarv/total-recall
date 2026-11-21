using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>
/// Decides whether a session is <c>interactive</c> or <c>automated</c> from its kept records (spec section 6). The first
/// rule that applies wins: an <c>sdk*</c> entrypoint; then, for a session with exactly one real user turn, a known
/// automation opening or a very long prompt; otherwise interactive.
/// </summary>
internal static class SessionKindRule
{
    public const string Interactive = "interactive";
    public const string Automated = "automated";

    /// <summary>A single prompt longer than this (redacted, trimmed) marks the session automated.</summary>
    public const int LongPromptLimit = 1_500;

    /// <summary>Known automation openings, matched case-insensitively after trimming. <c>Round &lt;n&gt;</c> is a pattern, below.</summary>
    public static readonly IReadOnlyList<string> Openings = new[]
    {
        "Review this change for security",
        "You previously flagged",
        "Scope:",
        "You are reviewing",
        "Read the full review brief",
    };

    private static readonly Regex RoundOpening =
        new(@"^Round \d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <param name="entrypoint">The head's <c>entrypoint</c>, if any.</param>
    /// <param name="settledUserTurns">Real user turns in the units before the watermark (kept on the session row).</param>
    /// <param name="passUserTurns">Real user turns in this pass.</param>
    /// <param name="passFirstUserText">The first real user turn of this pass (redacted); only the session's first turn when nothing is settled.</param>
    /// <param name="storedKind">The kind already stored, kept when a lone turn lies in the settled part and cannot be re-read.</param>
    public static string Decide(
        string? entrypoint, int settledUserTurns, int passUserTurns, string? passFirstUserText, string? storedKind)
    {
        if (entrypoint is not null && entrypoint.StartsWith("sdk", StringComparison.OrdinalIgnoreCase))
            return Automated;

        if (settledUserTurns + passUserTurns != 1)
            return Interactive;

        if (settledUserTurns == 1)
            return string.IsNullOrEmpty(storedKind) ? Interactive : storedKind;

        if (passFirstUserText is null)
            return Interactive;

        var text = passFirstUserText.Trim();
        if (text.Length > LongPromptLimit)
            return Automated;
        foreach (var opening in Openings)
        {
            if (text.StartsWith(opening, StringComparison.OrdinalIgnoreCase))
                return Automated;
        }

        return RoundOpening.IsMatch(text) ? Automated : Interactive;
    }
}
