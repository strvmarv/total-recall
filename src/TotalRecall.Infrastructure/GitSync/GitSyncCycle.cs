using TotalRecall.Core;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// What a cycle needs from the configuration, as values (milestone 1b-i-4, decision 1). <paramref name="Address"/> must be
/// the remote the guard and the paths were built from. <paramref name="DeleteGuardPercent"/> is passed to the apply
/// (<see cref="ApplyOptions.DeleteGuardPercent"/>).
/// </summary>
public sealed record CycleSettings(
    string Address, string Branch, string MachineLabel, int HotMaxContentChars, TimeSpan LockWait, int DeleteGuardPercent = 20);

/// <summary>
/// What a finished cycle did, as plain data (decision 2): the parts' summaries of the last attempt, the push's outcome
/// or null with <paramref name="PushSkipped"/> set, how many pushes were made, whether a clone was made and where a
/// damaged clone went.
/// </summary>
public sealed record CycleSummary(
    int Attempts,
    ScanSummary Scan,
    FetchResult? Fetch,
    ApplySummary? Apply,
    PushOutcome? Push,
    string? PushSkipped,
    bool Cloned,
    string? SidelinedClone);

/// <summary>The one result of a cycle (decision 2). Never thrown: the state of the store, the repository, the file system and the network are all data.</summary>
public abstract record CycleOutcome
{
    private CycleOutcome() { }

    /// <summary>The cycle ran to its end. The push may not have run (<see cref="CycleSummary.PushSkipped"/>).</summary>
    public sealed record Done(CycleSummary Summary) : CycleOutcome;

    /// <summary>This machine has no sync setup to run (it never joined). Nothing is recorded.</summary>
    public sealed record NotJoined : CycleOutcome;

    /// <summary>A setup exists but this machine must not sync; <paramref name="Step"/> is the step that refused. Recorded as the last error.</summary>
    public sealed record Refused(string Reason, string Step) : CycleOutcome;

    /// <summary>Another process or task holds the sync lock. Nothing is recorded.</summary>
    public sealed record Busy : CycleOutcome;

    /// <summary>A step failed. <paramref name="Kind"/> is the git failure kind name where there is one, else a word of the failing part.</summary>
    public sealed record Failed(string Step, string Kind, string Message, SyncPush.Published Published) : CycleOutcome;

    /// <summary>The caller cancelled. Nothing is recorded.</summary>
    public sealed record Cancelled : CycleOutcome;
}

/// <summary>
/// The sync cycle (milestone 1b-i-4): takes the sync lock, checks that this machine may sync, then runs the scan, the
/// fetch, the apply and the push in the umbrella's order and returns one <see cref="CycleOutcome"/>. It never prints
/// (the one exception is the exception log line of decision 12) and never needs a person. It reads no configuration: the
/// caller passes <see cref="CycleSettings"/>. Nothing calls it until milestone 1b-iii has merged, because it calls the
/// push. One instance is meant to live as long as the process: it keeps the damaged-clone state (decision 9).
/// </summary>
public sealed partial class GitSyncCycle
{
    private readonly GitClient _client;
    private readonly SqliteStore _store;
    private readonly IEmbedder _embedder;
    private readonly GitSyncPaths _paths;
    private readonly CycleSettings _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<long> _nowMs;
    private readonly GitSyncFetcher _fetcher;

    public GitSyncCycle(
        GitClient client,
        RepoGuard guard,
        SqliteStore store,
        IEmbedder embedder,
        GitSyncPaths paths,
        CycleSettings settings,
        Func<DateTimeOffset> clock,
        Func<long> nowMs)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(guard);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));

        if (string.IsNullOrEmpty(settings.Address))
            throw new ArgumentException("a sync address is required: a machine with no remote has no cycle to build", nameof(settings));
        if (!string.Equals(settings.Address, guard.ConfiguredRemote, StringComparison.Ordinal))
            throw new ArgumentException("the address is not the remote the guard was built with", nameof(settings));
        string pathsClone;
        try
        {
            pathsClone = Path.GetFullPath(paths.CloneDirectory);
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException("these paths have no clone directory (build them with GitSyncPaths.For)", nameof(paths), ex);
        }
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(pathsClone, Path.GetFullPath(client.CloneDirectory), comparison))
            throw new ArgumentException("the paths' clone directory is not the client's", nameof(paths));

        _fetcher = new GitSyncFetcher(client, guard, store, settings.Branch);
    }

    /// <summary>Test seam, and the hook the later progress work will use: called with the name of each step as it begins.</summary>
    internal Action<string>? OnStep { get; set; }

    /// <summary>Test seam: called before each <c>_meta</c> write of the recording, with the key and the value (null: a delete).</summary>
    internal Action<string, string?>? BeforeMetaWrite { get; set; }

    /// <summary>
    /// Runs one cycle. Under the lock, in order: the identity check, the removal of stale lock files, the scan, the clone
    /// check, the fetch, the apply, the push gate and the push (decisions 3 to 6), then the recording (decision 10).
    /// </summary>
    public async Task<CycleOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        if (StoredCloneFailure() is { } stored)
            return stored;
        if (cancellationToken.IsCancellationRequested)
            return new CycleOutcome.Cancelled();

        SyncLockHandle? handle;
        try
        {
            OnStep?.Invoke("lock");
            handle = await SyncLock.TryAcquireAsync(_paths.LockPath, _settings.LockWait, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CycleOutcome.Cancelled();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExceptionLogger.LogChain("[total-recall] sync cycle", ex);
            var failed = new CycleOutcome.Failed(SyncCycle.stepName(SyncCycle.Step.Lock), "Unexpected", ex.Message, SyncPush.Published.No);
            Record(failed); // best effort, without the lock
            return failed;
        }

        if (handle is null)
            return new CycleOutcome.Busy();

        using (handle)
        {
            var outcome = await RunLockedAsync(cancellationToken).ConfigureAwait(false);
            Record(outcome);
            return outcome;
        }
    }

    // ---- recording (decision 10) ----

    /// <summary>
    /// <c>Done</c> deletes <c>git_sync_last_error</c> first and sets <c>git_sync_last_ok</c> second; <c>Failed</c> and
    /// <c>Refused</c> write the error line and leave <c>last_ok</c>; the other outcomes write nothing. A failure to write
    /// is swallowed.
    /// </summary>
    private void Record(CycleOutcome outcome)
    {
        if (outcome is not (CycleOutcome.Done or CycleOutcome.Failed or CycleOutcome.Refused))
            return;
        try
        {
            OnStep?.Invoke("record");
            switch (outcome)
            {
                case CycleOutcome.Done:
                    WriteMeta(SyncIdentity.MetaKeys.LastError, null);
                    WriteMeta(SyncIdentity.MetaKeys.LastOk, SyncCycle.okValue(_clock()));
                    break;
                case CycleOutcome.Failed failed:
                    WriteMeta(SyncIdentity.MetaKeys.LastError, SyncCycle.errorLine(_clock(), failed.Step, failed.Kind, failed.Message));
                    break;
                case CycleOutcome.Refused refused:
                    WriteMeta(SyncIdentity.MetaKeys.LastError, SyncCycle.errorLine(_clock(), refused.Step, "Refused", refused.Reason));
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // recording never changes the outcome and never throws
        }
    }

    private void WriteMeta(string key, string? value)
    {
        BeforeMetaWrite?.Invoke(key, value);
        if (value is null)
            _store.DeleteMeta(key);
        else
            _store.SetMeta(key, value);
    }
}
