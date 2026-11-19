using TotalRecall.Core;
using TotalRecall.Infrastructure.Diagnostics;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>The clone of the cycle (decisions 8 and 9): a missing clone is made again, a damaged one is moved aside (never deleted) and replaced, once per process.</summary>
public sealed partial class GitSyncCycle
{
    private readonly object _damageGate = new();
    private int _damagedClones;
    private CycleOutcome.Failed? _unusable;

    /// <summary>Test seam: removes a directory (a partial clone). The default clears read-only attributes without following links, then deletes.</summary>
    internal Action<string>? DeleteDirectory { get; set; }

    /// <summary>Test seam: the rename <see cref="CloneSideline.Move"/> makes.</summary>
    internal Action<string, string>? MoveDirectory { get; set; }

    /// <summary>Decision 9: once the cycle has found a second damaged clone, every later run returns the same failure at once.</summary>
    private CycleOutcome? StoredCloneFailure()
    {
        lock (_damageGate)
            return _unusable;
    }

    /// <summary>
    /// Step (e): a sound clone needs nothing; a missing one is cloned; a directory that is not a sound clone is damage,
    /// moved aside and replaced. Returns the outcome that ends the cycle, or null to go on.
    /// </summary>
    private async Task<CycleOutcome?> EnsureCloneAsync(RunState state, CancellationToken ct)
    {
        if (_client.CloneIsSound())
            return null;

        Enter(state, SyncCycle.Step.Clone, ct);
        if (Directory.Exists(_paths.CloneDirectory))
            return await ReplaceDamagedCloneAsync(state, ct).ConfigureAwait(false);
        return await CloneFreshAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the damaged clone aside and clones again. The second damaged clone found in this process is
    /// <c>CloneUnusable</c> and is kept; the count is raised only after the move succeeded.
    /// </summary>
    private async Task<CycleOutcome?> ReplaceDamagedCloneAsync(RunState state, CancellationToken ct)
    {
        lock (_damageGate)
        {
            if (_damagedClones >= 1)
            {
                _unusable = Fail(
                    SyncCycle.stepName(SyncCycle.Step.Clone),
                    "CloneUnusable",
                    "the local copy of the repository was damaged again in this process; it was not moved aside a second time: restart total-recall to try again");
                return _unusable;
            }
        }

        string moved;
        try
        {
            moved = CloneSideline.Move(_paths.CloneDirectory, "broken", _clock(), MoveDirectory);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            ExceptionLogger.LogChain("[total-recall] sync cycle", ex);
            return Fail(SyncCycle.stepName(SyncCycle.Step.Clone), "Unexpected", "the damaged copy of the repository could not be moved aside: " + ex.Message);
        }

        lock (_damageGate)
            _damagedClones++;
        state.SidelinedClone = moved;
        return await CloneFreshAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>Clones the configured address into the (absent) clone directory. A clone that fails or is cancelled leaves nothing behind that counts.</summary>
    private async Task<CycleOutcome?> CloneFreshAsync(RunState state, CancellationToken ct)
    {
        GitOutcome<NoValue> cloned;
        try
        {
            cloned = await _client.CloneAsync(_settings.Address, _settings.Branch, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RemovePartialClone();
            return Cancelled();
        }

        if (cloned.IsOk)
        {
            state.Cloned = true;
            return null;
        }

        RemovePartialClone();
        var failure = cloned.Failure;
        var kind = failure.Failure ?? SyncGit.FailureKind.Other;
        if (ct.IsCancellationRequested || kind.IsCancelled)
            return Cancelled();
        if (kind.IsRemoteBranchMissing)
            return new CycleOutcome.Refused($"the remote has no branch '{_settings.Branch}'", SyncCycle.stepName(SyncCycle.Step.Clone));
        return Fail(SyncCycle.stepName(SyncCycle.Step.Clone), kind.ToString(), Describe(failure));
    }

    /// <summary>
    /// A clone that failed or was cancelled leaves a partial directory, which holds nothing the remote does not. It is
    /// deleted at once (best effort, never cancelled, links never followed); when that fails it is moved aside as
    /// <c>partial</c>; when that fails too it stays. Neither counts as damage. Known limit (the spec's "it stays"): a
    /// directory that stays can hold a <c>.git</c> that passes <see cref="GitClient.CloneIsSound"/>, and then the next
    /// cycle's fetch finds it as damage and uses the process's one sideline on it.
    /// </summary>
    private void RemovePartialClone()
    {
        var directory = _paths.CloneDirectory;
        try
        {
            if (!Directory.Exists(directory))
                return;
            try
            {
                (DeleteDirectory ?? DeleteTree)(directory);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                CloneSideline.Move(directory, "partial", _clock(), MoveDirectory);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // the original outcome stands
        }
    }

    private static void DeleteTree(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", options).ToList())
        {
            try
            {
                File.SetAttributes(entry, FileAttributes.Normal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // deleting it will say whether that mattered
            }
        }
        Directory.Delete(directory, recursive: true);
    }

    /// <summary>
    /// Decision 8: whether a failed call means the clone is damaged. <c>NotARepository</c> always is; <c>Other</c> and
    /// <c>RemoteNotFound</c> are decided by a local probe (<c>rev-parse --verify HEAD^{commit}</c>, as join does): healthy
    /// when it names a commit, damage when it names none or fails for a reason that is not a limit or a cancel.
    /// </summary>
    private async Task<bool> IsDamageAsync(SyncGit.FailureKind kind, CancellationToken ct)
    {
        if (!kind.IsNotARepository && !kind.IsOther && !kind.IsRemoteNotFound)
            return false;
        if (kind.IsNotARepository)
            return SyncCycle.cloneDamage(kind, SyncCycle.Probe.Healthy);

        var probed = await _client.RevParseAsync(SyncGit.Revision.Head, ct).ConfigureAwait(false);
        var probe = probed.IsOk
            ? (probed.Value is null ? SyncCycle.Probe.NoCommit : SyncCycle.Probe.Healthy)
            : SyncCycle.Probe.NewProbeFailed(probed.Failure.Failure ?? SyncGit.FailureKind.Other);
        return SyncCycle.cloneDamage(kind, probe);
    }

    /// <summary>One line of the git error text, cut at 200 characters (the fetcher's rule).</summary>
    private static string Describe(GitResult failure)
    {
        var text = string.IsNullOrWhiteSpace(failure.StderrText) ? "the git call failed" : failure.StderrText.Trim();
        var line = new System.Text.StringBuilder();
        foreach (var c in text)
            line.Append(char.IsControl(c) ? ' ' : c);
        return line.Length > 200 ? line.ToString(0, 200) : line.ToString();
    }
}
