using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>What a fetch learned: the remote head <c>R</c>, the commit this machine last synced <c>L</c> (null when none) and whether applying is proven.</summary>
public sealed record FetchResult(string Head, string? Last, bool Proven, bool NothingNew);

public abstract record FetchOutcome
{
    private FetchOutcome() { }

    /// <summary>The branch was fetched and the clone reset to <see cref="FetchResult.Head"/>.</summary>
    public sealed record Fetched(FetchResult Result) : FetchOutcome;

    /// <summary>The remote head equals <c>L</c> and the local copy holds it: nothing was fetched.</summary>
    public sealed record NothingNew(FetchResult Result) : FetchOutcome;

    /// <summary>The repository guard (or a missing branch) says this machine must not sync; nothing was changed.</summary>
    public sealed record Refused(string Reason) : FetchOutcome;

    /// <summary>A git call failed (<paramref name="Kind"/> is the <see cref="SyncGit.FailureKind"/> name); nothing was applied.</summary>
    public sealed record Failed(string Kind, string Message) : FetchOutcome;
}

/// <summary>The answer of the identity half of the guard (milestone 1b-i-4): may this machine sync this repository at all.</summary>
public abstract record IdentityCheck
{
    private IdentityCheck() { }

    /// <summary>This machine and data directory are the ones that joined the configured repository.</summary>
    public sealed record Joined : IdentityCheck;

    /// <summary>This machine has no sync setup (it never joined); there is nothing to report to the person.</summary>
    public sealed record NotJoined(string Text) : IdentityCheck;

    /// <summary>A setup exists but this machine must not sync (remote changed, data directory moved or copied, host unverified, ...).</summary>
    public sealed record Refused(string Text) : IdentityCheck;
}

/// <summary>
/// The fetch of the pull (milestone 1b-i-2, decisions 1 to 3): asks the remote for its head, skips the network when
/// nothing is new, fetches into the clone, runs the repository guard, decides whether applying is proven (<c>L</c> is
/// an ancestor of the new head and the protective ref says so) and resets the clone. It never writes the store, takes
/// no lock and never writes <c>git_sync_last_ok</c> or <c>git_sync_last_error</c>.
/// </summary>
public sealed class GitSyncFetcher(GitClient client, RepoGuard guard, IMetaStore meta, string branch)
{
    private readonly GitClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly RepoGuard _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    private readonly IMetaStore _meta = meta ?? throw new ArgumentNullException(nameof(meta));
    private readonly string _branch = branch ?? throw new ArgumentNullException(nameof(branch));

    public async Task<FetchOutcome> FetchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await FetchCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new FetchOutcome.Failed(nameof(SyncGit.FailureKind.Cancelled), "the pull was cancelled");
        }
    }

    /// <summary>
    /// The identity half of the guard, with no network command and no write (it may run the read-only local
    /// <c>ls-tree</c> and <c>cat-file</c> the guard uses to read the clone's manifest). A machine that never joined is
    /// <see cref="IdentityCheck.NotJoined"/>; every other refusal is <see cref="IdentityCheck.Refused"/>.
    /// </summary>
    public async Task<IdentityCheck> CheckIdentityAsync(CancellationToken cancellationToken = default)
    {
        var verdict = await _guard.EvaluateIdentityAsync(null, cancellationToken).ConfigureAwait(false);
        if (IdentityRefusal(verdict) is not { } text)
            return new IdentityCheck.Joined();
        var never = verdict.Join is SyncIdentity.JoinState.NotJoined { Item.IsNeverJoined: true };
        return never ? new IdentityCheck.NotJoined(text) : new IdentityCheck.Refused(text);
    }

    private async Task<FetchOutcome> FetchCoreAsync(CancellationToken ct)
    {
        var last = ReadLast();

        // 2. the identity half of the guard needs no clone and no network: refuse first.
        var early = await CheckIdentityAsync(ct).ConfigureAwait(false);
        if (early is IdentityCheck.NotJoined { Text: var notJoinedText })
            return new FetchOutcome.Refused(notJoinedText);
        if (early is IdentityCheck.Refused { Text: var earlyRefusal })
            return new FetchOutcome.Refused(earlyRefusal);

        var advertised = await _client.LsRemoteAsync(SyncGit.RemoteRef.Origin, _branch, cancellationToken: ct).ConfigureAwait(false);
        if (!advertised.IsOk)
            return FailedFrom(advertised.Failure);
        var head = advertised.Value;
        if (head is null)
            return new FetchOutcome.Refused($"the remote has no branch '{_branch}'");

        // Does the clone hold L? A restored database names a commit the clone may never have had (decision 2).
        var cloneHoldsLast = false;
        if (last is not null)
        {
            var held = await _client.RevParseAsync(SyncGit.Revision.NewSha(last), ct).ConfigureAwait(false);
            if (!held.IsOk)
                return FailedFrom(held.Failure);
            cloneHoldsLast = held.Value is not null;
        }

        // 3. nothing new, and the clone holds the commit: no fetch, no reset.
        if (last is not null && string.Equals(head, last, StringComparison.Ordinal))
        {
            if (cloneHoldsLast)
            {
                var guarded = await _guard.EvaluateIdentityAsync(SyncGit.Revision.NewSha(last), ct).ConfigureAwait(false);
                if (Gate(guarded) is { } blocked)
                    return blocked;
                var proof = await ProveAsync(last, last, ct).ConfigureAwait(false);
                if (proof.Failure is not null)
                    return proof.Failure;
                return new FetchOutcome.NothingNew(new FetchResult(last, last, proof.Proven, NothingNew: true));
            }
        }

        // 4. fetch, take R from the tracking ref, run the guard before the reset.
        var fetched = await _client.FetchAsync(_branch, cancellationToken: ct).ConfigureAwait(false);
        if (!fetched.IsOk)
            return FailedFrom(fetched.Failure);
        var tracking = SyncGit.Revision.NewRemoteBranch(_branch);
        var resolved = await _client.RevParseAsync(tracking, ct).ConfigureAwait(false);
        if (!resolved.IsOk)
            return FailedFrom(resolved.Failure);
        if (resolved.Value is not { Length: > 0 } r)
            return new FetchOutcome.Failed(nameof(SyncGit.FailureKind.RemoteBranchMissing), $"the fetch did not bring branch '{_branch}'");

        var verdict = await _guard.EvaluateIdentityAsync(SyncGit.Revision.NewSha(r), ct).ConfigureAwait(false);
        if (Gate(verdict) is { } lateBlocked)
            return lateBlocked;

        // 5. the proof state, then 6. the reset.
        var proven = await ProveAsync(cloneHoldsLast ? last : null, r, ct).ConfigureAwait(false);
        if (proven.Failure is not null)
            return proven.Failure;
        var reset = await _client.ResetHardAsync(SyncGit.Revision.NewSha(r), ct).ConfigureAwait(false);
        if (!reset.IsOk)
            return FailedFrom(reset.Failure);
        return new FetchOutcome.Fetched(new FetchResult(r, last, proven.Proven, NothingNew: false));
    }

    /// <summary><c>L</c> from <c>_meta</c>; none unless it is a full lowercase hex object id.</summary>
    private string? ReadLast()
    {
        var text = _meta.GetMeta(SyncIdentity.MetaKeys.LastSha);
        if (text is null || (text.Length != 40 && text.Length != 64))
            return null;
        foreach (var c in text)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return null;
        return text;
    }

    /// <summary>
    /// Decision 2: proven only with an <c>L</c> that is an ancestor of <paramref name="head"/> and a protective ref that
    /// points at <c>L</c>; a ref that is a strict ancestor of <c>L</c> is repaired in place. A git call that gives no
    /// answer is a failure; "no", "unknown", a missing ref or a ref elsewhere is unproven.
    /// </summary>
    private async Task<(bool Proven, FetchOutcome? Failure)> ProveAsync(string? last, string head, CancellationToken ct)
    {
        if (last is null)
            return (false, null);
        var ancestor = await _client.IsAncestorAsync(SyncGit.Revision.NewSha(last), SyncGit.Revision.NewSha(head), ct).ConfigureAwait(false);
        if (!ancestor.IsOk)
            return (false, FailedFrom(ancestor.Failure));
        if (ancestor.Value != Tri.Yes)
            return (false, null);

        var marker = await _client.RevParseAsync(SyncGit.Revision.LastSynced, ct).ConfigureAwait(false);
        if (!marker.IsOk)
            return (false, FailedFrom(marker.Failure));
        if (marker.Value is null)
            return (false, null);
        if (string.Equals(marker.Value, last, StringComparison.Ordinal))
            return (true, null);

        var behind = await _client.IsAncestorAsync(SyncGit.Revision.NewSha(marker.Value), SyncGit.Revision.NewSha(last), ct).ConfigureAwait(false);
        if (!behind.IsOk)
            return (false, FailedFrom(behind.Failure));
        if (behind.Value != Tri.Yes)
            return (false, null);
        var repaired = await _client.UpdateLastSyncedAsync(last, ct).ConfigureAwait(false);
        if (!repaired.IsOk)
            return (false, FailedFrom(repaired.Failure));
        return (true, null);
    }

    private static string? IdentityRefusal(GuardVerdict verdict)
    {
        if (verdict.Join is SyncIdentity.JoinState.NotJoined notJoined)
            return SyncIdentity.reasonText(notJoined.Item);
        if (verdict.HostUnverified)
            return "host identity could not be determined";
        return null;
    }

    /// <summary>
    /// The outcome when the full guard stops the pull: a refusal, or a failure when the manifest could not be read
    /// because a git call gave no answer (a cancelled or timed-out call is never a refusal).
    /// </summary>
    private static readonly string[] FailureNames =
    [
        "NoGit", "GitTooOld", "TimedOut", "Cancelled", "Auth", "RemoteNotFound", "Network",
        "NonFastForward", "RemoteBranchMissing", "NotARepository", "OutputTooLarge",
    ];

    private static FetchOutcome? Gate(GuardVerdict verdict)
    {
        if (verdict.Manifest.Kind == ManifestReadKind.Unreadable && IdentityRefusal(verdict) is null)
        {
            var reason = verdict.Manifest.Reason ?? "the manifest could not be read";
            var kind = nameof(SyncGit.FailureKind.Other);
            foreach (var name in FailureNames)
                if (reason.Contains(": " + name, StringComparison.Ordinal))
                    kind = name;
            return new FetchOutcome.Failed(kind, OneLine(reason));
        }
        return FullRefusal(verdict) is { } refusal ? new FetchOutcome.Refused(refusal) : null;
    }

    private static string? FullRefusal(GuardVerdict verdict)
    {
        if (IdentityRefusal(verdict) is { } identity)
            return identity;
        var manifest = verdict.Manifest;
        return manifest.Kind switch
        {
            ManifestReadKind.Ok => null,
            ManifestReadKind.NoClone => "the local copy of the repository is missing",
            ManifestReadKind.NoManifest => "the repository has no manifest.json",
            ManifestReadKind.Newer => $"the repository uses a newer format (version {manifest.NewerVersion}): update total-recall",
            ManifestReadKind.Invalid => "the repository's manifest.json is not valid: " + (manifest.Reason ?? "unreadable"),
            _ => "the repository's manifest.json could not be read: " + (manifest.Reason ?? "unreadable"),
        };
    }

    private static FetchOutcome.Failed FailedFrom(GitResult failure)
    {
        var kind = failure.Failure?.ToString() ?? nameof(SyncGit.FailureKind.Other);
        var text = string.IsNullOrWhiteSpace(failure.StderrText) ? "the git call failed" : OneLine(failure.StderrText);
        return new FetchOutcome.Failed(kind, text);
    }

    internal static string OneLine(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Trim())
            sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.Length > 200 ? sb.ToString(0, 200) : sb.ToString();
    }
}
