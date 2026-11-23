using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.GitSync;

// Git sync, milestone 1b-iii part 2 (held deletions), D3, D5 and D6: restoring a held deletion. A remote hold (the record
// is gone from the repository, the memory is still here) keeps the memory as it is and rewrites its row as a new one, with
// a fresh stamp per group above the old base's, so the next sync publishes it again as a new record. A local hold (the
// memory was deleted here, its record is still in the repository) re-inserts the memory from the row's base as the apply
// inserts a record, but never pinned: a sticky base opens a new-pin item for a later part to answer and no unpin is pushed.
// The embedding of a local restore is the only thing computed before the transaction; the base it was computed from is
// compared inside it.

public sealed partial class ReviewService
{
    private const string NoStampText =
        "this machine cannot issue a stamp (no sync identity, or its clock range is exhausted); nothing changed";

    /// <summary>
    /// Restores the held deletion of <paramref name="entryId"/> (D3, D5) after the checks of <see cref="CheckItem"/>. A
    /// remote hold (a <paramref name="shownHash"/> is ignored): the memory is kept and the next sync publishes it again; a
    /// base that does not parse, or no stamp to issue, is <c>failed</c>. A local hold or an approved item (only with the
    /// shown hash): the memory is inserted again, not pinned, from the row's base with an embedding computed first; a base
    /// that changed since the embedding or a knowledge entry with the id is <c>stale-item</c>, a base that does not parse or
    /// an embedder that throws is <c>failed</c>; a sticky base opens a <c>new-pin</c> item, which the lines say.
    /// </summary>
    public Task<SetupResult> RestoreAsync(string entryId, string? shownHash, CancellationToken ct = default) =>
        RunAsync(
            entryId,
            ReviewOperation.Restore,
            shownHash,
            store => EmbedBase(store, entryId, shownHash),
            (tx, facts, embedded) => facts.Item.Direction == "remote"
                ? RestoreRemote(tx, entryId, facts)
                : RestoreLocal(tx, entryId, facts, embedded),
            ct);

    /// <summary>
    /// The embedding of a local restore, before the transaction: the text of the row's base, embedded with
    /// <see cref="Embedder"/>. Null, with no embedder created, for a remote hold and for these refusals of the checks: no
    /// open or approved local item, a missing shown hash or one that is not the item's, a row that cannot be read, no base,
    /// a base that does not parse. Other refusals (an entry or a knowledge entry under the id, a row that is not active, an
    /// entry in two tiers) are found only by the checks inside the transaction, after the embedding.
    /// </summary>
    private Embedded? EmbedBase(SqliteStore store, string entryId, string? shownHash)
    {
        var conn = store.Connection;
        var item = GitSyncReviews.ReadHeldItem(conn, entryId);
        if (item is not { State: "open" or "approved", Direction: "local" }
            || string.IsNullOrEmpty(shownHash)
            || !string.Equals(shownHash, item.RecordHash, StringComparison.Ordinal))
        {
            return null;
        }

        GitSyncRow? row;
        try
        {
            row = GitSyncRows.Read(conn, entryId);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return null; // the checks inside the transaction report why the row cannot be read
        }
        if (row?.BaseJson is null)
            return null;
        var parsed = ApplyPlanning.ParseBase(entryId, row.BaseJson);
        if (parsed.Parsed is null)
            return null;
        return new Embedded(row.BaseJson, Embedder().Embed(parsed.Parsed.Record.Content));
    }

    /// <summary>
    /// D3: leaves the entry and its sticky flag as they are and rewrites the row as a new one: no base, a stamp per group
    /// issued from the clock above that group's stamp in the old base, <c>pushing_stamps</c> and <c>repush_groups</c>
    /// cleared, every other column kept; then removes the item.
    /// </summary>
    private SetupResult RestoreRemote(SqliteStoreTransaction tx, string entryId, ItemFacts facts)
    {
        var parsed = ApplyPlanning.ParseBase(entryId, facts.Row.BaseJson!);
        if (parsed.Parsed is null)
            return UnreadableBase(parsed);
        var oldBase = parsed.Parsed.Record;

        var conn = tx.Connection;
        var dbTx = tx.Transaction;
        var now = NowMs();
        var content = ApplyPlanning.IssueStamp(conn, dbTx, now, FSharpOption<SyncStamp.Stamp>.Some(oldBase.ContentStamp));
        var sticky = ApplyPlanning.IssueStamp(conn, dbTx, now, FSharpOption<SyncStamp.Stamp>.Some(oldBase.Placement.StickyStamp));
        var tier = ApplyPlanning.IssueStamp(conn, dbTx, now, FSharpOption<SyncStamp.Stamp>.Some(oldBase.Placement.TierStamp));
        if (content is null || sticky is null || tier is null)
            return Failed(NoStampText); // not committed: a clock the issues moved is rolled back

        GitSyncRows.Write(
            conn,
            facts.Row with
            {
                BaseJson = null,
                ContentStamp = content.Value,
                StickyStamp = sticky.Value,
                TierStamp = tier.Value,
                PushingStamps = null,
                RepushGroups = null,
            },
            dbTx);
        GitSyncReviews.DeleteHeld(conn, entryId, dbTx);
        return SetupResult.Done($"restored {entryId}: the next sync publishes it again");
    }

    /// <summary>
    /// D5: inserts the row's base as the memory the apply's <c>InsertFromRecord</c> inserts (the record's tier, or warm
    /// over the hot limit; never sticky) with the embedding computed before the transaction, writes the row the apply writes
    /// for it (pending stamps cleared, comparison columns from the stored entry) and its <c>import_log</c> line, opens a
    /// <c>new-pin</c> item with the held item's hash when the base is sticky, and removes the item.
    /// </summary>
    private SetupResult RestoreLocal(SqliteStoreTransaction tx, string entryId, ItemFacts facts, Embedded? embedded)
    {
        var parsed = ApplyPlanning.ParseBase(entryId, facts.Row.BaseJson!);
        if (parsed.Parsed is null)
            return UnreadableBase(parsed);
        if (embedded is null || !string.Equals(embedded.BaseJson, facts.Row.BaseJson, StringComparison.Ordinal))
            return Refuse(SyncSetup.Refusal.StaleItem, "the record changed while the memory was prepared; nothing changed");
        if (ApplyPlanning.InKnowledge(tx, entryId))
            return Refuse(SyncSetup.Refusal.StaleItem, "the id now belongs to a knowledge entry; nothing changed");

        var theirs = parsed.Parsed;
        var record = theirs.Record;
        var conn = tx.Connection;
        var dbTx = tx.Transaction;
        var decision = SyncMerge.insertFrom(record, false);
        var plan = ApplyPlanning.PlanStore(decision, record, null, env.HotMaxContentChars);

        ApplyPlanning.InsertRecord(tx, entryId, plan.InsertTier!, record, embedded.Vector);
        var storedRead = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, entryId), () => tx.IsSticky(ContentType.Memory, entryId));
        var stored = (storedRead as LocalRead.Present)?.View
            ?? throw new InvalidOperationException("the memory cannot be read back after it was written");

        var (next, error) = ApplyPlanning.BuildRow(
            entryId, decision, facts.Row, oldBase: theirs, theirs: theirs, stored, hadEntry: false, plan.Colder,
            floor => ApplyPlanning.IssueStamp(conn, dbTx, NowMs(), floor));
        if (error is not null || next is null)
            throw new InvalidOperationException(error ?? "the insert planned no row");
        GitSyncRows.Write(conn, next, dbTx);
        ApplyPlanning.LogImport(tx, entryId, record, stored);

        var pinned = record.Placement.Sticky;
        if (pinned)
            GitSyncReviews.UpsertNewPin(conn, entryId, facts.Item.RecordHash!, NowMs(), dbTx);
        GitSyncReviews.DeleteHeld(conn, entryId, dbTx);

        var back = $"restored {entryId}: the memory is back on this machine";
        return pinned
            ? SetupResult.Done(back, "the record is pinned: a new-pin review was opened; the memory is not pinned here")
            : SetupResult.Done(back);
    }
}
