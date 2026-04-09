// src/TotalRecall.Infrastructure/Memory/MoveHelpers.cs
//
// Plan 6 Task 6.0a — promoted from src/TotalRecall.Cli/Commands/Memory/MoveHelpers.cs
// so the Server's memory_promote / memory_demote / memory_import handlers
// can share the same locate-move-embed machinery the CLI promote/demote
// verbs already use, without forcing a Cli→Server or Server→Cli reference.
// Closes Plan 5 carry-forward #8 together with the TierNames promotion.
//
// Behavior is identical to the old Cli-local version: sweep all 6 (tier,
// type) pairs to locate a row, then run the 4-step move (delete embedding,
// store.Move, re-embed content, insert new embedding). Direction gating
// (promote must go warmer, demote must go colder) stays per-caller.
//
// Atomicity caveat: the 4-step move is NOT transactional across the
// store+vector seams. A crash between store.Move and the re-embed leaves
// the new row without an embedding. This is Plan 5 carry-forward #9 and
// is documented at the re-embed site. Plan 6 Task 6.0a does not fix it.

using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Memory;

public static class MoveHelpers
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
        // Resolve the source rowid BEFORE store.Move, while the content
        // row still exists in the source table. After the move the old
        // rowid is meaningless, so deleting the vec row first is the only
        // way to keep the two sides aligned.
        var sourceRowid = store.GetRowid(fromTier, fromType, entry.Id);
        if (sourceRowid is not null)
            vec.DeleteEmbedding(fromTier, fromType, sourceRowid.Value);
        store.Move(fromTier, fromType, toTier, toType, entry.Id);
        // TODO(Plan 5+): atomicity gap (carry-forward #9) — a crash between
        // store.Move and this re-embed leaves the target row without an
        // embedding. Fix by wrapping the full sequence in a single
        // transaction spanning both seams, or by making store.Move take
        // a callback that runs the embed step before committing.
        var embedding = embedder.Embed(entry.Content);
        vec.InsertEmbedding(toTier, toType, entry.Id, embedding);
    }
}
