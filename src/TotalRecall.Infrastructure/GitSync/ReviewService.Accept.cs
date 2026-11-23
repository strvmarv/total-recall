using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.GitSync;

// Git sync, milestone 1b-iii part 2 (held deletions), D2 and D4: accepting a held deletion. A remote hold (the record is
// gone from the repository, the memory is still here) is carried out as a proven pull carries out W6.4: the entry and its
// vector are deleted, the row is forgotten with its base kept and the item removed; no guard is involved, and a pinned
// entry is deleted too, because the person answered for it. A local hold (the memory was deleted here, its record is still
// in the repository) becomes `approved`, which the next pull and push carry out (D4); nothing else is written.

public sealed partial class ReviewService
{
    private const string DroppedNote =
        "note: pending-changes-dropped (a tag, tier, project, scope or type change went with the memory)";

    /// <summary>
    /// Accepts the held deletion of <paramref name="entryId"/> (D2, D4) after the checks of <see cref="CheckItem"/>. A remote
    /// hold (a memory edited after the hold is <c>edited-locally</c>; a base that does not parse is <c>failed</c>; a
    /// <paramref name="shownHash"/> is ignored): the memory is deleted here and its row forgotten, and the lines say so, with
    /// a note when a tag, tier, project, scope or type change went with it. A local hold (only with the shown hash): the item
    /// becomes <c>approved</c> and the next sync removes the record; an item already approved answers <c>already accepted</c>
    /// and changes nothing.
    /// </summary>
    public Task<SetupResult> AcceptAsync(string entryId, string? shownHash, CancellationToken ct = default) =>
        RunAsync(entryId, ReviewOperation.Accept, shownHash, embed: null, (tx, facts, _) => Accept(tx, entryId, facts), ct);

    private SetupResult Accept(SqliteStoreTransaction tx, string entryId, ItemFacts facts) =>
        facts.Item.Direction == "remote" ? AcceptRemote(tx, entryId, facts) : AcceptLocal(tx, entryId, facts);

    /// <summary>
    /// D2: deletes the entry and its vector (the apply's own <c>tx.Delete</c>), writes the row a proven remote deletion
    /// leaves (<c>planRow</c> of W6.4's decision: <c>forgotten</c>, base kept, pending stamps, <c>pushing_stamps</c> and
    /// <c>repush_groups</c> cleared; planned with no stored entry, so the scanned comparison columns stay) and removes the item.
    /// </summary>
    private SetupResult AcceptRemote(SqliteStoreTransaction tx, string entryId, ItemFacts facts)
    {
        var present = (LocalRead.Present)facts.Read;
        var oldBase = facts.Base ?? throw new InvalidOperationException("the edit test did not parse the base");
        var decision = SyncMerge.remoteDeletion(facts.Row.TierStamp is not null, oldBase.Record, present.View);
        var plan = ApplyPlanning.PlanStore(decision, null, present.View, env.HotMaxContentChars);

        tx.Delete(present.Tier, ContentType.Memory, entryId);
        // a deletion clears the pending stamps and issues none, so the row is planned with no clock
        var (next, error) = ApplyPlanning.BuildRow(
            entryId, decision, facts.Row, oldBase, theirs: null, stored: null, hadEntry: true, plan.Colder, _ => null);
        if (error is not null || next is null)
            throw new InvalidOperationException(error ?? "the deletion planned no row");
        GitSyncRows.Write(tx.Connection, next, tx.Transaction);
        GitSyncReviews.DeleteHeld(tx.Connection, entryId, tx.Transaction);

        var deleted = $"accepted {entryId}: the memory was deleted here";
        return decision.Notes.Contains(SyncMerge.MergeNote.PendingChangesDropped)
            ? SetupResult.Done(deleted, DroppedNote)
            : SetupResult.Done(deleted);
    }

    /// <summary>D4: sets the item <c>approved</c> (the shown hash was checked); an item already approved changes nothing.</summary>
    private static SetupResult AcceptLocal(SqliteStoreTransaction tx, string entryId, ItemFacts facts)
    {
        if (facts.Item.State == "approved")
            return SetupResult.Done($"already accepted {entryId}");
        GitSyncReviews.ApproveHeld(tx.Connection, entryId, tx.Transaction);
        return SetupResult.Done($"accepted {entryId}: the next sync removes its record from the repository");
    }
}
