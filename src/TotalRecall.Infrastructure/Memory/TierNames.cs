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
        (Tier.Hot,    ContentType.Memory),
        (Tier.Warm,   ContentType.Memory),
        (Tier.Cold,   ContentType.Memory),
        (Tier.Pinned, ContentType.Memory),
        (Tier.Hot,    ContentType.Knowledge),
        (Tier.Warm,   ContentType.Knowledge),
        (Tier.Cold,   ContentType.Knowledge),
        (Tier.Pinned, ContentType.Knowledge),
    };

    public static string TierName(Tier t) =>
        t.IsHot ? "hot" : t.IsWarm ? "warm" : t.IsPinned ? "pinned" : "cold";

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
        "pinned" => Tier.Pinned,
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
    /// Parse a case-insensitive entry-type name → <see cref="EntryType"/>.
    /// Returns null for an unknown value or null/empty input. Accepts the
    /// seven DU case names.
    /// </summary>
    public static EntryType? ParseEntryType(string? s) => s?.ToLowerInvariant() switch
    {
        "correction" => EntryType.Correction,
        "preference" => EntryType.Preference,
        "decision" => EntryType.Decision,
        "surfaced" => EntryType.Surfaced,
        "imported" => EntryType.Imported,
        "compacted" => EntryType.Compacted,
        "ingested" => EntryType.Ingested,
        _ => null,
    };

    /// <summary>
    /// Format an <see cref="EntryType"/> as its DB/wire case name
    /// ("Correction", "Preference", ...). Matches the value stored in the
    /// <c>entry_type</c> column.
    /// </summary>
    public static string EntryTypeName(EntryType t) =>
        t.IsCorrection ? "Correction"
        : t.IsPreference ? "Preference"
        : t.IsDecision ? "Decision"
        : t.IsSurfaced ? "Surfaced"
        : t.IsImported ? "Imported"
        : t.IsCompacted ? "Compacted"
        : "Ingested";

    /// <summary>
    /// Tier warmth rank: pinned=3, hot=2, warm=1, cold=0. Promote/demote
    /// direction gates never legally target pinned — both handlers reject
    /// pinned source AND target tiers explicitly before the rank check
    /// (memory_pin / memory_unpin are the only doors). Rank 3 exists for
    /// ordering/display only.
    /// </summary>
    public static int WarmthRank(Tier t) =>
        t.IsPinned ? 3 : t.IsHot ? 2 : t.IsWarm ? 1 : 0;
}
