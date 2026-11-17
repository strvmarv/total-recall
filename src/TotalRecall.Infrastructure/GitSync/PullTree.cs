using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>An accepted record file of the tree at <c>R</c>: its path and blob id.</summary>
internal sealed record AcceptedRecord(string Path, string Sha);

/// <summary>
/// A path of the tree that is not read as a record. <paramref name="Key"/> is the <c>rejected-record</c> issue key;
/// <paramref name="NamesId"/> the id the path names (only for the exact form <c>memories/&lt;shard&gt;/&lt;id&gt;.json</c>).
/// </summary>
internal sealed record RejectedPath(string Key, string Path, string? Sha, string Reason, string? NamesId);

/// <summary>What the pull learned about the tree at <c>R</c> (decision 4).</summary>
internal sealed record TreeView(
    IReadOnlyDictionary<string, AcceptedRecord> Accepted,
    IReadOnlyList<RejectedPath> Rejected,
    IReadOnlySet<string> Present,
    IReadOnlySet<string> Changed);

internal abstract record TreeRead
{
    private TreeRead() { }

    public sealed record Ok(TreeView View) : TreeRead;

    /// <summary>The listing or the diff could not be read; <paramref name="Cancelled"/> when the git client reports the call was cancelled.</summary>
    public sealed record Failed(string Reason, bool Cancelled) : TreeRead;
}

/// <summary>
/// Reads the repository tree at <c>R</c> as git objects, never from the working tree (decision 4): <c>ls-tree</c>
/// judges every path (accepted, or rejected and then maybe naming an id) and, when the apply is proven, <c>diff-tree</c>
/// from <c>L</c> to <c>R</c> says which ids changed. The listing alone decides what is present and what is rejected: the
/// diff's deletion and rejection verdicts are not used.
/// </summary>
internal static class PullTree
{
    public static async Task<TreeRead> ReadAsync(
        GitClient client, string head, string? last, bool unproven, CancellationToken cancellationToken)
    {
        var listing = await client.LsTreeAsync(SyncGit.Revision.NewSha(head), cancellationToken).ConfigureAwait(false);
        if (!listing.IsOk)
            return Failed("could not list the repository tree", listing.Failure);

        var accepted = new Dictionary<string, AcceptedRecord>(StringComparer.Ordinal);
        var rejected = new List<RejectedPath>();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in listing.Value)
        {
            var verdict = SyncTree.classifyRecordEntry(entry);
            if (verdict is SyncTree.Verdict.Accept accept)
            {
                accepted[accept.id] = new AcceptedRecord(entry.Path, accept.sha);
                present.Add(accept.id);
            }
            else if (verdict is SyncTree.Verdict.Reject reject)
            {
                var path = reject.path;
                var named = SyncValidation.idOfPath(path);
                var id = named is null ? null : named.Value;
                if (id is not null)
                    present.Add(id);
                rejected.Add(new RejectedPath(
                    SyncApply.rejectionKey(path), path, entry.Sha, SyncTree.RejectReasonModule.describe(reject.Item2), id));
            }
        }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (!unproven && last is not null)
        {
            var diff = await client.DiffTreeAsync(SyncGit.Revision.NewSha(last), SyncGit.Revision.NewSha(head), cancellationToken).ConfigureAwait(false);
            if (!diff.IsOk)
                return Failed("could not diff the repository tree", diff.Failure);
            foreach (var entry in diff.Value)
                if (SyncTree.classifyDiffEntry(entry) is SyncTree.DiffVerdict.Changed c)
                    changed.Add(c.id);
        }

        return new TreeRead.Ok(new TreeView(accepted, rejected, present, changed));
    }

    private static TreeRead Failed(string what, GitResult failure) =>
        new TreeRead.Failed(
            $"{what}: {failure.Failure?.ToString() ?? "failed"}",
            failure.Failure == SyncGit.FailureKind.Cancelled);
}
