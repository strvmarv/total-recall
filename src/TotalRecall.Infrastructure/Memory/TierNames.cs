// src/TotalRecall.Infrastructure/Memory/TierNames.cs
//
// Plan 6 Task 6.0a — promoted from src/TotalRecall.Cli/Internal/TierNames.cs
// so both the CLI and the MCP Server can share the same Tier/ContentType
// parse + format helpers and the 6-pair sweep table without duplicating
// code (closes Plan 5 carry-forward #8). The shape of the public surface
// matches the old Cli-local version one-for-one; the only difference is
// the namespace and the fact that it is now `public`.

using TotalRecall.Core;

namespace TotalRecall.Infrastructure.Memory;

public static class TierNames
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
