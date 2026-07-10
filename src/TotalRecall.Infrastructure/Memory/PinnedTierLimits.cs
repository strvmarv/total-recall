// src/TotalRecall.Infrastructure/Memory/PinnedTierLimits.cs
//
// Single source of truth for the pinned-tier content-size cap and the
// canonical over-limit error message. Both TotalRecall.Server (MemoryPinHandler,
// MemoryStoreHandler, ServerComposition) and TotalRecall.Cli (PinCommand) already
// reference TotalRecall.Infrastructure, so this is the natural shared home.
//
// Config override: [tiers.pinned] max_content_chars (Tiers.Pinned.MaxContentChars).

namespace TotalRecall.Infrastructure.Memory;

/// <summary>
/// Single source of truth for the pinned-tier content-size cap and its
/// associated error message. Pinned content is injected verbatim into every
/// session context and is never truncated, so size is enforced at the door.
/// <para>
/// The hard-coded default (<see cref="DefaultMaxContentChars"/>) can be raised
/// or lowered per-host via the <c>[tiers.pinned] max_content_chars</c> config
/// key (<c>Tiers.Pinned.MaxContentChars</c>).
/// </para>
/// </summary>
public static class PinnedTierLimits
{
    /// <summary>Default per-entry size cap for pinned content (chars).
    /// Measured in .NET <c>string.Length</c> (UTF-16 code units), so e.g.
    /// an emoji counts as ~2. Overridden by
    /// <c>Tiers.Pinned.MaxContentChars</c> in the effective config.</summary>
    public const int DefaultMaxContentChars = 500;

    /// <summary>
    /// Returns the canonical content-limit error message for pinned entries.
    /// Used by both the MCP server handlers and the CLI pin command so the
    /// wording stays identical across all entry points.
    /// </summary>
    public static string ContentLimitMessage(int limit, int actual) =>
        $"pinned entries are limited to {limit} characters ({actual} given); " +
        "trim the content or store a concise summary and pin that instead";

    /// <summary>
    /// Returns the canonical content-limit error message for hot-tier entries.
    /// Tier-neutral counterpart to <see cref="ContentLimitMessage"/> — used
    /// wherever a write targets <c>Tier.Hot</c> (including pinned:true writes,
    /// which route through the pinned tier but still enforce the hot cap).
    /// </summary>
    public static string HotContentLimitMessage(int limit, int actual) =>
        $"hot entries are limited to {limit} characters ({actual} given); " +
        "store a concise summary instead, or keep it in warm";
}
