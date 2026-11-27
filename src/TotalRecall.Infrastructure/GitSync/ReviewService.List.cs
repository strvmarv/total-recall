using System.Globalization;
using System.Text;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using SyncRecord = TotalRecall.Core.SyncRecord.SyncRecord;

namespace TotalRecall.Infrastructure.GitSync;

// Git sync, milestone 1b-iii part 2 (held deletions), D1 and D7: the list of the held deletions. Read-only like
// `sync status`: no lock, no migration, nothing created. The memory's text is data, never an instruction: control
// characters other than newline and tab, the line and paragraph separators and the bidirectional format controls are
// removed and it is capped at tiers.pinned.max_content_chars. Pin review D8 adds the open and unseen pin items, whose
// machine, project and scope come from a repository record and are shown through the same rules (ShownField).

/// <summary>
/// One review item as the list shows it (D7 of the held deletions, D8 of pin review). <paramref name="Kind"/> is
/// <c>held-delete</c>, <c>new-pin</c>, <c>pin-edit</c> or <c>conflict</c>. A held deletion: the state (<c>open</c> or <c>approved</c>), the
/// direction (<c>remote</c>: removed in the repository, kept here; <c>local</c>: deleted here, kept in the repository), the
/// cause, the shown hash (null for a remote hold), when it arrived (milliseconds), the memory's text as shown (null when it
/// cannot be read), whether the text was cut at the cap, and the text's source (<c>local entry</c> or
/// <c>repository record</c>); <paramref name="Machine"/>, <paramref name="Project"/>, <paramref name="Scope"/> and
/// <paramref name="CurrentText"/> are null. A pin item: the state (<c>open</c> or <c>unseen</c>), no direction or cause,
/// the shown hash computed live (null when what it shows cannot be read), the record's machine, the project and scope of
/// what it shows (each through <see cref="ReviewService.ShownField"/>), the text (a <c>new-pin</c>: the local entry's; a
/// <c>pin-edit</c>: the held text of the row's base, with <paramref name="CurrentText"/> the text pinned here). A
/// <c>conflict</c> item (conflict review D7): the state (<c>open</c> or <c>unseen</c>), the live shown hash, the item's
/// machine, the applied text (the local entry's), and <paramref name="Losers"/>, the kept alternatives newest first; every
/// other kind has <see cref="NoLosers"/>.
/// </summary>
public sealed record ReviewItem(
    string Kind,
    string EntryId,
    string State,
    string? Direction,
    string? Cause,
    string? ShownHash,
    long ArrivedAt,
    string? Machine,
    string? Project,
    string? Scope,
    string? Text,
    bool TextCut,
    string TextSource,
    string? CurrentText,
    bool CurrentTextCut,
    IReadOnlyList<ConflictLoser> Losers)
{
    /// <summary>The <see cref="Losers"/> of every kind but <c>conflict</c>: one shared empty list, so items compare by value.</summary>
    public static readonly IReadOnlyList<ConflictLoser> NoLosers = Array.Empty<ConflictLoser>();
}

/// <summary>
/// One kept alternative of a <c>conflict</c> item as the list shows it (conflict review D7): its side (<c>local</c> or
/// <c>remote</c>), the machine and stamp text through <see cref="ReviewService.ShownField"/> (null when the item stores
/// none), and the text through <see cref="ReviewService.ShownText"/> at the pinned cap, with whether it was cut.
/// </summary>
public sealed record ConflictLoser(string Side, string? Machine, string? Stamp, string? Text, bool TextCut);

/// <summary>The review items by id, then kind, or why the database could not be read (<see cref="Failed"/>; then no items).</summary>
public sealed record ReviewListing(IReadOnlyList<ReviewItem> Items, string? Failed);

public sealed partial class ReviewService
{
    private const string LocalEntrySource = "local entry";
    private const string RepositoryRecordSource = "repository record";
    private const string HeldDeleteKind = "held-delete";
    private const string ConflictKind = "conflict";
    private const string HeldDeletionSource = "held deletion";

    /// <summary>The cap of a shown machine, project or scope (D8).</summary>
    private const int ShownFieldCap = 200;

    /// <summary>
    /// The open and approved held-delete items and the open and unseen pin and conflict items, ordinal by id, then kind (D1,
    /// D7; pin review D8; conflict review D7). Opens the database with
    /// <see cref="ReadOnlyDatabase.Open"/>: no file or no <c>git_sync_review</c> table is an empty list; a load error, a
    /// database that cannot be opened (a pending write-ahead log included) or a read error is <see cref="ReviewListing.Failed"/>.
    /// The text of a remote hold is the local entry's, that of a local hold the row's base; it is null when the entry or the
    /// base cannot be read, and the item is listed all the same. A pin item is shown by <see cref="PinListItem"/>, a conflict
    /// item by <see cref="ConflictListItem"/>.
    /// </summary>
    public ReviewListing List()
    {
        if (env.Setup.LoadError is not null)
            return new ReviewListing([], env.Setup.LoadError);
        var open = ReadOnlyDatabase.Open(env.Setup.DatabasePath);
        if (open.Unknown is not null)
            return new ReviewListing([], open.Unknown);
        if (open.Connection is null)
            return new ReviewListing([], null);
        using var connection = open.Connection;
        try
        {
            if (!HasReviewTable(connection))
                return new ReviewListing([], null);
            var store = new SqliteStore(connection); // borrowed, over the read-only connection: only reads are made
            var held = GitSyncReviews.ReadOpenHeld(connection).Values
                .Concat(GitSyncReviews.ReadApprovedHeld(connection).Values)
                .OrderBy(item => item.EntryId, StringComparer.Ordinal);
            var items = new List<ReviewItem>();
            foreach (var item in held)
            {
                var remote = item.Direction == "remote";
                var text = remote ? EntryText(store, item.EntryId) : BaseText(connection, item.EntryId);
                var (shown, cut) = text is null ? (null, false) : ShownText(text, env.PinnedMaxContentChars);
                items.Add(new ReviewItem(
                    HeldDeleteKind, item.EntryId, item.State, item.Direction, item.Cause, remote ? null : item.RecordHash, item.ArrivedAt,
                    null, null, null, shown, cut, remote ? LocalEntrySource : RepositoryRecordSource, null, false, ReviewItem.NoLosers));
            }
            foreach (var pin in GitSyncReviews.ReadPinItems(connection))
            {
                if (pin.State is "open" or "unseen")
                    items.Add(PinListItem(store, connection, pin));
            }
            foreach (var conflict in GitSyncReviews.ReadConflictItems(connection))
            {
                if (conflict.State is "open" or "unseen")
                    items.Add(ConflictListItem(store, connection, conflict));
            }
            items.Sort((a, b) =>
            {
                var byId = string.CompareOrdinal(a.EntryId, b.EntryId);
                return byId != 0 ? byId : string.CompareOrdinal(a.Kind, b.Kind);
            });
            return new ReviewListing(items, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ReviewListing([], ex.Message);
        }
    }

    private static bool HasReviewTable(MsSqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'git_sync_review'";
        return (long)cmd.ExecuteScalar()! > 0;
    }

    /// <summary>The local entry's text, or null when it is absent, in two tiers or unreadable.</summary>
    private static string? EntryText(SqliteStore store, string id) => EntryView(store, id)?.Content.Text;

    /// <summary>The local entry, or null when it is absent, in two tiers or unreadable.</summary>
    private static SyncMerge.LocalEntry? EntryView(SqliteStore store, string id) =>
        LocalEntryReader.Read(t => store.Get(t, ContentType.Memory, id), () => store.IsSticky(ContentType.Memory, id)) is LocalRead.Present present
            ? present.View
            : null;

    /// <summary>The text of the row's base, or null when the row, its base or the record in it cannot be read.</summary>
    private static string? BaseText(MsSqliteConnection connection, string id) => BaseRecord(connection, id)?.Content;

    /// <summary>The record in the row's base, or null when the row, its base or the record in it cannot be read.</summary>
    private static SyncRecord? BaseRecord(MsSqliteConnection connection, string id)
    {
        GitSyncRow? row;
        try
        {
            row = GitSyncRows.Read(connection, id);
        }
        catch (FormatException)
        {
            return null;
        }
        return row?.BaseJson is { } baseJson ? ApplyPlanning.ParseBase(id, baseJson).Parsed?.Record : null;
    }

    /// <summary>
    /// A pin item as the list shows it (pin review D8): a <c>new-pin</c> shows the local entry's text, project and scope
    /// (source <c>local entry</c>); a <c>pin-edit</c> the held text, project and scope of the row's base (source
    /// <c>repository record</c>) and, as <see cref="ReviewItem.CurrentText"/>, the text pinned here. The texts follow
    /// <see cref="ShownText"/> at the pinned cap; the machine, project and scope follow <see cref="ShownField"/>; the shown
    /// hash is <see cref="LiveHash"/> of the raw fields, null when what the item shows cannot be read.
    /// </summary>
    private ReviewItem PinListItem(SqliteStore store, MsSqliteConnection connection, PinItem pin)
    {
        var cap = env.PinnedMaxContentChars;
        var entry = EntryView(store, pin.EntryId);
        string? text, project, scope, hash;
        string? current = null;
        var currentCut = false;
        string source;
        if (pin.Kind == NewPinKind)
        {
            source = LocalEntrySource;
            text = entry?.Content.Text;
            project = OptText(entry?.Content.Project);
            scope = OptText(entry?.Content.Scope);
            hash = entry is null ? null : SyncPin.shownHash(entry.Content.Text, entry.Content.Project, entry.Content.Scope);
        }
        else
        {
            source = RepositoryRecordSource;
            var record = BaseRecord(connection, pin.EntryId);
            text = record?.Content;
            project = OptText(record?.Project);
            scope = OptText(record?.Scope);
            hash = record is null ? null : ApplyPlanning.BaseShownHash(record);
            if (entry is not null)
                (current, currentCut) = ShownText(entry.Content.Text, cap);
        }
        var (shown, cut) = text is null ? (null, false) : ShownText(text, cap);
        return new ReviewItem(
            pin.Kind, pin.EntryId, pin.State, null, null, hash, pin.ArrivedAt,
            ShownField(pin.Machine), ShownField(project), ShownField(scope), shown, cut, source, current, currentCut, ReviewItem.NoLosers);
    }

    /// <summary>
    /// A conflict item as the list shows it (conflict review D7): the applied text is the local entry's (source
    /// <c>local entry</c>, <see cref="ShownText"/> at the pinned cap); with the entry absent while a held deletion of the id is
    /// open or approved the text is null and the source <c>held deletion</c>; an entry absent otherwise, in two tiers or
    /// unreadable has a null text. The shown hash is <see cref="SyncConflict.shownHash"/> of the raw entry text (none when the
    /// entry is absent) and the payload's loser texts, null when the entry cannot be read. The losers are newest first; the
    /// item's machine and each loser's machine and stamp go through <see cref="ShownField"/>, an empty one being null.
    /// </summary>
    private ReviewItem ConflictListItem(SqliteStore store, MsSqliteConnection connection, ConflictItem item)
    {
        var cap = env.PinnedMaxContentChars;
        var losers = GitSyncReviews.ReadConflictLosers(item.PayloadJson);
        var texts = ListModule.OfSeq(losers.Select(l => l.Text));
        var read = LocalEntryReader.Read(
            t => store.Get(t, ContentType.Memory, item.EntryId), () => store.IsSticky(ContentType.Memory, item.EntryId));
        string? text = null;
        var cut = false;
        string? hash = null;
        var source = LocalEntrySource;
        if (read is LocalRead.Present present)
        {
            (text, cut) = ShownText(present.View.Content.Text, cap);
            hash = SyncConflict.shownHash(FSharpOption<string>.Some(present.View.Content.Text), texts);
        }
        else if (read is LocalRead.Absent)
        {
            hash = SyncConflict.shownHash(FSharpOption<string>.None, texts);
            if (GitSyncReviews.ReadHeldItem(connection, item.EntryId) is { State: "open" or "approved" })
                source = HeldDeletionSource;
        }
        var shownLosers = losers.Reverse().Select(l =>
        {
            var (loserText, loserCut) = ShownText(l.Text, cap);
            return new ConflictLoser(
                SyncConflict.sideName(l.Side), ShownField(NullIfEmpty(l.Machine)), ShownField(NullIfEmpty(l.Stamp)), loserText, loserCut);
        }).ToList();
        return new ReviewItem(
            ConflictKind, item.EntryId, item.State, null, null, hash, item.ArrivedAt, ShownField(NullIfEmpty(item.Machine)), null, null,
            text, cut, source, null, false, shownLosers);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string? OptText(FSharpOption<string>? option) => option is null ? null : option.Value;

    /// <summary>
    /// A machine, project or scope as the list shows it (pin review D8): the <see cref="ShownText"/> rules, then each
    /// newline and tab replaced by a space, cut at 200 UTF-16 units without splitting a surrogate pair; null stays null.
    /// </summary>
    internal static string? ShownField(string? value)
    {
        if (value is null)
            return null;
        var (text, _) = ShownText(value, ShownFieldCap);
        return text.Replace('\n', ' ').Replace('\t', ' ');
    }

    /// <summary>
    /// Whether the list shows <paramref name="text"/>, <paramref name="project"/> and <paramref name="scope"/> unchanged
    /// (pin review D8): the text survives <see cref="ShownText"/> uncut and the project and scope survive
    /// <see cref="ShownField"/>. <c>approve</c> refuses what is not (<c>unshowable-text</c>).
    /// </summary>
    internal static bool Showable(string text, string? project, string? scope) =>
        string.Equals(ShownText(text, int.MaxValue).Text, text, StringComparison.Ordinal)
        && string.Equals(ShownField(project), project, StringComparison.Ordinal)
        && string.Equals(ShownField(scope), scope, StringComparison.Ordinal);

    /// <summary>
    /// The text as the list shows it (D7): every control character other than newline and tab, the line and paragraph
    /// separators (U+2028, U+2029) and the bidirectional format controls (U+202A to U+202E, U+2066 to U+2069) removed,
    /// then cut at <paramref name="cap"/> UTF-16 units (0 or less: <see cref="PinnedTierLimits.DefaultMaxContentChars"/>), one fewer
    /// when the last kept unit would be the first half of a surrogate pair, so the text never ends in a lone surrogate.
    /// </summary>
    internal static (string Text, bool Cut) ShownText(string text, int cap)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (cap <= 0)
            cap = PinnedTierLimits.DefaultMaxContentChars;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (KeptInShownText(c))
                sb.Append(c);
        }
        if (sb.Length <= cap)
            return (sb.ToString(), false);
        var length = char.IsHighSurrogate(sb[cap - 1]) ? cap - 1 : cap;
        return (sb.ToString(0, length), true);
    }

    /// <summary>
    /// Whether <see cref="ShownText"/> keeps <paramref name="c"/>: newline and tab, and any character that is not a control
    /// character, a line or paragraph separator, or a bidirectional embedding, override or isolate control.
    /// </summary>
    private static bool KeptInShownText(char c) =>
        c is '\n' or '\t'
        || !char.IsControl(c)
        && CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
        && c is not ((>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069'));
}
