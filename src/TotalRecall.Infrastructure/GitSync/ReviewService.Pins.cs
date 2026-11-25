using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.GitSync;

// Git sync, milestone 1b-iii part 3 (pin review), D6 and D7: answering a pin item. `approve` of a new-pin pins the entry
// here (moved to hot when it is not there, sticky, decay 1.0, the row's comparison columns following, no stamp); `approve`
// of a pin-edit applies the held content group of the row's base to the pinned entry, as a local unpin does (D5), with the
// vector of the base's text computed before the transaction. `decline` of a new-pin changes only the item; `decline` of a
// pin-edit leaves a fresh content stamp above the base's pending, so the next sync pushes the text pinned here. Nothing is
// pushed by an answer. Every answer runs in the frame of the held deletions (RunAsync) with the checks of CheckPin.

/// <summary>
/// What the checks of a pin answer read inside its transaction: the item, its row (active, with a base), the local entry,
/// the row's parsed base and the item's live shown hash (D1: of the entry for a <c>new-pin</c>, of the base for a <c>pin-edit</c>).
/// </summary>
internal sealed record PinFacts(PinItem Item, GitSyncRow Row, LocalRead.Present Entry, ParsedRecord Base, string LiveHash);

public sealed partial class ReviewService
{
    /// <summary>The refusal text of <c>no-pin-item</c>.</summary>
    public const string NoPinItemText = "no open pin review of this kind for this id; nothing changed";

    private const string NewPinKind = "new-pin";
    private const string PinEditKind = "pin-edit";

    /// <summary>The line a declined <c>pin-edit</c> carries (D7); the CLI prints it, as a warning, before it runs the answer.</summary>
    public const string RevertWarning =
        "this replaces that change in the repository and on your other machines where the memory is not pinned; "
        + "where it is pinned it is held there as an edit";

    /// <summary>
    /// Approves the <paramref name="kind"/> item of <paramref name="entryId"/> (D6) after the checks of <see cref="CheckPin"/>
    /// (with <c>unshowable-text</c> and <c>over-pin-cap</c>). A <c>new-pin</c>: the memory is pinned here. A <c>pin-edit</c>:
    /// the held text, project and scope become the pinned memory's, with an embedding computed first (a base that changed
    /// since is <c>stale-item</c>, an embedder that throws is <c>failed</c>). The item becomes <c>approved</c>; nothing is pushed.
    /// </summary>
    public Task<SetupResult> ApproveAsync(string entryId, string kind, string? shownHash, CancellationToken ct = default) =>
        RunPinAsync(
            entryId,
            kind,
            shownHash,
            ReviewOperation.Approve,
            kind == PinEditKind ? store => EmbedPinBase(store, entryId, shownHash) : null,
            (tx, facts, embedded) => facts.Item.Kind == NewPinKind
                ? ApproveNewPin(tx, entryId, facts)
                : ApprovePinEdit(tx, entryId, facts, embedded),
            ct);

    /// <summary>
    /// Declines the <paramref name="kind"/> item of <paramref name="entryId"/> (D7) after the checks of <see cref="CheckPin"/>.
    /// A <c>new-pin</c>: the memory stays an ordinary memory here and only the item changes. A <c>pin-edit</c>: a fresh
    /// content stamp above the base's is left pending (no stamp to issue: <c>failed</c>), so the next sync pushes the text
    /// pinned here; the lines carry the revert warning. The item becomes <c>declined</c>.
    /// </summary>
    public Task<SetupResult> DeclineAsync(string entryId, string kind, string? shownHash, CancellationToken ct = default) =>
        RunPinAsync(
            entryId,
            kind,
            shownHash,
            ReviewOperation.Decline,
            embed: null,
            (tx, facts, _) => facts.Item.Kind == NewPinKind ? DeclineNewPin(tx, entryId) : DeclinePinEdit(tx, entryId, facts),
            ct);

    private Task<SetupResult> RunPinAsync(
        string entryId,
        string kind,
        string? shownHash,
        ReviewOperation op,
        Func<SqliteStore, Embedded?>? embed,
        Func<SqliteStoreTransaction, PinFacts, Embedded?, SetupResult> work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return RunAsync(
            entryId, Refuse(SyncSetup.Refusal.NoPinItem, NoPinItemText), tx => CheckPin(tx, entryId, kind, shownHash, op), embed, work, ct);
    }

    /// <summary>
    /// The live shown hash of a pin item (D1): of the entry's text, project and scope for a <c>new-pin</c>, of the base's
    /// for a <c>pin-edit</c>.
    /// </summary>
    private static string LiveHash(string kind, SyncMerge.LocalEntry entry, SyncRecord.SyncRecord baseRecord) =>
        kind == NewPinKind
            ? SyncPin.shownHash(entry.Content.Text, entry.Content.Project, entry.Content.Scope)
            : ApplyPlanning.BaseShownHash(baseRecord);

    /// <summary>
    /// The checks of a pin answer inside its transaction (D6, D7), in this order, each a refusal that changes nothing unless
    /// stated: an item of <paramref name="kind"/> <c>open</c> or <c>unseen</c> (<c>no-pin-item</c>, also for an unknown
    /// kind); the row and entry checks of <see cref="ReadRowAndEntry"/> and a base that parses (<c>failed</c>); the shown hash
    /// present (<c>shown-hash-required</c>); the entry present, not sticky for a <c>new-pin</c> and sticky for a
    /// <c>pin-edit</c> (<c>stale-item</c>: the next sync closes the item); for a <c>pin-edit</c> no local content edit (a
    /// pending content stamp, or the entry's content-group hash against <c>scanned_content_hash</c>: <c>edited-locally</c>);
    /// the shown hash equal to the live one (else <c>stale-item</c> with the item as it is now, and the item's update to the
    /// live hash and <c>unseen</c> is committed: machine from the base's <c>written_by</c>); for <c>approve</c> only, the text,
    /// project and scope to pin unchanged by the list (<c>unshowable-text</c>) and the text within the pinned cap (0 or
    /// less: 500; <c>over-pin-cap</c>, worded by <see cref="PinnedTierLimits.ContentLimitMessage"/>).
    /// </summary>
    private CheckOutcome<PinFacts> CheckPin(
        SqliteStoreTransaction tx, string entryId, string kind, string? shownHash, ReviewOperation op)
    {
        static CheckOutcome<PinFacts> No(SetupResult refusal) => new(refusal, null);
        var conn = tx.Connection;
        var dbTx = tx.Transaction;

        var item = kind is NewPinKind or PinEditKind ? GitSyncReviews.ReadPinItem(conn, entryId, kind, dbTx) : null;
        if (item is not { State: "open" or "unseen" })
            return No(Refuse(SyncSetup.Refusal.NoPinItem, NoPinItemText));

        var rowRefusal = ReadRowAndEntry(tx, entryId, out var readRow, out var read);
        if (rowRefusal is not null)
            return No(rowRefusal);
        var row = readRow!;
        var parsed = ApplyPlanning.ParseBase(entryId, row.BaseJson!); // ReadRowAndEntry: a base is present
        if (parsed.Parsed is null)
            return No(UnreadableBase(parsed));
        var baseRecord = parsed.Parsed.Record;

        if (string.IsNullOrEmpty(shownHash))
        {
            return No(Refuse(
                SyncSetup.Refusal.ShownHashRequired,
                "a pin review is answered only with the hash that was shown; nothing changed"));
        }

        var newPin = kind == NewPinKind;
        if (read is not LocalRead.Present entry || entry.View.Sticky == newPin)
        {
            return No(Refuse(
                SyncSetup.Refusal.StaleItem,
                "the memory changed since the item arrived; nothing changed; the next sync closes the item"));
        }

        if (!newPin
            && (row.ContentStamp is not null
                || !string.Equals(SyncScan.contentHash(entry.View.Content), row.ScannedContentHash, StringComparison.Ordinal)))
        {
            return No(Refuse(
                SyncSetup.Refusal.EditedLocally,
                "the pinned memory was edited here after the edit was held; nothing changed; the next sync closes the item"));
        }

        var live = LiveHash(kind, entry.View, baseRecord);
        if (!string.Equals(shownHash, live, StringComparison.Ordinal))
        {
            GitSyncReviews.MarkPinUnseen(conn, entryId, kind, live, OptText(baseRecord.WrittenBy), dbTx);
            return new CheckOutcome<PinFacts>(
                Refuse(
                    SyncSetup.Refusal.StaleItem,
                    $"what the item shows changed since it was shown; only the item was updated; now: unseen {kind} {live}"),
                null,
                CommitRefusal: true);
        }

        if (op == ReviewOperation.Approve)
        {
            var text = newPin ? entry.View.Content.Text : baseRecord.Content;
            var project = OptText(newPin ? entry.View.Content.Project : baseRecord.Project);
            var scope = OptText(newPin ? entry.View.Content.Scope : baseRecord.Scope);
            if (!Showable(text, project, scope))
            {
                return No(Refuse(
                    SyncSetup.Refusal.UnshowableText,
                    "the text, project or scope holds characters the list does not show, so it cannot be approved; nothing changed"));
            }
            var cap = env.PinnedMaxContentChars <= 0 ? PinnedTierLimits.DefaultMaxContentChars : env.PinnedMaxContentChars;
            if (text.Length > cap)
                return No(Refuse(SyncSetup.Refusal.OverPinCap, PinnedTierLimits.ContentLimitMessage(cap, text.Length)));
        }

        return new CheckOutcome<PinFacts>(null, new PinFacts(item, row, entry, parsed.Parsed, live));
    }

    /// <summary>
    /// The embedding of a <c>pin-edit</c> approval, before the transaction: the text of the row's base, embedded with
    /// <see cref="Embedder"/>. Null, with no embedder created, for these refusals of the checks: no open or unseen
    /// <c>pin-edit</c> item, no shown hash, a row that cannot be read, no base, a base that does not parse, a shown hash that
    /// is not the base's live one. The other refusals are found only by the checks inside the transaction.
    /// </summary>
    private Embedded? EmbedPinBase(SqliteStore store, string entryId, string? shownHash)
    {
        var conn = store.Connection;
        var item = GitSyncReviews.ReadPinItem(conn, entryId, PinEditKind);
        if (item is not { State: "open" or "unseen" } || string.IsNullOrEmpty(shownHash))
            return null;

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
        var record = ApplyPlanning.ParseBase(entryId, row.BaseJson).Parsed?.Record;
        if (record is null
            || !string.Equals(shownHash, ApplyPlanning.BaseShownHash(record), StringComparison.Ordinal))
        {
            return null;
        }
        return new Embedded(row.BaseJson, Embedder().Embed(record.Content));
    }

    /// <summary>
    /// D6, <c>new-pin</c>: moves the entry to hot when it is not there (the vector is copied), sets it sticky with
    /// <c>decay_score</c> 1.0 as <c>memory_pin</c> does, writes the row with <c>local_sticky</c> true, <c>local_tier</c> hot
    /// and no pending sticky stamp (every other column kept: the base already holds the record's pin), and approves the item.
    /// </summary>
    private SetupResult ApproveNewPin(SqliteStoreTransaction tx, string entryId, PinFacts facts)
    {
        var conn = tx.Connection;
        var dbTx = tx.Transaction;
        tx.BeforeStep = BeforeStep;
        if (!facts.Entry.Tier.Equals(Tier.Hot))
            tx.Move(facts.Entry.Tier, ContentType.Memory, Tier.Hot, ContentType.Memory, entryId);
        tx.SetSticky(ContentType.Memory, entryId, true);
        tx.Update(Tier.Hot, ContentType.Memory, entryId, new UpdateEntryOpts { DecayScore = 1.0 });
        BeforeStep?.Invoke("row");
        GitSyncRows.Write(conn, facts.Row with { LocalSticky = true, LocalTier = Tier.Hot, StickyStamp = null }, dbTx);
        BeforeStep?.Invoke("resolve");
        GitSyncReviews.ResolvePinItem(conn, entryId, NewPinKind, "approved", dbTx);
        return SetupResult.Done($"approved {entryId}: the memory is pinned here; nothing is pushed");
    }

    /// <summary>
    /// D6, <c>pin-edit</c>: the base's content group applied to the pinned entry as a local unpin applies it (D5,
    /// <see cref="ApplyPlanning.ApplyBaseContent"/>) with the vector computed before the transaction (a base that changed
    /// since is <c>stale-item</c>), the row's comparison columns from the stored entry (the pending content stamp stays
    /// none), and the item approved. The entry stays pinned and in hot.
    /// </summary>
    private SetupResult ApprovePinEdit(SqliteStoreTransaction tx, string entryId, PinFacts facts, Embedded? embedded)
    {
        if (embedded is null || !string.Equals(embedded.BaseJson, facts.Row.BaseJson, StringComparison.Ordinal))
            return Refuse(SyncSetup.Refusal.StaleItem, "the record changed while the memory was prepared; nothing changed");

        var conn = tx.Connection;
        var dbTx = tx.Transaction;
        tx.BeforeStep = BeforeStep;
        var applied = ApplyPlanning.ApplyBaseContent(
            tx, entryId, facts.Entry, facts.Base.Record, embedded.Vector, env.HotMaxContentChars);
        BeforeStep?.Invoke("row");
        GitSyncRows.Write(conn, ApplyPlanning.WithComparison(facts.Row, applied), dbTx);
        BeforeStep?.Invoke("resolve");
        GitSyncReviews.ResolvePinItem(conn, entryId, PinEditKind, "approved", dbTx);
        return SetupResult.Done($"approved {entryId}: the held edit is now the pinned text here; nothing is pushed");
    }

    /// <summary>D7, <c>new-pin</c>: only the item changes (<c>declined</c>); the entry stays ordinary, no stamp is issued.</summary>
    private SetupResult DeclineNewPin(SqliteStoreTransaction tx, string entryId)
    {
        BeforeStep?.Invoke("resolve");
        GitSyncReviews.ResolvePinItem(tx.Connection, entryId, NewPinKind, "declined", tx.Transaction);
        return SetupResult.Done($"declined {entryId}: the memory stays an ordinary memory here; the pin stays on the other machines");
    }

    /// <summary>
    /// D7, <c>pin-edit</c>: a content stamp issued above the base's content stamp is left pending on the row (every other
    /// column kept; no stamp to issue: <c>failed</c>, nothing committed), and the item is declined; the lines carry the
    /// revert warning.
    /// </summary>
    private SetupResult DeclinePinEdit(SqliteStoreTransaction tx, string entryId, PinFacts facts)
    {
        var conn = tx.Connection;
        var dbTx = tx.Transaction;
        var stamp = ApplyPlanning.IssueStamp(
            conn, dbTx, NowMs(), FSharpOption<SyncStamp.Stamp>.Some(facts.Base.Record.ContentStamp));
        if (stamp is null)
            return Failed(NoStampText); // not committed: a clock the issue moved is rolled back
        BeforeStep?.Invoke("row");
        GitSyncRows.Write(conn, facts.Row with { ContentStamp = stamp.Value }, dbTx);
        BeforeStep?.Invoke("resolve");
        GitSyncReviews.ResolvePinItem(conn, entryId, PinEditKind, "declined", dbTx);
        return SetupResult.Done($"declined {entryId}: the next sync pushes the text pinned here", RevertWarning);
    }
}
