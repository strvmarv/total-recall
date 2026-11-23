using Microsoft.Data.Sqlite;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.GitSync;

// Git sync, milestone 1b-iii part 2 (held deletions), D1: the review service answers a held-delete item one at a time.
// An answer takes the sync lock (the setup service's 3 s wait), opens the database read-write and runs the migrations,
// then works in one immediate transaction that first re-reads the item, the row and the local entry and checks them in
// the spec's order; nothing is computed from a read taken before it except a restore's embedding, which the answer
// re-validates inside it. The service leaves work for the next sync only through the rows and items it writes.

/// <summary>
/// What the review service needs besides the setup environment: the embedder, created on first use (a restore of a
/// held local deletion only), the hot tier limit (0 or less: none) and the cap of a shown text
/// (<c>tiers.pinned.max_content_chars</c>; 0 or less: the pinned default).
/// </summary>
public sealed record ReviewEnvironment(SyncSetupEnvironment Setup, Func<IEmbedder> Embedder, int HotMaxContentChars, int PinnedMaxContentChars);

/// <summary>Which answer an item is checked for.</summary>
internal enum ReviewOperation { Accept, Restore }

/// <summary>What the checks read inside the answer's transaction: the item, its row (active, with a base) and the local entry.</summary>
internal sealed record ItemFacts(HeldItem Item, GitSyncRow Row, LocalRead Read);

/// <summary>An embedding computed before the transaction: the base it was computed from and the vector.</summary>
internal sealed record Embedded(string BaseJson, float[] Vector);

/// <summary>
/// Answers the held deletions of this machine (D1): <see cref="List"/> reads them; an answer runs under the sync lock in
/// one immediate transaction whose checks refuse with <c>no-held-item</c>, <c>failed</c>, <c>unreadable-entry</c>,
/// <c>stale-item</c> or <c>shown-hash-required</c> and change nothing. Disposing it disposes the embedder it created.
/// </summary>
public sealed partial class ReviewService(ReviewEnvironment env) : IDisposable
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(3);

    private const string BusyLockText = "another total-recall process is syncing this database: try again in a moment";
    private const string BusyDatabaseText = "the database is busy: try again in a moment";
    private const string NoHeldItemText = "no held deletion is open for this id; nothing changed";

    private IEmbedder? _embedder;

    /// <summary>Test seam: called with the answer's own connection right after it is opened, before the migrations.</summary>
    internal Action<MsSqliteConnection>? AfterOpen { get; set; }

    /// <summary>Test seam: called with the id after the embedding, before <c>BEGIN IMMEDIATE</c> (a second connection edits here).</summary>
    internal Action<string>? BeforeTransaction { get; set; }

    /// <summary>How many times the embedder was created (at most once).</summary>
    internal int EmbedderCreations { get; private set; }

    /// <summary>The embedder, created on first use and kept for the life of the service.</summary>
    internal IEmbedder Embedder()
    {
        if (_embedder is null)
        {
            _embedder = env.Embedder();
            EmbedderCreations++;
        }
        return _embedder;
    }

    public void Dispose()
    {
        if (_embedder is IDisposable disposable)
            disposable.Dispose();
        _embedder = null;
    }

    private long NowMs() => env.Setup.Clock().ToUnixTimeMilliseconds();

    private static SetupResult Refuse(SyncSetup.Refusal refusal, string text) => SetupResult.Refused(refusal, text);

    private static SetupResult Failed(string text) => SetupResult.Refused(SyncSetup.Refusal.Failed, text);

    /// <summary>Thrown by a <c>work</c> to leave the transaction without a commit and return <see cref="Result"/> as it is.</summary>
    private sealed class CheckedRollback(SetupResult result) : Exception("the checks passed; the transaction is rolled back")
    {
        public SetupResult Result { get; } = result;
    }

    /// <summary>The test entry point: every check of an answer, in a transaction that is always rolled back; <c>Done("checked")</c> when they all pass.</summary>
    internal Task<SetupResult> CheckForTestsAsync(string entryId, ReviewOperation op, string? shownHash, CancellationToken ct = default) =>
        RunAsync(entryId, op, shownHash, embed: null, (_, _, _) => throw new CheckedRollback(SetupResult.Done("checked")), ct);

    /// <summary>
    /// One answer (D1): a load error is <c>failed</c>; no database file is <c>no-held-item</c> before anything is created;
    /// then the sync directory is created when missing, the sync lock taken (<c>busy</c>), the legacy check run, the
    /// database opened read-write (<see cref="AfterOpen"/>) and migrated, <paramref name="embed"/> run on the store (an
    /// exception is <c>failed</c> and nothing is written), <see cref="BeforeTransaction"/> called, and <c>BEGIN IMMEDIATE</c>
    /// begun (a busy database is <c>busy</c>). Inside it <see cref="CheckItem"/> runs, then <paramref name="work"/>; the
    /// transaction is committed only when <paramref name="work"/> returns exit code 0. A <see cref="CheckedRollback"/> thrown by
    /// <paramref name="work"/> rolls the transaction back and its result is returned.
    /// </summary>
    private async Task<SetupResult> RunAsync(
        string entryId,
        ReviewOperation op,
        string? shownHash,
        Func<SqliteStore, Embedded?>? embed,
        Func<SqliteStoreTransaction, ItemFacts, Embedded?, SetupResult> work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entryId);
        var setup = env.Setup;
        var phase = "reading the settings";
        try
        {
            if (setup.LoadError is not null)
                return Failed(setup.LoadError);
            if (!File.Exists(setup.DatabasePath))
                return Refuse(SyncSetup.Refusal.NoHeldItem, NoHeldItemText);

            // a missing sync directory is created, not a refusal: no-held-item comes only from the item read
            phase = "taking the sync lock";
            var paths = GitSyncPaths.ForDatabase(setup.DatabasePath, setup.CaseInsensitive);
            paths.EnsureCreated();
            using var handle = await SyncLock.TryAcquireAsync(paths.LockPath, LockWait, ct).ConfigureAwait(false);
            if (handle is null)
                return Refuse(SyncSetup.Refusal.Busy, BusyLockText);

            phase = "checking the database";
            var legacy = LegacyDatabaseProbe.Check(setup.DatabasePath);
            if (legacy is not null)
                return Failed(legacy);

            try
            {
                phase = "opening the database";
                using var connection = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(setup.DatabasePath, pooling: false);
                AfterOpen?.Invoke(connection);
                MigrationRunner.RunMigrations(connection);
                var store = new SqliteStore(connection); // borrowed: it does not own the connection and runs no orphan sweep

                Embedded? embedded = null;
                if (embed is not null)
                {
                    phase = "computing the embedding";
                    try
                    {
                        embedded = embed(store);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        return Failed("the embedder failed: " + SyncSql.OneLine(ex.Message));
                    }
                }

                BeforeTransaction?.Invoke(entryId);
                phase = "answering the item";
                using var tx = store.BeginImmediateTransaction();
                var refusal = CheckItem(tx, entryId, op, shownHash, out var facts);
                if (refusal is not null)
                    return refusal;
                SetupResult result;
                try
                {
                    result = work(tx, facts!, embedded);
                }
                catch (CheckedRollback rollback)
                {
                    return rollback.Result; // disposing the transaction without a commit rolls it back
                }
                if (result.ExitCode == 0)
                    tx.Commit();
                return result;
            }
            catch (SqliteException ex) when (SyncSql.IsBusy(ex))
            {
                return Refuse(SyncSetup.Refusal.Busy, BusyDatabaseText);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExceptionLogger.LogChain(Console.Error, $"sync review ({phase})", ex);
            return Failed($"{phase}: {ex.Message}");
        }
    }

    /// <summary>
    /// The checks of an answer inside its transaction (D1, D6), in this order, each a refusal that changes nothing: a
    /// held-delete item <c>open</c> or <c>approved</c> (<c>no-held-item</c>); its row <c>active</c> with a base (<c>failed</c>);
    /// the local entry read (in two tiers or unreadable: <c>unreadable-entry</c>); the entry present for a remote hold and
    /// absent for a local one (<c>stale-item</c>: the next sync closes the item); for a local hold the shown hash (none:
    /// <c>shown-hash-required</c>; another: <c>stale-item</c>, whose text ends with the item as it is now). A remote hold
    /// ignores the hash. Null, with <paramref name="facts"/> set, when every check passed.
    /// </summary>
    private static SetupResult? CheckItem(
        SqliteStoreTransaction tx, string entryId, ReviewOperation op, string? shownHash, out ItemFacts? facts)
    {
        facts = null;
        var conn = tx.Connection;
        var dbTx = tx.Transaction;

        var item = GitSyncReviews.ReadHeldItem(conn, entryId, dbTx);
        if (item is not { State: "open" or "approved" })
            return Refuse(SyncSetup.Refusal.NoHeldItem, NoHeldItemText);

        GitSyncRow? row;
        try
        {
            row = GitSyncRows.Read(conn, entryId, dbTx);
        }
        catch (FormatException ex)
        {
            return Failed("the row cannot be read: " + SyncSql.OneLine(ex.Message));
        }
        if (row is null || !row.State.IsActive || row.BaseJson is null)
            return Failed("the row no longer matches the item; nothing changed");

        var read = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, entryId), () => tx.IsSticky(ContentType.Memory, entryId));
        if (read is LocalRead.Duplicate or LocalRead.Unreadable)
        {
            return Refuse(
                SyncSetup.Refusal.UnreadableEntry,
                "the memory with this id is in more than one tier or cannot be read; nothing changed");
        }

        var remote = item.Direction == "remote";
        if (remote != read is LocalRead.Present)
            return Refuse(SyncSetup.Refusal.StaleItem, "the memory changed since the hold; the next sync closes the item");

        if (!remote)
        {
            if (string.IsNullOrEmpty(shownHash))
            {
                return Refuse(
                    SyncSetup.Refusal.ShownHashRequired,
                    "a held local deletion is answered only with the record hash that was shown; nothing changed");
            }
            if (!string.Equals(shownHash, item.RecordHash, StringComparison.Ordinal))
            {
                return Refuse(
                    SyncSetup.Refusal.StaleItem,
                    "the record changed since it was shown; nothing changed; now: "
                    + $"{item.State} {item.Direction} {item.Cause ?? "-"} {item.RecordHash ?? "-"}");
            }
        }

        facts = new ItemFacts(item, row, read);
        return null;
    }
}
