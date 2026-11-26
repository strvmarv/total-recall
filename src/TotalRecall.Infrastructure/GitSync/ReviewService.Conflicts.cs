using System.Text;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;
using SyncRecord = TotalRecall.Core.SyncRecord.SyncRecord;

namespace TotalRecall.Infrastructure.GitSync;

// Git sync, milestone 1b-iii part 4 (conflict review), D6: answering a conflict item. `keep` removes the item and changes
// nothing else. `swap` makes a kept alternative the entry's text and `merge` a given text, and only the text (the hot limit
// of a TakeTheirs applies), re-embedded with the vector computed before the transaction; the row gets a fresh content stamp
// above the base's and the pending one, left pending, and its comparison columns follow the stored entry; the item is
// removed. Nothing is pushed by an answer. Every answer runs in the frame of the held deletions (RunAsync) with the checks
// of CheckConflict.

/// <summary>
/// What the checks of a conflict answer read inside its transaction: the item, its losers (oldest first, as stored), the
/// local entry, the row (<c>swap</c> and <c>merge</c>: active with a base; <c>keep</c>: not read, null), the row's parsed
/// base (<c>swap</c> and <c>merge</c>; <c>keep</c>: null) and the chosen text (<c>swap</c>: the named loser's; <c>merge</c>:
/// the given one; <c>keep</c>: null).
/// </summary>
internal sealed record ConflictFacts(
    ConflictItem Item, FSharpList<SyncConflict.Loser> Losers, LocalRead.Present Entry, GitSyncRow? Row,
    SyncRecord? Base, string? Text);

public sealed partial class ReviewService
{
    /// <summary>The refusal text of <c>no-conflict-item</c>.</summary>
    public const string NoConflictItemText = "no open conflict review for this id; nothing changed";

    /// <summary>The most UTF-8 bytes a merged text may have: the record limit (D6).</summary>
    private const int MaxMergeTextBytes = 262144;

    /// <summary>
    /// Keeps the applied text of <paramref name="entryId"/>'s conflict item (D6) after the checks of
    /// <see cref="CheckConflict"/>: the item is removed and nothing else changes (a pending push stays pending).
    /// </summary>
    public Task<SetupResult> KeepAsync(string entryId, string? shownHash, CancellationToken ct = default) =>
        RunConflictAsync(entryId, ReviewOperation.Keep, shownHash, null, null, ct);

    /// <summary>
    /// Makes loser <paramref name="loserIndex"/> (1-based, newest first; may be omitted when there is exactly one) the text of
    /// <paramref name="entryId"/> (D6) after the checks of <see cref="CheckConflict"/>. The text is re-embedded, a fresh content
    /// stamp is left pending so the next sync pushes it, and the item is removed. A text equal to the entry's only removes the item.
    /// </summary>
    public Task<SetupResult> SwapAsync(string entryId, string? shownHash, int? loserIndex, CancellationToken ct = default) =>
        RunConflictAsync(entryId, ReviewOperation.Swap, shownHash, loserIndex, null, ct);

    /// <summary>As <see cref="SwapAsync"/>, with <paramref name="text"/> as the new text (<c>bad-merge-text</c> when it is not acceptable).</summary>
    public Task<SetupResult> MergeAsync(string entryId, string? shownHash, string? text, CancellationToken ct = default) =>
        RunConflictAsync(entryId, ReviewOperation.Merge, shownHash, null, text, ct);

    private Task<SetupResult> RunConflictAsync(
        string entryId, ReviewOperation op, string? shownHash, int? loserIndex, string? mergeText, CancellationToken ct) =>
        RunAsync(
            entryId,
            Refuse(SyncSetup.Refusal.NoConflictItem, NoConflictItemText),
            tx => CheckConflict(tx, entryId, op, shownHash, loserIndex, mergeText),
            op == ReviewOperation.Keep ? null : store => EmbedConflictText(store, entryId, op, shownHash, loserIndex, mergeText),
            (tx, facts, embedded) => op == ReviewOperation.Keep
                ? CloseConflict(tx, entryId, $"kept {entryId}: the memory keeps its text; the review is closed; nothing is pushed")
                : SetConflictText(tx, entryId, op, facts, embedded),
            ct);

    /// <summary>The live shown hash of a conflict item (D2): of the entry's text and the losers' texts in payload order.</summary>
    private static string ConflictLiveHash(string entryText, FSharpList<SyncConflict.Loser> losers) =>
        SyncConflict.shownHash(FSharpOption<string>.Some(entryText), ListModule.OfSeq(losers.Select(l => l.Text)));

    /// <summary>
    /// The text a <c>swap</c> or <c>merge</c> sets, or the refusal: <c>swap</c> takes loser <paramref name="loserIndex"/>
    /// (1-based, newest first; omitted with exactly one loser: 1; else <c>unknown-loser</c>), and a <c>remote</c> loser must
    /// be <see cref="Showable"/> (<c>unshowable-text</c>); <c>merge</c> takes <paramref name="mergeText"/>,
    /// which must not be empty or whitespace only, must have at most 262144 UTF-8 bytes and no NUL (<c>bad-merge-text</c>).
    /// </summary>
    private static (string? Text, SetupResult? Refusal) ChosenText(
        ReviewOperation op, FSharpList<SyncConflict.Loser> losers, int? loserIndex, string? mergeText)
    {
        if (op == ReviewOperation.Swap)
        {
            var count = losers.Length;
            var index = loserIndex ?? (count == 1 ? 1 : 0);
            if (index < 1 || index > count)
            {
                return (null, Refuse(
                    SyncSetup.Refusal.UnknownLoser,
                    count == 1
                        ? "this review has one loser, number 1; nothing changed"
                        : $"name a loser by its number in the list, 1 (the newest) to {count}; nothing changed"));
            }
            var loser = losers[count - index];
            if (loser.Side.IsRemote && !Showable(loser.Text, null, null))
            {
                return (null, Refuse(
                    SyncSetup.Refusal.UnshowableText,
                    "the loser's text holds characters the list does not show, so it cannot be swapped in; nothing changed"));
            }
            return (loser.Text, null);
        }

        if (string.IsNullOrWhiteSpace(mergeText))
            return (null, Refuse(SyncSetup.Refusal.BadMergeText, "the merged text is empty; nothing changed"));
        if (mergeText.Contains('\0'))
            return (null, Refuse(SyncSetup.Refusal.BadMergeText, "the merged text holds a NUL character; nothing changed"));
        if (Encoding.UTF8.GetByteCount(mergeText) > MaxMergeTextBytes)
        {
            return (null, Refuse(
                SyncSetup.Refusal.BadMergeText, $"the merged text is over {MaxMergeTextBytes} UTF-8 bytes; nothing changed"));
        }
        return (mergeText, null);
    }

    /// <summary>
    /// The checks of a conflict answer inside its transaction (D6), in this order, each a refusal that changes nothing unless
    /// stated: a conflict item <c>open</c> or <c>unseen</c> (<c>no-conflict-item</c>); the shown hash present
    /// (<c>shown-hash-required</c>); the local entry read (absent: <c>stale-item</c>, the next sync closes the item, or while a
    /// held deletion of the id is open or approved, restore or accept it first; in two tiers or unreadable:
    /// <c>unreadable-entry</c>); for <c>swap</c> and <c>merge</c> the row <c>active</c> with a base (missing: <c>failed</c>,
    /// run a sync first; otherwise <c>failed</c>) and the base parsed (else <c>failed</c>); the shown hash equal to the live one (else <c>stale-item</c> with the item as
    /// it is now, and the item's update to the live hash and <c>unseen</c> is committed); the chosen text
    /// (<see cref="ChosenText"/>); for a sticky entry the chosen text within the pinned cap (0 or less: 500;
    /// <c>over-pin-cap</c>, worded by <see cref="PinnedTierLimits.ContentLimitMessage"/>).
    /// </summary>
    private CheckOutcome<ConflictFacts> CheckConflict(
        SqliteStoreTransaction tx, string entryId, ReviewOperation op, string? shownHash, int? loserIndex, string? mergeText)
    {
        static CheckOutcome<ConflictFacts> No(SetupResult refusal) => new(refusal, null);
        var conn = tx.Connection;
        var dbTx = tx.Transaction;

        var item = GitSyncReviews.ReadConflictItem(conn, entryId, dbTx);
        if (item is not { State: "open" or "unseen" })
            return No(Refuse(SyncSetup.Refusal.NoConflictItem, NoConflictItemText));

        if (string.IsNullOrEmpty(shownHash))
        {
            return No(Refuse(
                SyncSetup.Refusal.ShownHashRequired,
                "a conflict review is answered only with the hash that was shown; nothing changed"));
        }

        var read = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, entryId), () => tx.IsSticky(ContentType.Memory, entryId));
        if (read is LocalRead.Absent)
        {
            var held = GitSyncReviews.ReadHeldItem(conn, entryId, dbTx) is { State: "open" or "approved" };
            return No(Refuse(
                SyncSetup.Refusal.StaleItem,
                held
                    ? "the memory is deleted here and the deletion is held; nothing changed; restore or accept the held deletion first"
                    : "the memory is no longer here; nothing changed; the next sync closes the item"));
        }
        if (read is not LocalRead.Present entry)
        {
            return No(Refuse(
                SyncSetup.Refusal.UnreadableEntry,
                "the memory with this id is in more than one tier or cannot be read; nothing changed"));
        }

        GitSyncRow? row = null;
        SyncRecord? baseRecord = null;
        if (op != ReviewOperation.Keep)
        {
            try
            {
                row = GitSyncRows.Read(conn, entryId, dbTx);
            }
            catch (FormatException ex)
            {
                return No(Failed("the row cannot be read: " + SyncSql.OneLine(ex.Message)));
            }
            if (row is null)
                return No(Failed("this memory is not synced yet; run a sync first; nothing changed"));
            if (!row.State.IsActive || row.BaseJson is null)
                return No(Failed("the row no longer matches the item; nothing changed"));
            var parsed = ApplyPlanning.ParseBase(entryId, row.BaseJson);
            if (parsed.Parsed is null)
                return No(UnreadableBase(parsed));
            baseRecord = parsed.Parsed.Record;
        }

        var losers = GitSyncReviews.ReadConflictLosers(item.PayloadJson);
        var live = ConflictLiveHash(entry.View.Content.Text, losers);
        if (!string.Equals(shownHash, live, StringComparison.Ordinal))
        {
            GitSyncReviews.MarkConflictUnseen(conn, entryId, live, dbTx);
            return new CheckOutcome<ConflictFacts>(
                Refuse(
                    SyncSetup.Refusal.StaleItem,
                    $"what the item shows changed since it was shown; only the item was updated; now: unseen conflict {live}"),
                null,
                CommitRefusal: true);
        }

        string? text = null;
        if (op != ReviewOperation.Keep)
        {
            var (chosen, refusal) = ChosenText(op, losers, loserIndex, mergeText);
            if (refusal is not null)
                return No(refusal);
            text = chosen!;
            if (OverPinCap(entry, text) is { } over)
                return No(over);
        }

        return new CheckOutcome<ConflictFacts>(null, new ConflictFacts(item, losers, entry, row, baseRecord, text));
    }

    /// <summary>For a sticky entry, <c>over-pin-cap</c> when <paramref name="text"/> is over the pinned cap (0 or less: 500); else null.</summary>
    private SetupResult? OverPinCap(LocalRead.Present entry, string text)
    {
        if (!entry.View.Sticky)
            return null;
        var cap = env.PinnedMaxContentChars <= 0 ? PinnedTierLimits.DefaultMaxContentChars : env.PinnedMaxContentChars;
        return text.Length > cap ? Refuse(SyncSetup.Refusal.OverPinCap, PinnedTierLimits.ContentLimitMessage(cap, text.Length)) : null;
    }

    /// <summary>
    /// The embedding of a <c>swap</c> or <c>merge</c>, before the transaction: the chosen text, embedded with
    /// <see cref="Embedder"/>, as <c>Embedded(text, vector)</c>. Null, with no embedder created, when the item is not open or
    /// unseen, the hash is missing, the entry cannot be read, the row is missing, cannot be read, not active or without a
    /// base, the base does not parse, the hash is not the live one, the text cannot be chosen (<see cref="ChosenText"/>), is over the pinned cap of a
    /// sticky entry or equals the entry's text. The checks inside the transaction report every refusal.
    /// </summary>
    private Embedded? EmbedConflictText(
        SqliteStore store, string entryId, ReviewOperation op, string? shownHash, int? loserIndex, string? mergeText)
    {
        var conn = store.Connection;
        var item = GitSyncReviews.ReadConflictItem(conn, entryId);
        if (item is not { State: "open" or "unseen" } || string.IsNullOrEmpty(shownHash))
            return null;
        var read = LocalEntryReader.Read(t => store.Get(t, ContentType.Memory, entryId), () => store.IsSticky(ContentType.Memory, entryId));
        if (read is not LocalRead.Present entry)
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
        if (row is null || !row.State.IsActive || row.BaseJson is null || ApplyPlanning.ParseBase(entryId, row.BaseJson).Parsed is null)
            return null;
        var losers = GitSyncReviews.ReadConflictLosers(item.PayloadJson);
        if (!string.Equals(shownHash, ConflictLiveHash(entry.View.Content.Text, losers), StringComparison.Ordinal))
            return null;
        var (text, refusal) = ChosenText(op, losers, loserIndex, mergeText);
        if (refusal is not null || text is null || OverPinCap(entry, text) is not null
            || string.Equals(text, entry.View.Content.Text, StringComparison.Ordinal))
            return null;
        return new Embedded(text, Embedder().Embed(text));
    }

    /// <summary>Removes the conflict item (the <c>resolve</c> step) and returns <paramref name="line"/>.</summary>
    private SetupResult CloseConflict(SqliteStoreTransaction tx, string entryId, string line)
    {
        BeforeStep?.Invoke("resolve");
        GitSyncReviews.DeleteConflictItem(tx.Connection, entryId, tx.Transaction);
        return SetupResult.Done(line);
    }

    /// <summary>
    /// D6, <c>swap</c> and <c>merge</c>: a chosen text equal to the entry's only removes the item. Otherwise the text set by
    /// <see cref="ApplyPlanning.ApplyText"/> with the vector computed before the transaction (none, or of another text:
    /// <c>stale-item</c>, nothing written), a content stamp issued above the greater of the base's content stamp and the
    /// row's pending one (none to issue: <c>failed</c>, nothing committed) and left pending, the row's comparison columns
    /// from the stored entry (a pending tier stamp the hot limit beat dropped), and the item removed. The lines carry the
    /// revert warning (where the memory is pinned on another machine the text is held there as an edit).
    /// </summary>
    private SetupResult SetConflictText(SqliteStoreTransaction tx, string entryId, ReviewOperation op, ConflictFacts facts, Embedded? embedded)
    {
        var verb = op == ReviewOperation.Swap ? "swapped" : "merged";
        var text = facts.Text ?? throw new InvalidOperationException("a swap or merge needs a text");
        var current = facts.Entry;
        if (string.Equals(text, current.View.Content.Text, StringComparison.Ordinal))
        {
            return CloseConflict(
                tx, entryId, $"{verb} {entryId}: the memory already has this text; the review is closed; nothing is pushed");
        }
        if (embedded is null || !string.Equals(embedded.BaseJson, text, StringComparison.Ordinal))
            return Refuse(SyncSetup.Refusal.StaleItem, "the memory changed while the text was prepared; nothing changed");

        var row = facts.Row ?? throw new InvalidOperationException("a swap or merge needs the row");
        var baseRecord = facts.Base ?? throw new InvalidOperationException("a swap or merge needs the parsed base");

        var conn = tx.Connection;
        var dbTx = tx.Transaction;
        var floor = row.ContentStamp is { } pending && SyncStamp.StampModule.compare(pending, baseRecord.ContentStamp) > 0
            ? pending
            : baseRecord.ContentStamp;
        var stamp = ApplyPlanning.IssueStamp(conn, dbTx, NowMs(), FSharpOption<SyncStamp.Stamp>.Some(floor));
        if (stamp is null)
            return Failed(NoStampText); // not committed: a clock the issue moved is rolled back

        tx.BeforeStep = BeforeStep;
        var applied = ApplyPlanning.ApplyText(tx, entryId, current, text, embedded.Vector, env.HotMaxContentChars);
        var appliedColder = !applied.Tier.Equals(current.Tier);
        BeforeStep?.Invoke("row");
        GitSyncRows.Write(
            conn,
            ApplyPlanning.DropBeatenTierStamp(
                ApplyPlanning.WithComparison(row, applied) with { ContentStamp = stamp.Value }, applied.Tier, baseRecord, appliedColder),
            dbTx);
        BeforeStep?.Invoke("resolve");
        GitSyncReviews.DeleteConflictItem(conn, entryId, dbTx);
        var what = op == ReviewOperation.Swap ? "the chosen text" : "the merged text";
        return SetupResult.Done($"{verb} {entryId}: {what} is now the memory's text here; the next sync pushes it", RevertWarning);
    }
}
