using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// Everything the setup service needs from the outside world, passed in so a test drives a command against a real
/// bare repository and a real store without touching the environment (spec decision 1).
/// </summary>
/// <param name="DatabasePath">The database file (the same one every other command uses).</param>
/// <param name="CaseInsensitive">True on Windows and macOS, as <see cref="HostIdentity.ForCurrentMachine"/> decides it.</param>
/// <param name="Settings">The configured <c>[git_sync]</c> settings (the defaults when the table is absent).</param>
/// <param name="EffectiveMode">The storage mode the server would run in (<c>SyncSetup.effectiveMode</c>).</param>
/// <param name="PathVariable">The PATH to look for <c>git</c> on; null means the process's own.</param>
/// <param name="Report">Notices and git progress, as they happen (the commands write them to standard error).</param>
/// <param name="Fault">Test seam: called with <c>after-placement</c> and <c>after-database</c> between the record steps.</param>
/// <param name="Move">Test seam: the directory rename used for sidelining and for moving a clone; null means <see cref="Directory.Move"/>.</param>
/// <param name="LoadError">Set when the config or the database path could not be resolved; the verbs report it.</param>
public sealed record SyncSetupEnvironment(
    string DatabasePath,
    bool CaseInsensitive,
    Core.Config.GitSyncConfig Settings,
    string EffectiveMode,
    string? PathVariable,
    IGitProcess Process,
    GitClientOptions? GitOptions,
    IPublicRepoProbe Probe,
    IUserConfigFile Config,
    HostIdentity Host,
    Func<DateTimeOffset> Clock,
    Func<int, byte[]> Random,
    string AppVersion,
    Action<string> Report,
    Func<string, Task>? Fault,
    Action<string, string>? Move,
    string? LoadError);

/// <summary>What a setup command did: an exit code, the refusal if it stopped, the lines to print and the notes after a refusal.</summary>
public sealed record SetupResult(
    int ExitCode,
    SyncSetup.Refusal? Refusal,
    string? RefusalText,
    IReadOnlyList<string> Lines,
    IReadOnlyList<string> Notes)
{
    public static SetupResult Done(params string[] lines) => new(0, null, null, lines, []);

    public static SetupResult Refused(SyncSetup.Refusal refusal, string text, IReadOnlyList<string>? notes = null) =>
        new(1, refusal, text, [], notes ?? []);
}

internal enum RecordKind { Init, JoinDifferent, JoinSame }

/// <param name="Kind">Which record this is (decision 10).</param>
/// <param name="RepoId">The repository id to write (a fresh one for <c>init</c>, the manifest's for <c>join</c>).</param>
/// <param name="ReusedClone">True when a clone that was already there is being used (false for a fresh or a re-made one).</param>
/// <param name="SameRepository">True for the same repository (rows kept).</param>
internal sealed record RecordPlan(RecordKind Kind, string RepoId, bool ReusedClone, bool SameRepository);

/// <summary>What a precondition pass opened. It owns the lock and the connection; disposing it releases both.</summary>
internal sealed class Prepared : IDisposable
{
    public required string Address { get; init; }
    public required string Branch { get; init; }
    public required GitSyncPaths Paths { get; init; }
    public required GitClient Client { get; init; }
    public required RepoGuard Guard { get; init; }
    public required MsSqliteConnection Connection { get; init; }
    public required IMetaStore Meta { get; init; }
    public required SyncIdentity.StoredIdentity Stored { get; init; }
    /// <summary>The branch configured before this command (null when the configured one was refused).</summary>
    public required string? PreviousBranch { get; init; }
    public required string Fingerprint { get; init; }
    public required SyncLockHandle Lock { get; init; }

    public void Dispose()
    {
        try
        {
            Connection.Dispose();
        }
        finally
        {
            Lock.Dispose();
        }
    }
}

/// <summary>
/// The four setup commands as one service (spec decision 1): <c>init</c> starts a sync repository, <c>join</c> adopts one,
/// <c>leave</c> turns git mode off, <c>status</c> reads. The commands in the CLI only parse arguments and print.
/// </summary>
public sealed partial class SyncSetupService(SyncSetupEnvironment env)
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(3);
    private string _phase = "starting";

    /// <summary>The <c>note:</c> lines a refusal prints: what this run left behind (decision 11).</summary>
    private readonly List<string> _notes = [];

    /// <summary>
    /// The notes of a successful run that the record step did not produce: where a clone that was moved aside went. The
    /// clone now in use is not "kept" (it is the one the machine uses), so that note is left out.
    /// </summary>
    private IEnumerable<string> SidelineNotes(IEnumerable<string> already) =>
        _notes.Distinct().Where(n => !n.StartsWith("note: a clone was kept at", StringComparison.Ordinal) && !already.Contains(n));

    /// <summary>A refusal carries the notes of what the run changed before it stopped (each once).</summary>
    private SetupResult WithNotes(SetupResult result) =>
        result.ExitCode == 0 ? result : result with { Notes = [.. result.Notes.Concat(_notes).Distinct()] };

    // ---- result helpers ---------------------------------------------------------------------------------------

    private static SetupResult Refuse(SyncSetup.Refusal refusal, string text, params string[] notes) =>
        SetupResult.Refused(refusal, text, notes);

    private static SetupResult Failed(string text, params string[] notes) =>
        SetupResult.Refused(SyncSetup.Refusal.Failed, text, notes);

    private SetupResult FailedFrom(string verb, Exception ex)
    {
        ExceptionLogger.LogChain(Console.Error, $"sync {verb} ({_phase})", ex);
        return Failed($"{_phase}: {ex.Message}");
    }

    // ---- the three keys init and join write -------------------------------------------------------------------

    internal static IReadOnlyList<(string Key, object Value)> ConfigKeys(string address, string branch) =>
    [
        ("storage.mode", "git"),
        ("git_sync.remote", address),
        ("git_sync.branch", branch),
    ];

    // ---- the address and the branch (decision 4.1) ------------------------------------------------------------

    /// <summary>
    /// Checks the address and the branch without any side effect. Null when both are fine, with the branch to use.
    /// The texts never hold the address.
    /// </summary>
    internal static SyncSetup.Refusal? CheckAddress(
        string address, string? branchOverride, Core.Config.GitSyncConfig settings, bool windows, out string text, out string branch)
    {
        text = "";
        branch = "";
        var usable = SyncGit.validateRemote(windows, address);
        if (usable.IsError)
        {
            text = usable.ErrorValue;
            return SyncSetup.Refusal.BadRemote;
        }
        var credential = SyncSetup.addressRefusal(address);
        if (FSharpOption<string>.get_IsSome(credential))
        {
            text = credential.Value;
            return SyncSetup.Refusal.BadRemote;
        }

        if (branchOverride is not null)
        {
            if (!Core.Config.GitSyncSettings.isUsableBranch(branchOverride))
            {
                text = "not a usable branch name";
                return SyncSetup.Refusal.BadBranch;
            }
            branch = branchOverride;
            return null;
        }
        if (!FSharpOption<string>.get_IsSome(settings.Branch))
        {
            text = "the configured git_sync.branch is not a usable branch name: give one with --branch";
            return SyncSetup.Refusal.BadBranch;
        }
        branch = settings.Branch.Value;
        return null;
    }

    // ---- the preconditions (decision 4) -----------------------------------------------------------------------

    /// <summary>The test entry point: the preconditions, returning what they opened (the caller disposes it) or the refusal.</summary>
    internal async Task<(Prepared? Prepared, SetupResult? Refusal)> PrepareForTestsAsync(
        string address, string? branchOverride, CancellationToken ct) =>
        await PrepareAsync(address, branchOverride, ct).ConfigureAwait(false);

    private async Task<(Prepared? Prepared, SetupResult? Refusal)> PrepareAsync(
        string address, string? branchOverride, CancellationToken ct)
    {
        _phase = "reading the settings";
        if (env.LoadError is not null)
            return (null, Failed(env.LoadError));

        // 4.1 the address, the branch and the config text it would write
        _phase = "checking the address";
        var addressRefusal = CheckAddress(address, branchOverride, env.Settings, OperatingSystem.IsWindows(), out var addressText, out var branch);
        if (addressRefusal is not null)
            return (null, Refuse(addressRefusal, addressText));
        _phase = "rendering the config";
        env.Config.Render(ConfigKeys(address, branch)); // a value the TOML writer cannot write fails now, before any network call

        // 4.2 the storage mode
        _phase = "checking the storage mode";
        var verdict = SyncSetup.modeVerdict(env.EffectiveMode);
        if (verdict.IsError)
            return (null, Refuse(SyncSetup.Refusal.WrongMode, verdict.ErrorValue));

        // 4.3 git
        _phase = "looking for git";
        var executable = GitExecutable.TryFind(env.PathVariable);
        if (executable is null)
            return (null, Refuse(SyncSetup.Refusal.NoGit, "git was not found on the PATH: install git 2.28 or newer"));
        var paths = GitSyncPaths.For(env.DatabasePath, address, env.CaseInsensitive);
        var client = new GitClient(env.Process, executable, paths, env.GitOptions);
        var version = await client.VersionAsync(ct).ConfigureAwait(false);
        if (!version.IsOk)
        {
            var kind = version.Failure.Failure;
            if (kind is { IsGitTooOld: true })
                return (null, Refuse(SyncSetup.Refusal.GitTooOld, "git is older than 2.28: update git"));
            if (kind is { IsNoGit: true })
                return (null, Refuse(SyncSetup.Refusal.NoGit, "git could not be started: install git 2.28 or newer"));
            return (null, Failed("git did not report a usable version"));
        }

        // 4.4 the host fingerprint
        _phase = "identifying this machine";
        var fingerprint = env.Host.Fingerprint;
        if (fingerprint is null)
            return (null, Refuse(SyncSetup.Refusal.HostUnverified, "this machine's identity could not be determined, so it could never sync"));

        // 4.5 the lock, then the database
        _phase = "taking the sync lock";
        paths.EnsureCreated();
        var handle = await SyncLock.TryAcquireAsync(paths.LockPath, LockWait, ct).ConfigureAwait(false);
        if (handle is null)
            return (null, Refuse(SyncSetup.Refusal.Busy, "another total-recall process is syncing this database: try again in a moment"));

        MsSqliteConnection? connection = null;
        try
        {
            _phase = "checking the database";
            var legacy = LegacyDatabaseProbe.Check(env.DatabasePath);
            if (legacy is not null)
                return Release(handle, null, Failed(legacy));

            _phase = "opening the database";
            connection = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(env.DatabasePath, pooling: false);
            MigrationRunner.RunMigrations(connection);
            var store = new SqliteStore(connection); // borrowed: it does not own the connection and runs no orphan sweep
            var stored = new GitSyncIdentityStore(store).Read();

            // 4.6 the public check, on a guard for the address being set up
            _phase = "checking whether the repository is public";
            var guardSettings = new Core.Config.GitSyncConfig(
                FSharpOption<string>.Some(address),
                FSharpOption<string>.Some(branch),
                env.Settings.IntervalSeconds,
                env.Settings.SessionStart,
                env.Settings.SessionStartSoftSeconds,
                env.Settings.SessionStartMaxSeconds,
                env.Settings.SessionStartStallSeconds,
                env.Settings.SessionEnd,
                env.Settings.SessionEndSeconds,
                env.Settings.DeleteGuardPercent,
                env.Settings.Warnings);
            var guard = new RepoGuard(guardSettings, store, client, env.Host, env.Probe, env.Clock);
            var pub = await guard.CheckPublicAsync(ct).ConfigureAwait(false);
            if (pub.Notice is not null)
                env.Report(pub.Notice);
            if (pub.Verdict.IsPublic)
            {
                return Release(handle, connection, Refuse(
                    SyncSetup.Refusal.PublicRepo,
                    "this repository is public: memories must not be synced to a public repository. Make it private, or use another one"));
            }

            return (new Prepared
            {
                Address = address,
                Branch = branch,
                Paths = paths,
                Client = client,
                Guard = guard,
                Connection = connection,
                Meta = store,
                Stored = stored,
                PreviousBranch = FSharpOption<string>.get_IsSome(env.Settings.Branch) ? env.Settings.Branch.Value : null,
                Fingerprint = fingerprint,
                Lock = handle,
            }, null);
        }
        catch
        {
            connection?.Dispose();
            handle.Dispose();
            throw;
        }
    }

    private static (Prepared?, SetupResult?) Release(SyncLockHandle handle, MsSqliteConnection? connection, SetupResult result)
    {
        try
        {
            connection?.Dispose();
        }
        finally
        {
            handle.Dispose();
        }
        return (null, result);
    }

    // ---- the commands -----------------------------------------------------------------------------------------

    /// <summary>
    /// The config, last (decision 10.3): the text is rendered now from the file's current content, so a <c>config set</c>
    /// that ran meanwhile is kept, and written once.
    /// </summary>
    private void WriteConfig(IReadOnlyList<(string Key, object Value)> keys)
    {
        _phase = "writing config.toml";
        var text = env.Config.Render(keys);
        env.Config.Write(text);
    }
}
