using System.Globalization;
using System.Text;
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
/// <c>held-delete</c>, <c>new-pin</c> or <c>pin-edit</c>. A held deletion: the state (<c>open</c> or <c>approved</c>), the
/// direction (<c>remote</c>: removed in the repository, kept here; <c>local</c>: deleted here, kept in the repository), the
/// cause, the shown hash (null for a remote hold), when it arrived (milliseconds), the memory's text as shown (null when it
/// cannot be read), whether the text was cut at the cap, and the text's source (<c>local entry</c> or
/// <c>repository record</c>); <paramref name="Machine"/>, <paramref name="Project"/>, <paramref name="Scope"/> and
/// <paramref name="CurrentText"/> are null. A pin item: the state (<c>open</c> or <c>unseen</c>), no direction or cause,
/// the shown hash computed live (null when what it shows cannot be read), the record's machine, the project and scope of
/// what it shows (each through <see cref="ReviewService.ShownField"/>), the text (a <c>new-pin</c>: the local entry's; a
/// <c>pin-edit</c>: the held text of the row's base, with <paramref name="CurrentText"/> the text pinned here).
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
    bool CurrentTextCut);

/// <summary>The review items by id, then kind, or why the database could not be read (<see cref="Failed"/>; then no items).</summary>
public sealed record ReviewListing(IReadOnlyList<ReviewItem> Items, string? Failed);

public sealed partial class ReviewService
{
    private const string LocalEntrySource = "local entry";
    private const string RepositoryRecordSource = "repository record";
    private const string HeldDeleteKind = "held-delete";

    /// <summary>The cap of a shown machine, project or scope (D8).</summary>
    private const int ShownFieldCap = 200;

    /// <summary>
    /// The open and approved held-delete items and the open and unseen pin items, ordinal by id, then kind (D1, D7; pin
    /// review D8). Opens the database with
    /// <see cref="ReadOnlyDatabase.Open"/>: no file or no <c>git_sync_review</c> table is an empty list; a load error, a
    /// database that cannot be opened (a pending write-ahead log included) or a read error is <see cref="ReviewListing.Failed"/>.
    /// The text of a remote hold is the local entry's, that of a local hold the row's base; it is null when the entry or the
    /// base cannot be read, and the item is listed all the same. A pin item is shown by <see cref="PinListItem"/>.
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
                    null, null, null, shown, cut, remote ? LocalEntrySource : RepositoryRecordSource, null, false));
            }
            foreach (var pin in GitSyncReviews.ReadPinItems(connection))
            {
                if (pin.State is "open" or "unseen")
                    items.Add(PinListItem(store, connection, pin));
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
            hash = record is null ? null : SyncPin.shownHash(record.Content, record.Project, record.Scope);
            if (entry is not null)
                (current, currentCut) = ShownText(entry.Content.Text, cap);
        }
        var (shown, cut) = text is null ? (null, false) : ShownText(text, cap);
        return new ReviewItem(
            pin.Kind, pin.EntryId, pin.State, null, null, hash, pin.ArrivedAt,
            ShownField(pin.Machine), ShownField(project), ShownField(scope), shown, cut, source, current, currentCut);
    }

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
