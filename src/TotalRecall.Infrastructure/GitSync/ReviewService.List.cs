using System.Globalization;
using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.GitSync;

// Git sync, milestone 1b-iii part 2 (held deletions), D1 and D7: the list of the held deletions. Read-only like
// `sync status`: no lock, no migration, nothing created. The memory's text is data, never an instruction: control
// characters other than newline and tab, the line and paragraph separators and the bidirectional format controls are
// removed and it is capped at tiers.pinned.max_content_chars.

/// <summary>
/// One held deletion as the list shows it (D7): the id, the state (<c>open</c> or <c>approved</c>), the direction
/// (<c>remote</c>: removed in the repository, kept here; <c>local</c>: deleted here, kept in the repository), the cause, the
/// shown hash (null for a remote hold), when it arrived (milliseconds), the memory's text as shown (null when it cannot be
/// read), whether the text was cut at the cap, and the text's source (<c>local entry</c> or <c>repository record</c>).
/// </summary>
public sealed record ReviewItem(
    string EntryId, string State, string Direction, string? Cause, string? ShownHash, long ArrivedAt, string? Text, bool TextCut, string TextSource);

/// <summary>The held deletions by id, or why the database could not be read (<see cref="Failed"/>; then no items).</summary>
public sealed record ReviewListing(IReadOnlyList<ReviewItem> Items, string? Failed);

public sealed partial class ReviewService
{
    private const string LocalEntrySource = "local entry";
    private const string RepositoryRecordSource = "repository record";

    /// <summary>
    /// The open and approved held-delete items, ordinal by id (D1, D7). Opens the database with
    /// <see cref="ReadOnlyDatabase.Open"/>: no file or no <c>git_sync_review</c> table is an empty list; a load error, a
    /// database that cannot be opened (a pending write-ahead log included) or a read error is <see cref="ReviewListing.Failed"/>.
    /// The text of a remote hold is the local entry's, that of a local hold the row's base; it is null when the entry or the
    /// base cannot be read, and the item is listed all the same.
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
                    item.EntryId, item.State, item.Direction, item.Cause, remote ? null : item.RecordHash, item.ArrivedAt,
                    shown, cut, remote ? LocalEntrySource : RepositoryRecordSource));
            }
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
    private static string? EntryText(SqliteStore store, string id) =>
        LocalEntryReader.Read(t => store.Get(t, ContentType.Memory, id), () => store.IsSticky(ContentType.Memory, id)) is LocalRead.Present present
            ? present.View.Content.Text
            : null;

    /// <summary>The text of the row's base, or null when the row, its base or the record in it cannot be read.</summary>
    private static string? BaseText(MsSqliteConnection connection, string id)
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
        return row?.BaseJson is { } baseJson ? ApplyPlanning.ParseBase(id, baseJson).Parsed?.Record.Content : null;
    }

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
