using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>An entry the push listed with the reason it did not send it (or sent it with a caveat).</summary>
public sealed record PushItem(string Id, string Reason);

/// <summary>
/// What one push did, as plain data (decision 13). <paramref name="New"/> and <paramref name="Changed"/> count the
/// entries whose bytes changed (they are also the commit message's <c>+</c> and <c>~</c>); <paramref name="Unchanged"/>
/// the entries finalized with no change; <paramref name="Removed"/> the ids whose record the commit removes, ordinal
/// (milestone 1b-ii, D14: the commit message's <c>-</c>). It holds no text for the user.
/// </summary>
public sealed record PushSummary(
    string? Commit,
    int New,
    int Changed,
    int Unchanged,
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Removed,
    IReadOnlyList<PushItem> Blocked,
    IReadOnlyList<PushItem> Deferred,
    IReadOnlyList<PushItem> Skipped)
{
    public static PushSummary Empty { get; } = new(null, 0, 0, 0, [], [], [], [], []);
}

public abstract record PushOutcome
{
    private PushOutcome() { }

    /// <summary>A commit was made and pushed.</summary>
    public sealed record Pushed(PushSummary Summary) : PushOutcome;

    /// <summary>Nothing changed a file: no commit, no push, <c>L</c> did not move.</summary>
    public sealed record NothingToPush(PushSummary Summary) : PushOutcome;

    /// <summary>The pull did not complete, or the last synced commit is not the pull's head; nothing was written.</summary>
    public sealed record NotReady(string Reason) : PushOutcome;

    /// <summary>The remote moved on: nothing was published and every change is still pending.</summary>
    public sealed record NonFastForward(PushSummary Summary) : PushOutcome;

    /// <summary>
    /// A step failed. <paramref name="Kind"/> is the git client's failure kind text, or <c>Busy</c>, <c>Store</c>,
    /// <c>Clone</c>, <c>Write</c>, <c>Stage</c>, <c>StagedSet</c>, <c>Commit</c>, <c>Finalize</c>, <c>Meta</c>, <c>Ref</c> or
    /// <c>Cancelled</c>. <paramref name="Published"/> says whether the commit may have reached the remote.
    /// </summary>
    public sealed record Failed(string Kind, string Message, SyncPush.Published Published) : PushOutcome;
}

/// <summary>
/// The push of the sync cycle (milestone 1b-i-3): after a complete pull, writes this machine's changed memories into
/// the local clone as patches of the records already there (a new memory becomes a new record), commits once, pushes
/// without force, and makes the rows and <c>L</c> say what was pushed. Milestone 1b-ii (D14) adds removals: the record
/// of each local deletion the apply proved (<see cref="ApplySummary.PushDeletions"/>) is removed in the same commit, and
/// after the push the row says the memory is <c>forgotten</c>. The caller holds the sync lock, has just run the
/// scan and a complete pull, and passes both summaries. Nothing calls it yet: the cycle (1b-i-4) will, after the secret
/// scan of 1b-iii has merged.
/// </summary>
public sealed partial class GitSyncPusher
{
    private readonly GitClient _client;
    private readonly SqliteStore _store;
    private readonly string _branch;
    private readonly string _label;

    /// <param name="machineLabel">The OS machine name; it is reduced to the allowed characters here.</param>
    public GitSyncPusher(GitClient client, SqliteStore store, string branch, string machineLabel)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _branch = branch ?? throw new ArgumentNullException(nameof(branch));
        _label = SyncPush.machineLabel(machineLabel);
    }

    /// <summary>Test seam: called after the clone was prepared and read, before the plan transaction opens.</summary>
    internal Action? BeforePlanTransaction { get; set; }

    /// <summary>
    /// Test seam: called with an id (or <c>""</c> for a step that is not per entry) and a step name. In the plan
    /// transaction <c>reread</c>, <c>plan</c>, <c>pushing</c> and <c>block</c>; between steps <c>write</c> (per file,
    /// before the rename, or before the delete of a removed record), <c>stage</c> (before each batch) and <c>commit</c>;
    /// in the finalize transaction <c>finalize</c> (per entry), <c>forget</c> (per removed record) and <c>meta</c>; after
    /// it <c>ref</c>. A hook that throws is the fault of its step.
    /// </summary>
    internal Action<string, string>? BeforeStep { get; set; }

    /// <summary>Test seam: called immediately before the push command (a test makes the remote move here).</summary>
    internal Action? BeforePush { get; set; }

    /// <summary>How many transactions were opened (the plan transaction and the finalize transaction).</summary>
    internal int TransactionCount { get; set; }

    private void Step(string id, string step) => BeforeStep?.Invoke(id, step);

    /// <summary>
    /// Pushes what is pending (decisions 1 to 17). Never throws for a state of the store or the repository: every
    /// failure is a <see cref="PushOutcome"/>.
    /// </summary>
    public async Task<PushOutcome> PushAsync(ApplySummary apply, ScanSummary scan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(scan);
        var prepared = await PrepareAsync(apply, scan, cancellationToken).ConfigureAwait(false);
        if (prepared is PrepareResult.Stop stop)
            return stop.Outcome;
        var ready = ((PrepareResult.Ready)prepared).Value;

        var planned = Plan(ready, cancellationToken);
        if (planned is PlanResult.Stop planStop)
            return planStop.Outcome;
        var plan = (PlanResult.Planned)planned;

        var written = await WriteAsync(ready.Head, plan, cancellationToken).ConfigureAwait(false);
        switch (written)
        {
            case WriteOutcome.Committed committed:
            {
                var failure = await FinalizeAsync(plan, committed.Commit).ConfigureAwait(false);
                return failure is not null ? failure : new PushOutcome.Pushed(plan.Summary(committed.Commit));
            }
            case WriteOutcome.Stop { Outcome: PushOutcome.NothingToPush nothing }:
            {
                var failure = await FinalizeAsync(plan, null).ConfigureAwait(false);
                return failure is not null ? failure : nothing;
            }
            case WriteOutcome.Stop stopped:
                return stopped.Outcome;
            default:
                throw new InvalidOperationException("unreachable");
        }
    }

    private static PushOutcome.Failed Fail(string kind, string message, SyncPush.Published published) =>
        new(kind, SyncSql.OneLine(message), published);

    private static PushOutcome.Failed Cancelled() => Fail("Cancelled", "the push was cancelled", SyncPush.Published.No);

    /// <summary>The failure of a git call as a push failure: a cancelled call is <c>Cancelled</c>, every other kind keeps its own name.</summary>
    private static PushOutcome.Failed FailedFrom(string localKind, GitResult failure, SyncPush.Published published)
    {
        if (failure.Failure == SyncGit.FailureKind.Cancelled)
            return new PushOutcome.Failed("Cancelled", "the push was cancelled", published);
        var text = string.IsNullOrWhiteSpace(failure.StderrText) ? "git failed" : failure.StderrText;
        return Fail(localKind, text, published);
    }
}
