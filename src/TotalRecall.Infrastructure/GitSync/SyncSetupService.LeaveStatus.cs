using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.GitSync;

public sealed partial class SyncSetupService
{
    // ---- leave (decision 7) -----------------------------------------------------------------------------------

    /// <summary>
    /// Turns git mode off: writes <c>storage.mode = "local"</c> and nothing else. Its paths come from the database path
    /// alone, so it works when the remote is missing or unusable and with <c>git</c> missing. It reads no repository.
    /// </summary>
    public async Task<SetupResult> LeaveAsync(CancellationToken ct = default)
    {
        try
        {
            _phase = "reading the settings";
            if (env.LoadError is not null)
                return Failed(env.LoadError);
            if (env.EffectiveMode != "git")
                return SetupResult.Done("this machine is not in git mode; nothing changed");

            _phase = "taking the sync lock";
            var paths = GitSyncPaths.ForDatabase(env.DatabasePath, env.CaseInsensitive);
            paths.EnsureCreated();
            using var handle = await SyncLock.TryAcquireAsync(paths.LockPath, LockWait, ct).ConfigureAwait(false);
            if (handle is null)
                return Refuse(SyncSetup.Refusal.Busy, "another total-recall process is syncing this database: try again in a moment");

            WriteConfig([("storage.mode", "local")]);
            return SetupResult.Done(
                "storage.mode is now \"local\"",
                "your memories, this machine's sync state and the repository are untouched; [git_sync] is kept,",
                "so `sync join <address>` later restores git mode");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return FailedFrom("leave", ex);
        }
    }

    // ---- status (decision 8) ----------------------------------------------------------------------------------

    /// <summary>
    /// What this machine knows about its sync setup. Read-only: no network, no lock, nothing created (a database that
    /// does not exist is opened by no one). Exit 0 whatever it finds; a fact it cannot read prints as
    /// <c>unknown (reason)</c> and the rest carries on.
    /// </summary>
    public async Task<SetupResult> StatusAsync(CancellationToken ct = default)
    {
        var loadError = env.LoadError;
        var remote = FSharpOption<string>.get_IsSome(env.Settings.Remote) ? env.Settings.Remote.Value : null;

        var mode = loadError is null ? env.EffectiveMode : $"unknown ({loadError})";
        var configuredRemote = loadError is null ? Opt(remote) : SyncSetup.Fact.NewUnknown(loadError);
        var configuredBranch = loadError is null
            ? Opt(FSharpOption<string>.get_IsSome(env.Settings.Branch) ? env.Settings.Branch.Value : null)
            : SyncSetup.Fact.NewUnknown(loadError);
        SyncSetup.Fact storedRemote = Unknown(loadError), repoId = Unknown(loadError), machineId = Unknown(loadError);
        var host = loadError is null ? SyncSetup.HostCheck.NotRecorded : SyncSetup.HostCheck.NewHostUnknown(loadError);
        var blocked = loadError is null ? SyncSetup.BlockedFact.NoDatabase : SyncSetup.BlockedFact.NewBlockedUnreadable(loadError);
        var join = loadError is null ? "unknown" : $"unknown ({loadError})";
        var clone = loadError is null ? "unknown" : $"unknown ({loadError})";

        // git: asked even when the settings could not be read (over a path that is never used, so nothing is created)
        string git;
        GitSyncPaths? paths = null;
        GitClient? client = null;
        string? pathsError = null;
        try
        {
            try
            {
                paths = GitSyncPaths.For(
                    loadError is null ? env.DatabasePath : Path.Combine(Path.GetTempPath(), "tr-status-none", "x.db"),
                    remote ?? "none",
                    env.CaseInsensitive);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                pathsError = ex.Message;
                paths = GitSyncPaths.For(Path.Combine(Path.GetTempPath(), "tr-status-none", "x.db"), "none", false);
            }

            var executable = GitExecutable.TryFind(env.PathVariable);
            if (executable is null)
            {
                git = "not found";
            }
            else
            {
                client = new GitClient(env.Process, executable, paths, env.GitOptions);
                var version = await client.VersionAsync(ct).ConfigureAwait(false);
                git = version.IsOk
                    ? $"{version.Value.Major}.{version.Value.Minor}"
                    : version.Failure.Failure is { IsGitTooOld: true } ? "found, but older than 2.28" : "found, but it did not report a version";
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            git = $"unknown ({ex.Message})";
        }

        MsSqliteConnection? connection = null;
        try
        {
            if (loadError is null)
            {
                var open = ReadOnlyDatabase.Open(env.DatabasePath);
                connection = open.Connection;
                var meta = new ReadOnlyMeta(connection);
                if (open.Unknown is not null)
                {
                    storedRemote = repoId = machineId = SyncSetup.Fact.NewUnknown(open.Unknown);
                    host = SyncSetup.HostCheck.NewHostUnknown(open.Unknown);
                    blocked = SyncSetup.BlockedFact.NewBlockedUnreadable(open.Unknown);
                }
                else
                {
                    storedRemote = ReadFact(meta, SyncIdentity.MetaKeys.Remote);
                    repoId = ReadFact(meta, SyncIdentity.MetaKeys.RepoId);
                    machineId = ReadFact(meta, SyncIdentity.MetaKeys.Machine);
                    host = ReadHost(meta);
                    if (connection is not null)
                        blocked = ReadBlocked(connection);
                }

                (join, clone) = await JoinAndCloneAsync(open, meta, remote, pathsError is null ? paths : null, pathsError, client, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            connection?.Dispose();
        }

        return new SetupResult(
            0, null, null,
            SyncSetup.renderStatus(new SyncSetup.StatusFacts(mode, git, configuredRemote, configuredBranch, storedRemote, repoId, machineId, host, join, clone, blocked))
                .ToArray(),
            []);
    }

    internal const string NoSyncStateText = "the database has no git sync state: start total-recall once";

    /// <summary>
    /// The <c>blocked</c> section of <c>sync status</c> (#161, D8): the active sync rows that carry a reason, at most
    /// <see cref="SyncSetup.blockedShownLimit"/> of them ordinal by id, and how many there are, from one statement on the
    /// connection it is given (one snapshot; it writes nothing and takes no lock). The columns are read directly, never
    /// through <see cref="GitSyncRows"/>, so a row with a state this build does not know is simply not listed. No
    /// <c>git_sync_entry</c> table (an unmigrated database), or any read error, is <c>BlockedUnreadable</c>.
    /// </summary>
    internal static SyncSetup.BlockedFact ReadBlocked(MsSqliteConnection connection)
    {
        try
        {
            using (var probe = connection.CreateCommand())
            {
                probe.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'git_sync_entry'";
                if ((long)probe.ExecuteScalar()! == 0)
                    return SyncSetup.BlockedFact.NewBlockedUnreadable(NoSyncStateText);
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT entry_id, blocked_reason, count(*) OVER () FROM git_sync_entry
                WHERE state = 'active' AND blocked_reason IS NOT NULL
                ORDER BY entry_id COLLATE BINARY
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", SyncSetup.blockedShownLimit);
            var total = 0;
            var shown = new List<Tuple<string, string>>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    shown.Add(Tuple.Create(reader.GetString(0), reader.GetString(1)));
                    total = checked((int)reader.GetInt64(2));
                }
            }
            return SyncSetup.BlockedFact.NewListed(total, ListModule.OfSeq(shown));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return SyncSetup.BlockedFact.NewBlockedUnreadable(ex.Message);
        }
    }

    private static SyncSetup.Fact Opt(string? value) =>
        value is null ? SyncSetup.Fact.Missing : SyncSetup.Fact.NewKnown(value);

    private static SyncSetup.Fact Unknown(string? reason) =>
        reason is null ? SyncSetup.Fact.Missing : SyncSetup.Fact.NewUnknown(reason);

    private static SyncSetup.Fact ReadFact(IMetaStore meta, string key)
    {
        try
        {
            var value = meta.GetMeta(key);
            return string.IsNullOrEmpty(value) ? SyncSetup.Fact.Missing : SyncSetup.Fact.NewKnown(value);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return SyncSetup.Fact.NewUnknown(ex.Message);
        }
    }

    private SyncSetup.HostCheck ReadHost(IMetaStore meta)
    {
        try
        {
            var stored = meta.GetMeta(SyncIdentity.MetaKeys.Host);
            if (string.IsNullOrEmpty(stored))
                return SyncSetup.HostCheck.NotRecorded;
            var current = env.Host.Fingerprint;
            if (current is null)
                return SyncSetup.HostCheck.Unavailable;
            return string.Equals(stored, current, StringComparison.Ordinal) ? SyncSetup.HostCheck.Matches : SyncSetup.HostCheck.Differs;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return SyncSetup.HostCheck.NewHostUnknown(ex.Message);
        }
    }

    /// <summary>The join state (from the 1a-2 guard, local reads only) and whether a clone exists, each as one text.</summary>
    private async Task<(string Join, string Clone)> JoinAndCloneAsync(
        ReadOnlyOpen open, IMetaStore meta, string? remote, GitSyncPaths? paths, string? pathsError, GitClient? client, CancellationToken ct)
    {
        if (remote is null)
            return (JoinText(SyncIdentity.NotJoinedReason.NotConfigured), "no clone");

        var cloneText = pathsError is null ? "no clone" : $"unknown ({pathsError})";
        var helperFilesMissing = false;
        try
        {
            if (paths is not null)
            {
                var dotGit = Path.Combine(paths.CloneDirectory, ".git");
                if (Directory.Exists(dotGit) || File.Exists(dotGit))
                {
                    cloneText = $"clone at {paths.CloneDirectory}";
                    // reading the manifest runs git commands that would create these two files: status creates nothing
                    helperFilesMissing = !Directory.Exists(paths.NoHooksDirectory) || !File.Exists(paths.EmptyFile);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cloneText = $"unknown ({ex.Message})";
        }

        if (open.Unknown is not null)
            return ($"unknown ({open.Unknown})", cloneText);
        try
        {
            var guard = new RepoGuard(env.Settings, meta, helperFilesMissing ? null : client, env.Host, env.Probe, env.Clock);
            var verdict = await guard.EvaluateIdentityAsync(null, ct).ConfigureAwait(false);
            var join = verdict.Join.IsJoined ? "joined" : JoinText(((SyncIdentity.JoinState.NotJoined)verdict.Join).Item);
            var manifest = verdict.Manifest.Kind switch
            {
                ManifestReadKind.Ok => "manifest ok",
                ManifestReadKind.Newer => $"manifest written by a newer total-recall (format {verdict.Manifest.NewerVersion}): update total-recall",
                ManifestReadKind.Invalid => $"manifest invalid: {verdict.Manifest.Reason}",
                ManifestReadKind.Unreadable => $"manifest unreadable: {verdict.Manifest.Reason}",
                ManifestReadKind.NoManifest => "the clone has no manifest",
                _ => null,
            };
            if (helperFilesMissing)
                manifest = "manifest not read (the sync directory is incomplete: run a sync command once)";
            return (join, manifest is null ? cloneText : $"{cloneText}; {manifest}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return ($"unknown ({ex.Message})", cloneText);
        }
    }

    private static string JoinText(SyncIdentity.NotJoinedReason reason) => "not joined: " + SyncIdentity.reasonText(reason);
}
