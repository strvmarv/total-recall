// src/TotalRecall.Cli/Commands/Memory/MoveHelpers.cs
//
// Plan 5 Task 5.4 — shared plumbing for `memory promote` and `memory demote`.
// Both verbs run the same 4-step sequence from src-ts/memory/promote-demote.ts:
//
//     1. locate source (tier, type) via a 6-pair sweep
//     2. delete the embedding at the source
//     3. transactionally store.Move source -> target
//     4. re-embed content and insert at target
//
// The only behavioral difference between promote and demote is the
// direction gate (warmer vs colder), which is why that validation stays
// per-command and this helper only owns the locate-move-embed machinery.

using System;
using System.IO;
using TotalRecall.Cli.Internal;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Cli.Commands.Memory;

internal static class MoveHelpers
{
    /// <summary>
    /// Sweep all 6 (tier, type) pairs looking for <paramref name="id"/>.
    /// Returns the first (tier, type, entry) hit, or null if none match.
    /// </summary>
    public static (Tier Tier, ContentType Type, Entry Entry)? Locate(
        ISqliteStore store, string id)
    {
        foreach (var pair in TierNames.AllTablePairs)
        {
            var entry = store.Get(pair.Tier, pair.Type, id);
            if (entry is not null)
            {
                return (pair.Tier, pair.Type, entry);
            }
        }
        return null;
    }

    /// <summary>
    /// Execute the 4-step move: delete source embedding, store.Move,
    /// re-embed content, insert target embedding. Mirrors the TS
    /// promote/demote flow.
    /// </summary>
    public static void MoveAndReEmbed(
        ISqliteStore store,
        IVectorSearch vec,
        IEmbedder embedder,
        Entry entry,
        Tier fromTier, ContentType fromType,
        Tier toTier, ContentType toType)
    {
        vec.DeleteEmbedding(fromTier, fromType, entry.Id);
        store.Move(fromTier, fromType, toTier, toType, entry.Id);
        var embedding = embedder.Embed(entry.Content);
        vec.InsertEmbedding(toTier, toType, entry.Id, embedding);
    }
}
