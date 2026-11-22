using Microsoft.Data.Sqlite;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.GitSync;

// Git sync secret block (#161, spec decision D7): `sync unblock <id>` allows the current fields of a memory the scan held
// back. Same environment, sync lock and refusals as `leave`; it opens the database read-write as `init` does. The checks
// run in one immediate transaction in a fixed order (the entry, then the row, then the key) and the release is local
// state of this machine only: `allowed_hash` is never synced.

public sealed partial class SyncSetupService
{
    /// <summary>Test seam: the secret detector run on the released content after the commit (decision D2's field set).</summary>
    internal Func<string?, IReadOnlyList<string>> SecretDetector { get; set; } = SensitiveContentScanner.ScanSecrets;

    /// <summary>Test seam: called with <c>unblock</c>'s own connection right after it is opened, before the migrations.</summary>
    internal Action<MsSqliteConnection>? AfterOpen { get; set; }

    private const string BusyDatabase = "the database is busy: try again in a moment";

    /// <summary>
    /// Releases a memory held back by a secret (decision D7): under the sync lock and in one immediate transaction, reads
    /// the entry (none: <c>unknown-entry</c>; in two tiers or unreadable: <c>unreadable-entry</c>), then its row (none, not
    /// <c>active</c>, or a reason other than <c>secret-detected</c>: <c>not-blocked</c>), then compares the current key with
    /// the row's <c>blocked_hash</c> (different: <c>changed-since-blocked</c>), and writes <c>allowed_hash</c> = the key with
    /// the mark cleared. Only after the commit it runs the detector on the content it read and prints the categories found,
    /// never a value; a detector that throws then still leaves the release done, with a line saying the categories could
    /// not be checked. A refusal writes nothing; a missing database (<c>unknown-entry</c>) or a missing sync directory (<c>not-blocked</c>: no cycle ever ran) is refused before anything is opened or created.
    /// </summary>
    public async Task<SetupResult> UnblockAsync(string entryId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entryId);
        try
        {
            _phase = "reading the settings";
            if (env.LoadError is not null)
                return Failed(env.LoadError);
            if (!File.Exists(env.DatabasePath))
                return Refuse(SyncSetup.Refusal.UnknownEntry, UnknownEntryText);

            var paths = GitSyncPaths.ForDatabase(env.DatabasePath, env.CaseInsensitive);
            if (!Directory.Exists(paths.SyncDirectory))
                return Refuse(SyncSetup.Refusal.NotBlocked, NoSyncDirectoryText); // no cycle ever ran: no row can be secret-detected

            _phase = "taking the sync lock";
            paths.EnsureCreated();
            using var handle = await SyncLock.TryAcquireAsync(paths.LockPath, LockWait, ct).ConfigureAwait(false);
            if (handle is null)
                return Refuse(SyncSetup.Refusal.Busy, "another total-recall process is syncing this database: try again in a moment");

            _phase = "checking the database";
            var legacy = LegacyDatabaseProbe.Check(env.DatabasePath);
            if (legacy is not null)
                return Failed(legacy);

            SyncMerge.LocalEntry released;
            try
            {
                _phase = "opening the database";
                using var connection = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(env.DatabasePath, pooling: false);
                AfterOpen?.Invoke(connection);
                MigrationRunner.RunMigrations(connection);
                var store = new SqliteStore(connection); // borrowed: it does not own the connection and runs no orphan sweep

                _phase = "releasing the memory";
                using var tx = store.BeginImmediateTransaction();
                var (refusal, view) = ReleaseEntry(tx, entryId);
                if (refusal is not null)
                    return refusal;
                tx.Commit();
                released = view!;
            }
            catch (SqliteException ex) when (SyncSql.IsBusy(ex))
            {
                return Refuse(SyncSetup.Refusal.Busy, BusyDatabase);
            }

            _phase = "checking the released content";
            List<string> found;
            try
            {
                found = Categories(released);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // the release is committed: a detector failure must not turn it into one (no exception text, no value)
                return SetupResult.Done("unblocked " + entryId, "the categories could not be checked");
            }
            return SetupResult.Done(
                "unblocked " + entryId,
                found.Count == 0 ? "no secret was found in the current content" : "found: " + string.Join(", ", found));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return FailedFrom("unblock", ex);
        }
    }

    private const string UnknownEntryText = "no memory has this id";
    private const string NoSyncDirectoryText = "no sync has run on this database (there is no sync directory), so nothing is blocked; nothing changed";
    private const string NotBlockedText = "not blocked by a secret; nothing changed";

    /// <summary>The checks of decision D7 and the write, inside the caller's transaction; a refusal, or the entry that was released.</summary>
    private static (SetupResult? Refusal, SyncMerge.LocalEntry? Released) ReleaseEntry(SqliteStoreTransaction tx, string entryId)
    {
        var read = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, entryId), () => tx.IsSticky(ContentType.Memory, entryId));
        switch (read)
        {
            case LocalRead.Absent:
                return (Refuse(SyncSetup.Refusal.UnknownEntry, UnknownEntryText), null);
            case LocalRead.Duplicate or LocalRead.Unreadable:
                return (Refuse(
                    SyncSetup.Refusal.UnreadableEntry,
                    "the memory with this id is in more than one tier or cannot be read; nothing changed"), null);
        }
        var view = ((LocalRead.Present)read).View;

        var row = GitSyncRows.Read(tx.Connection, entryId, tx.Transaction);
        if (row is null || !row.State.IsActive || row.BlockedReason != SyncSecret.blockReason)
            return (Refuse(SyncSetup.Refusal.NotBlocked, NotBlockedText), null);

        var key = SyncSecret.secretKey(view.Content, view.Tags);
        if (row.BlockedHash != key)
        {
            return (Refuse(
                SyncSetup.Refusal.ChangedSinceBlocked,
                "changed since it was blocked; the next sync checks it again, then run this again if it is still blocked"), null);
        }

        // SyncSecret.afterAllowed on a secret-detected reason (the only one that gets here) is ClearBlock
        GitSyncRows.Write(tx.Connection, row with { AllowedHash = key, BlockedReason = null, BlockedHash = null }, tx.Transaction);
        return (null, view);
    }

    /// <summary>The detector's categories over the text, the project, the scope and every tag (decision D2), each once, in order.</summary>
    private List<string> Categories(SyncMerge.LocalEntry view)
    {
        var fields = new List<string?> { view.Content.Text, view.Content.Project?.Value, view.Content.Scope?.Value };
        fields.AddRange(view.Tags);
        var found = new List<string>();
        foreach (var field in fields)
        {
            if (field is null)
                continue;
            foreach (var category in SecretDetector(field))
                if (!found.Contains(category))
                    found.Add(category);
        }
        return found;
    }
}
