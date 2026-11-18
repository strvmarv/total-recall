using Microsoft.FSharp.Collections;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

internal abstract record WriteOutcome
{
    private WriteOutcome() { }

    /// <summary>The commit was made and the push command succeeded.</summary>
    public sealed record Committed(string Commit) : WriteOutcome;

    /// <summary>The push ends here: nothing to push, the remote moved on, or a failure.</summary>
    public sealed record Stop(PushOutcome Outcome) : WriteOutcome;
}

public sealed partial class GitSyncPusher
{
    private const int StageBatch = 100;

    /// <summary>
    /// Decision 10 and 11: writes the planned records into the clone (temporary file under <c>.git</c>, then a rename
    /// over the record path), stages exactly those paths, checks the staged set, commits once and pushes without force.
    /// Any failure up to the push command resets and cleans the clone. A rejected push leaves the clone as it is (the next
    /// push resets it); the finalize is the caller's.
    /// </summary>
    internal async Task<WriteOutcome> WriteAsync(string head, PlanResult.Planned plan, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return new WriteOutcome.Stop(Cancelled());
        if (plan.Entries.Count == 0)
            return new WriteOutcome.Stop(new PushOutcome.NothingToPush(plan.Summary(null)));

        var clone = _client.CloneDirectory;
        var gitDirectory = Path.Combine(clone, ".git");
        var temps = new List<string>();
        var newPaths = new List<string>();
        var paths = new List<string>();
        var phase = "Write";
        try
        {
            // 1. the files
            foreach (var entry in plan.Entries)
            {
                if (ct.IsCancellationRequested)
                    return await Abandon(head, temps, newPaths, Cancelled()).ConfigureAwait(false);
                var relative = SyncValidation.recordPath(entry.Id);
                var destination = Path.Combine(clone, relative.Replace('/', Path.DirectorySeparatorChar));
                paths.Add(relative);
                var existed = File.Exists(destination);
                if (!existed)
                    newPaths.Add(destination);

                var temp = Path.Combine(gitDirectory, "tr-push-" + Guid.NewGuid().ToString("N") + ".tmp");
                temps.Add(temp);
                await File.WriteAllBytesAsync(temp, entry.Bytes, ct).ConfigureAwait(false);
                if (existed && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temp, File.GetUnixFileMode(destination));
                Step(entry.Id, "write");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(temp, destination, overwrite: true);
                temps.Remove(temp);
            }

            // 2. stage the written paths only, in batches, and check the staged set
            phase = "Stage";
            foreach (var batch in paths.Chunk(StageBatch))
            {
                if (ct.IsCancellationRequested)
                    return await Abandon(head, temps, newPaths, Cancelled()).ConfigureAwait(false);
                Step("", "stage");
                var staged = await _client.StagePathsAsync(batch, ct).ConfigureAwait(false);
                if (!staged.IsOk)
                    return await Abandon(head, temps, newPaths, FailedFrom("Stage", staged.Failure, SyncPush.Published.No)).ConfigureAwait(false);
            }

            phase = "StagedSet";
            var set = await _client.StagedSetAsync(ct).ConfigureAwait(false);
            if (!set.IsOk)
                return await Abandon(head, temps, newPaths, FailedFrom("StagedSet", set.Failure, SyncPush.Published.No)).ConfigureAwait(false);
            if (!SyncPush.stagedSetMatches(ListModule.OfSeq(paths), ListModule.OfSeq(set.Value)))
            {
                return await Abandon(head, temps, newPaths,
                    Fail("StagedSet", "the staged files are not exactly the files this push wrote", SyncPush.Published.No)).ConfigureAwait(false);
            }
            var any = await _client.HasStagedAsync(ct).ConfigureAwait(false);
            if (!any.IsOk)
                return await Abandon(head, temps, newPaths, FailedFrom("StagedSet", any.Failure, SyncPush.Published.No)).ConfigureAwait(false);
            if (!any.Value)
            {
                await CleanUpAsync(head, temps, newPaths).ConfigureAwait(false);
                return new WriteOutcome.Stop(new PushOutcome.NothingToPush(plan.Summary(null)));
            }

            // 3. one commit
            phase = "Commit";
            if (ct.IsCancellationRequested)
                return await Abandon(head, temps, newPaths, Cancelled()).ConfigureAwait(false);
            Step("", "commit");
            var message = SyncPush.commitMessage(plan.Entries.Count(e => e.IsNew), plan.Entries.Count(e => !e.IsNew), _label);
            var committed = await _client.CommitAsync(message, ct).ConfigureAwait(false);
            if (!committed.IsOk)
                return await Abandon(head, temps, newPaths, FailedFrom("Commit", committed.Failure, SyncPush.Published.No)).ConfigureAwait(false);
            var tip = await _client.RevParseAsync(SyncGit.Revision.Head, ct).ConfigureAwait(false);
            if (!tip.IsOk)
                return await Abandon(head, temps, newPaths, FailedFrom("Commit", tip.Failure, SyncPush.Published.No)).ConfigureAwait(false);
            if (!IsSha(tip.Value) || string.Equals(tip.Value, head, StringComparison.Ordinal))
                return await Abandon(head, temps, newPaths, Fail("Commit", "the commit did not move HEAD", SyncPush.Published.No)).ConfigureAwait(false);
            var commit = tip.Value!;

            // 4. the push
            phase = "Push";
            BeforePush?.Invoke();
            if (ct.IsCancellationRequested)
                return await Abandon(head, temps, newPaths, Cancelled()).ConfigureAwait(false);
            var pushed = await _client.PushAsync(_branch, cancellationToken: ct).ConfigureAwait(false);
            if (pushed.IsOk)
                return new WriteOutcome.Committed(commit);

            var failure = pushed.Failure;
            var kind = failure.Failure ?? SyncGit.FailureKind.Other;
            if (kind == SyncGit.FailureKind.NonFastForward)
                return new WriteOutcome.Stop(new PushOutcome.NonFastForward(plan.Summary(commit)));
            var published = SyncPush.publishedAfterPush(kind, failure.StderrText);
            var text = string.IsNullOrWhiteSpace(failure.StderrText) ? "git push failed" : failure.StderrText;
            return new WriteOutcome.Stop(kind == SyncGit.FailureKind.Cancelled
                ? new PushOutcome.Failed("Cancelled", "the push was cancelled", published)
                : Fail(kind.ToString(), text, published));
        }
        catch (OperationCanceledException)
        {
            return await Abandon(head, temps, newPaths, Cancelled()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a file system failure, or a test hook that throws: the fault of the step it happened in
            return await Abandon(head, temps, newPaths, Fail(phase, ex.Message, SyncPush.Published.No)).ConfigureAwait(false);
        }
    }

    private async Task<WriteOutcome> Abandon(string head, List<string> temps, List<string> newPaths, PushOutcome failure)
    {
        await CleanUpAsync(head, temps, newPaths).ConfigureAwait(false);
        return new WriteOutcome.Stop(failure);
    }

    /// <summary>
    /// Decision 10: back to <c>L</c> and clean, the temporary files and the files of new entries removed (an ignored new
    /// file survives <c>clean</c>). Best effort, never cancelled.
    /// </summary>
    private async Task CleanUpAsync(string head, List<string> temps, List<string> newPaths)
    {
        try
        {
            await _client.ResetHardAsync(SyncGit.Revision.NewSha(head), CancellationToken.None).ConfigureAwait(false);
            await _client.CleanAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // best effort: the next push resets the clone again
        }
        foreach (var path in temps.Concat(newPaths))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }
    }
}
