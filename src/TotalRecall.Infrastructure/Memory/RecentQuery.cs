// src/TotalRecall.Infrastructure/Memory/RecentQuery.cs
//
// Shared cross-tier "recent memories" query used by both the memory_recent
// MCP handler and the `total-recall memory recent` CLI command. Queries each
// requested tier via IStore.List (pushing OrderBy/Limit/Project/Scopes/
// EntryType down to the store), then merges, sorts by the chosen timestamp,
// and caps to `Limit`. Correct top-N: a globally-newest entry is always among
// its own tier's newest N, so fetching Limit per tier and re-capping is exact.

using System;
using System.Collections.Generic;
using System.Linq;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Memory;

public sealed record RecentOptions(
    int Limit,
    Tier? Tier,
    EntryType? Type,
    string? Project,
    string Order,                       // "created" | "updated" | "accessed"
    IReadOnlyList<string>? Scopes);

public static class RecentQuery
{
    public static IReadOnlyList<(Tier Tier, Entry Entry)> Run(IStore store, RecentOptions o)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(o);

        var column = o.Order switch
        {
            "updated" => "updated_at",
            "accessed" => "last_accessed_at",
            _ => "created_at",
        };
        Func<Entry, long> selector = o.Order switch
        {
            "updated" => e => e.UpdatedAt,
            "accessed" => e => e.LastAccessedAt,
            _ => e => e.CreatedAt,
        };

        var opts = new ListEntriesOpts
        {
            OrderBy = column + " DESC",
            Limit = o.Limit,
            Project = o.Project,
            Scopes = o.Scopes,
            EntryType = o.Type,
        };

        var tiers = o.Tier is { } only
            ? new[] { only }
            : new[] { Tier.Hot, Tier.Warm, Tier.Cold };

        var merged = new List<(Tier Tier, Entry Entry)>();
        foreach (var tier in tiers)
            foreach (var e in store.List(tier, ContentType.Memory, opts))
                merged.Add((tier, e));

        return merged
            .OrderByDescending(x => selector(x.Entry))
            .ThenBy(x => x.Entry.Id, StringComparer.Ordinal)
            .Take(o.Limit)
            .ToList();
    }
}
