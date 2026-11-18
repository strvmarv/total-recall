using Microsoft.FSharp.Collections;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>The tree-reading and write operations of <see cref="GitClient"/>.</summary>
public sealed partial class GitClient
{
    private static GitOutcome<IReadOnlyList<T>> Parsed<T>(GitResult result, Microsoft.FSharp.Core.FSharpResult<FSharpList<T>, string> parsed) =>
        parsed.IsOk
            ? GitOutcome<IReadOnlyList<T>>.Ok(parsed.ResultValue)
            : GitOutcome<IReadOnlyList<T>>.Fail(result with { Failure = SyncGit.FailureKind.Other, Stdout = [], StderrText = "unreadable listing: " + parsed.ErrorValue });

    /// <summary>The files that changed between two commits under <c>memories/</c>; renames are not detected (a rename is a delete and an add).</summary>
    public async Task<GitOutcome<IReadOnlyList<SyncTree.DiffEntry>>> DiffTreeAsync(
        SyncGit.Revision from,
        SyncGit.Revision to,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(SyncGit.GitCommand.NewDiffTree(from, to), _options.LocalTimeout, cancellationToken).ConfigureAwait(false);
        return result.Failure is not null
            ? GitOutcome<IReadOnlyList<SyncTree.DiffEntry>>.Fail(result)
            : Parsed(result, SyncTree.parseDiffTree(result.Stdout));
    }

    /// <summary>Every file under <c>memories/</c> at a revision, with its mode and size. Judge each with <see cref="SyncTree.classifyRecordEntry"/>.</summary>
    public async Task<GitOutcome<IReadOnlyList<SyncTree.TreeEntry>>> LsTreeAsync(SyncGit.Revision revision, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(SyncGit.GitCommand.NewLsTree(revision), _options.LocalTimeout, cancellationToken).ConfigureAwait(false);
        return result.Failure is not null
            ? GitOutcome<IReadOnlyList<SyncTree.TreeEntry>>.Fail(result)
            : Parsed(result, SyncTree.parseLsTree(result.Stdout));
    }

    /// <summary>The entries of the root of a revision, not recursive (the manifest and the <c>memories</c> directory).</summary>
    public async Task<GitOutcome<IReadOnlyList<SyncTree.TreeEntry>>> LsTreeRootAsync(SyncGit.Revision revision, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(SyncGit.GitCommand.NewLsTreeRoot(revision), _options.LocalTimeout, cancellationToken).ConfigureAwait(false);
        return result.Failure is not null
            ? GitOutcome<IReadOnlyList<SyncTree.TreeEntry>>.Fail(result)
            : Parsed(result, SyncTree.parseLsTree(result.Stdout));
    }

    /// <summary>
    /// The bytes of a blob. A blob larger than <paramref name="maxBytes"/> ends the call as <c>OutputTooLarge</c>: the
    /// result is never a truncated array.
    /// </summary>
    public async Task<GitOutcome<byte[]>> CatBlobAsync(string sha, int maxBytes, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            SyncGit.GitCommand.NewCatBlob(sha),
            _options.LocalTimeout,
            cancellationToken,
            maxStdoutBytes: Math.Max(0, maxBytes)).ConfigureAwait(false);
        return result.Failure is null ? GitOutcome<byte[]>.Ok(result.Stdout) : GitOutcome<byte[]>.Fail(result);
    }

    /// <summary>
    /// Stages one fixed scope (<c>git add -A</c>, so deletions are staged). A scope with nothing tracked or present
    /// stages nothing and succeeds; any other exit 128, such as a damaged clone, is a failure.
    /// </summary>
    public async Task<GitOutcome<NoValue>> StageAsync(SyncGit.StageScope scope, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(SyncGit.GitCommand.NewStage(scope), _options.LocalTimeout, cancellationToken).ConfigureAwait(false);
        // git's message for an empty scope is exactly one line; a longer text could carry a hostile file name that
        // quotes it, so only the exact line is a success
        var path = scope.IsMemories ? "memories" : scope.IsManifest ? "manifest.json" : ".gitattributes";
        if (result.ExitCode == 128 && result.StderrText.Trim() == $"fatal: pathspec '{path}' did not match any files")
            return GitOutcome<NoValue>.Ok(default);
        return Done(result);
    }

    /// <summary>
    /// Stages exactly these record paths (<c>git add -f -- &lt;paths&gt;</c>: no <c>-A</c>, so a deletion is never staged
    /// by it). Unlike <see cref="StageAsync"/> every failure is a failure, including exit 128 for a path that does not
    /// exist. The list must be non-empty and hold only exact record paths, else the call fails before any process starts.
    /// </summary>
    public async Task<GitOutcome<NoValue>> StagePathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Done(await RunAsync(
            SyncGit.GitCommand.NewStagePaths(ListModule.OfSeq(paths)), _options.LocalTimeout, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>What the index holds against <c>HEAD</c>: one entry per path, with its status letter (renames are not detected).</summary>
    public async Task<GitOutcome<IReadOnlyList<SyncPush.StagedEntry>>> StagedSetAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(SyncGit.GitCommand.StagedSet, _options.LocalTimeout, cancellationToken).ConfigureAwait(false);
        return result.Failure is not null
            ? GitOutcome<IReadOnlyList<SyncPush.StagedEntry>>.Fail(result)
            : Parsed(result, SyncPush.parseStagedSet(result.Stdout));
    }

    /// <summary>True when the index differs from <c>HEAD</c>.</summary>
    public async Task<GitOutcome<bool>> HasStagedAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(SyncGit.GitCommand.HasStaged, _options.LocalTimeout, cancellationToken).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => GitOutcome<bool>.Ok(false),
            1 => GitOutcome<bool>.Ok(true),
            _ => GitOutcome<bool>.Fail(result),
        };
    }

    /// <summary>Commits the index as <c>total-recall &lt;noreply@total-recall.local&gt;</c>: unsigned, without hooks, the message verbatim.</summary>
    public async Task<GitOutcome<NoValue>> CommitAsync(string message, CancellationToken cancellationToken = default) =>
        Done(await RunAsync(SyncGit.GitCommand.NewCommit(message), _options.LocalTimeout, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Pushes <c>HEAD</c> to <c>refs/heads/&lt;branch&gt;</c> and nothing else: no force, no delete, no mirror. A remote
    /// that moved on is a failure of kind <c>NonFastForward</c> and nothing is overwritten.
    /// </summary>
    public async Task<GitOutcome<NoValue>> PushAsync(
        string branch,
        TimeSpan? timeout = null,
        Action<string>? onStderr = null,
        CancellationToken cancellationToken = default) =>
        Done(await RunNetworkAsync(
            SyncGit.GitCommand.NewPush(branch),
            null,
            timeout ?? _options.NetworkTimeout,
            onStderr,
            cancellationToken).ConfigureAwait(false));
}
