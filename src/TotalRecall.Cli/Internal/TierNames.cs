// src/TotalRecall.Cli/Internal/TierNames.cs
//
// Plan 5 Task 5.4 — Cli-local equivalents of the Tier/ContentType string
// formatters and the 6-pair sweep table. A deliberate duplicate of
// Server/Handlers/EntryMapping.cs (which is internal to Server) because
// TotalRecall.Cli does NOT reference TotalRecall.Server. If Plan 5.10's
// output formatter refactor ever exposes a cross-host helper, this file
// can collapse into it.

using TotalRecall.Core;

namespace TotalRecall.Cli.Internal;

internal static class TierNames
{
    public static readonly (Tier Tier, ContentType Type)[] AllTablePairs =
    {
        (Tier.Hot,  ContentType.Memory),
        (Tier.Warm, ContentType.Memory),
        (Tier.Cold, ContentType.Memory),
        (Tier.Hot,  ContentType.Knowledge),
        (Tier.Warm, ContentType.Knowledge),
        (Tier.Cold, ContentType.Knowledge),
    };

    public static string TierName(Tier t) =>
        t.IsHot ? "hot" : t.IsWarm ? "warm" : "cold";

    public static string ContentTypeName(ContentType c) =>
        c.IsMemory ? "memory" : "knowledge";

    /// <summary>
    /// Parse "hot"|"warm"|"cold" → Tier. Returns null for unknown values.
    /// </summary>
    public static Tier? ParseTier(string s) => s switch
    {
        "hot" => Tier.Hot,
        "warm" => Tier.Warm,
        "cold" => Tier.Cold,
        _ => null,
    };

    /// <summary>
    /// Parse "memory"|"knowledge" → ContentType. Returns null for unknown.
    /// </summary>
    public static ContentType? ParseContentType(string s) => s switch
    {
        "memory" => ContentType.Memory,
        "knowledge" => ContentType.Knowledge,
        _ => null,
    };

    /// <summary>
    /// Tier warmth rank: hot=2, warm=1, cold=0. Used by promote/demote
    /// direction gates (promote must increase rank, demote must decrease).
    /// </summary>
    public static int WarmthRank(Tier t) =>
        t.IsHot ? 2 : t.IsWarm ? 1 : 0;
}
