using Microsoft.FSharp.Core;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

public sealed partial class SyncSetupService
{
    private static SetupResult NothingToJoin(string branch, IReadOnlyList<string>? notes = null) =>
        SetupResult.Refused(
            SyncSetup.Refusal.NothingToJoin,
            $"the repository has no '{branch}' branch: use `sync init` on a repository you want to start, or check the branch name",
            notes ?? []);

    /// <summary>Adopts an existing sync repository (decision 6).</summary>
    public async Task<SetupResult> JoinAsync(string address, string? branch, CancellationToken ct = default)
    {
        try
        {
            var (prepared, refusal) = await PrepareAsync(address, branch, ct).ConfigureAwait(false);
            if (refusal is not null)
                return refusal;
            using (prepared)
                return WithNotes(await JoinCoreAsync(prepared!, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return WithNotes(FailedFrom("join", ex));
        }
    }

    private async Task<SetupResult> JoinCoreAsync(Prepared p, CancellationToken ct)
    {
        // 6.1: does the remote have the branch?
        _phase = "looking at the repository";
        var head = await p.Client.LsRemoteAsync(SyncGit.RemoteRef.NewUrl(p.Address), p.Branch, onStderr: Progress, cancellationToken: ct).ConfigureAwait(false);
        if (!head.IsOk)
            return ClassifyFailure(head.Failure, Step.Look, forInit: false);
        if (head.Value is null)
            return NothingToJoin(p.Branch);

        // 6.2 to 6.4: a clone, the manifest, the working tree level with the remote
        var (refusal, reused, manifest) = await AcquireCloneAsync(p, forceFresh: false, ct).ConfigureAwait(false);
        if (refusal is not null)
            return refusal;
        var repoId = manifest!.Manifest!.RepoId;

        // 6.5: the same repository, or not
        var storedId = FSharpOptionOrNull(p.Stored.RepoId);
        var same = SyncSetup.isSameRepository(
            storedId is null ? FSharpOption<string>.None : FSharpOption<string>.Some(storedId),
            repoId,
            p.PreviousBranch is null ? FSharpOption<string>.None : FSharpOption<string>.Some(p.PreviousBranch),
            p.Branch);

        if (!same && reused)
        {
            // an earlier clone of this very address may hold the old repository's protective ref: it never carries over
            _phase = "moving the earlier clone aside";
            var left = CloneSideline.Move(p.Paths.CloneDirectory, "left", env.Clock(), env.Move);
            _notes.Add($"note: the earlier clone of this address was kept at {left}");
            reused = false;
            _phase = "cloning the repository";
            var fresh = await p.Client.CloneAsync(p.Address, p.Branch, onStderr: Progress, cancellationToken: ct).ConfigureAwait(false);
            if (!fresh.IsOk)
                return MapCloneFailure(fresh.Failure, p.Branch);
            var again = await p.Guard.ReadManifestAsync(SyncGit.Revision.NewRemoteBranch(p.Branch), ct).ConfigureAwait(false);
            var againRefusal = ManifestRefusal(again);
            if (againRefusal is not null)
                return againRefusal;
            if (!string.Equals(again.Manifest!.RepoId, repoId, StringComparison.Ordinal))
                return Failed("the repository changed while joining: run this again");
        }

        // 6.6: record
        IReadOnlyList<string> notes;
        try
        {
            var kind = same ? RecordKind.JoinSame : RecordKind.JoinDifferent;
            notes = await RecordAsync(p, new RecordPlan(kind, repoId, reused, same), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            Diagnostics.ExceptionLogger.LogChain(Console.Error, $"sync join ({_phase})", ex);
            return Failed($"the repository was joined but this machine could not record it ({_phase}: {ex.Message}): run `sync join {SyncSetup.displayRemote(p.Address)}` again");
        }

        var lines = new List<string>
        {
            $"joined the sync repository at {SyncSetup.displayRemote(p.Address)} (branch {p.Branch})",
            "this machine is set up for git sync (storage.mode = \"git\"), but syncing memories is not part of this build yet",
        };
        lines.AddRange(notes);
        lines.AddRange(SidelineNotes(lines));
        return SetupResult.Done([.. lines]);
    }

    private static SetupResult MapCloneFailure(GitResult failure, string branch, IReadOnlyList<string>? notes = null) =>
        failure.Failure is { IsRemoteBranchMissing: true }
            ? NothingToJoin(branch, notes)
            : ClassifyFailure(failure, Step.Clone, forInit: false, notes);

    private static SetupResult? ManifestRefusal(ManifestRead manifest) => manifest.Kind switch
    {
        ManifestReadKind.Ok => null,
        ManifestReadKind.NoManifest => Refuse(SyncSetup.Refusal.NotASyncRepo, "this repository has no manifest.json: it is not a total-recall sync repository"),
        ManifestReadKind.Invalid or ManifestReadKind.Unreadable =>
            Refuse(SyncSetup.Refusal.InvalidManifest, $"the repository's manifest.json is not usable: {manifest.Reason}"),
        ManifestReadKind.Newer =>
            Refuse(SyncSetup.Refusal.FormatTooNew, "this repository was written by a newer total-recall; update total-recall"),
        _ => Failed("the clone could not be read"),
    };

    /// <summary>
    /// Decision 6.2 to 6.4: use the clone that is there when it is sound (refresh it), else move it aside and clone afresh;
    /// read the manifest at the remote's revision; bring the working tree level. Returns the refusal, whether a clone that
    /// was already there is being used, and the manifest.
    /// </summary>
    private async Task<(SetupResult? Refusal, bool Reused, ManifestRead? Manifest)> AcquireCloneAsync(
        Prepared p, bool forceFresh, CancellationToken ct)
    {
        var client = p.Client;
        var clone = p.Paths.CloneDirectory;
        string[] kept = [$"note: a clone was kept at {clone}"];
        var reused = false;
        var needFresh = forceFresh;

        if (!forceFresh && Directory.Exists(clone))
        {
            _phase = "refreshing the clone";
            if (!Directory.Exists(Path.Combine(clone, ".git")))
            {
                needFresh = true; // a directory without a sound .git
            }
            else
            {
                client.RemoveStaleLocks();
                var origin = await client.SetOriginAsync(p.Address, ct).ConfigureAwait(false);
                if (!origin.IsOk)
                {
                    needFresh = true;
                }
                else
                {
                    var fetch = await client.FetchAsync(p.Branch, onStderr: Progress, cancellationToken: ct).ConfigureAwait(false);
                    if (fetch.IsOk)
                    {
                        reused = true;
                    }
                    else
                    {
                        var failure = fetch.Failure;
                        if (failure.Failure is { IsNotARepository: true })
                        {
                            needFresh = true;
                        }
                        else if (failure.Failure is { IsRemoteBranchMissing: true })
                        {
                            return (NothingToJoin(p.Branch, kept), false, null);
                        }
                        else if (failure.Failure is { IsOther: true })
                        {
                            // `Other` is real damage (a corrupt .git) or a transient server error: a local, network-free probe tells which
                            var probe = await client.RevParseAsync(SyncGit.Revision.Head, ct).ConfigureAwait(false);
                            if (probe.IsOk && probe.Value is not null)
                                return (Failed($"the fetch failed: {GitText(failure)}", kept), false, null);
                            if (!probe.IsOk && probe.Failure.Failure is { IsCancelled: true } or { IsTimedOut: true } or { IsOutputTooLarge: true })
                                return (Failed($"the fetch failed: {GitText(failure)}", kept), false, null);
                            needFresh = true;
                        }
                        else
                        {
                            return (ClassifyFailure(failure, Step.Fetch, forInit: false, kept), false, null);
                        }
                    }
                }
            }
        }

        if (needFresh || !Directory.Exists(clone))
        {
            _phase = "cloning the repository";
            if (Directory.Exists(clone))
            {
                var broken = CloneSideline.Move(clone, "broken", env.Clock(), env.Move); // never deleted
                _notes.Add($"note: a damaged clone was kept at {broken}");
            }
            reused = false;
            var cloned = await client.CloneAsync(p.Address, p.Branch, onStderr: Progress, cancellationToken: ct).ConfigureAwait(false);
            if (!cloned.IsOk)
                return (MapCloneFailure(cloned.Failure, p.Branch), false, null);
            _notes.Add($"note: a clone was kept at {clone}");
        }

        _phase = "reading the manifest";
        var manifest = await p.Guard.ReadManifestAsync(SyncGit.Revision.NewRemoteBranch(p.Branch), ct).ConfigureAwait(false);
        var refusal = ManifestRefusal(manifest);
        if (refusal is not null)
            return (refusal with { Notes = kept }, reused, null);

        _phase = "bringing the clone level";
        var reset = await client.ResetHardAsync(SyncGit.Revision.NewRemoteBranch(p.Branch), ct).ConfigureAwait(false);
        if (!reset.IsOk)
        {
            if (reused && !forceFresh)
                return await AcquireCloneAsync(p, forceFresh: true, ct).ConfigureAwait(false); // a damaged clone: move it aside, clone afresh
            return (ClassifyFailure(reset.Failure, Step.Clone, forInit: false, kept), reused, null);
        }
        return (null, reused, manifest);
    }
}
