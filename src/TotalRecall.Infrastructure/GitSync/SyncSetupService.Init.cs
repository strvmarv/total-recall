using System.Globalization;
using System.Text;
using Microsoft.FSharp.Collections;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

public sealed partial class SyncSetupService
{
    private enum Step { Look, Fetch, Clone, Push }

    private void Progress(string line) => env.Report("  " + line);

    /// <summary>The last non-empty line of git's (already redacted) standard error, short enough to print.</summary>
    private static string GitText(GitResult result)
    {
        var lines = (result.StderrText ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // the closing lines of a failed push ("error: failed to push some refs", "hint:", "To <url>") do not say why
        var line = lines.LastOrDefault(l => !l.StartsWith("error: failed to push", StringComparison.Ordinal)
                                            && !l.StartsWith("hint:", StringComparison.Ordinal)
                                            && !l.StartsWith("To ", StringComparison.Ordinal))
                   ?? lines.LastOrDefault();
        if (string.IsNullOrEmpty(line))
            return "git failed";
        return line.Length > 300 ? line[..300] + "..." : line;
    }

    /// <summary>
    /// Turns a failed git call into the refusal of decisions 5.1, 5.4 and 6 (the same classification for every network
    /// step; <c>OutputTooLarge</c> is <c>not-empty</c> only when listing the refs of <c>init</c>).
    /// </summary>
    private static SetupResult ClassifyFailure(GitResult result, Step step, bool forInit, IReadOnlyList<string>? notes = null)
    {
        notes ??= [];
        var kind = result.Failure;
        if (kind is { IsAuth: true })
            return SetupResult.Refused(SyncSetup.Refusal.NeedsAuth, "git needs credentials this machine does not have: set up an ssh key or a git credential helper, then run this again", notes);
        if (kind is { IsNetwork: true } or { IsTimedOut: true })
        {
            var text = step == Step.Push
                ? "the repository could not be reached; the push may have completed: if running this again says the repository already exists, run `sync join`"
                : "the repository could not be reached: check the address and the network";
            return SetupResult.Refused(SyncSetup.Refusal.Unreachable, text, notes);
        }
        if (kind is { IsRemoteNotFound: true })
            return SetupResult.Refused(SyncSetup.Refusal.Unreachable, "the repository was not found or is not readable", notes);
        if (kind is { IsOutputTooLarge: true } && step == Step.Look && forInit)
            return SetupResult.Refused(SyncSetup.Refusal.NotEmpty, "this repository lists too many refs to be empty: sync needs an empty repository", notes);
        if (kind is { IsNonFastForward: true } && step == Step.Push)
            return SetupResult.Refused(SyncSetup.Refusal.RepoExists, "someone pushed to this repository while it was being set up: if it is your sync repository, run `sync join` instead", notes);
        if (kind is { IsCancelled: true })
        {
            var text = step == Step.Push
                ? "cancelled; the push may have completed: if running this again says the repository already exists, run `sync join`"
                : "cancelled";
            return SetupResult.Refused(SyncSetup.Refusal.Failed, text, notes);
        }
        return SetupResult.Refused(SyncSetup.Refusal.Failed, GitText(result), notes);
    }

    /// <summary>Starts a sync repository at an empty remote (decision 5).</summary>
    public async Task<SetupResult> InitAsync(string address, string? branch, CancellationToken ct = default)
    {
        try
        {
            var (prepared, refusal) = await PrepareAsync(address, branch, ct).ConfigureAwait(false);
            if (refusal is not null)
                return refusal;
            using (prepared)
                return WithNotes(await InitCoreAsync(prepared!, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return WithNotes(FailedFrom("init", ex));
        }
    }

    private async Task<SetupResult> InitCoreAsync(Prepared p, CancellationToken ct)
    {
        var client = p.Client;

        // 5.1, 5.2: one look at the remote, no clone
        _phase = "looking at the repository";
        var refs = await client.LsRemoteRefsAsync(p.Address, onStderr: Progress, cancellationToken: ct).ConfigureAwait(false);
        if (!refs.IsOk)
            return ClassifyFailure(refs.Failure, Step.Look, forInit: true);
        var shape = SyncSetup.classifyRefs(ListModule.OfSeq(refs.Value), p.Branch);
        if (shape.IsBranchPresent)
        {
            return Refuse(
                SyncSetup.Refusal.RepoExists,
                $"this repository already has a '{p.Branch}' branch; if it is your sync repository, run `sync join` instead");
        }
        if (shape.IsOtherRefs)
        {
            return Refuse(
                SyncSetup.Refusal.NotEmpty,
                "this repository is not empty (it has other branches, tags or refs): sync needs an empty repository, so create a new one");
        }

        // 5.3: the first commit, built locally
        _phase = "preparing the first commit";
        var cloneDirectory = p.Paths.CloneDirectory;
        if (Directory.Exists(cloneDirectory))
        {
            var moved = CloneSideline.Move(cloneDirectory, "unpushed", env.Clock(), env.Move); // never deleted: an earlier init's clone may be the only copy
            _notes.Add($"note: the clone of an earlier init was kept at {moved}");
        }

        var init = await client.InitAsync(p.Branch, ct).ConfigureAwait(false);
        if (!init.IsOk)
            return ClassifyFailure(init.Failure, Step.Clone, forInit: true);
        _notes.Add($"note: a clone was kept at {cloneDirectory}");
        var origin = await client.SetOriginAsync(p.Address, ct).ConfigureAwait(false);
        if (!origin.IsOk)
            return ClassifyFailure(origin.Failure, Step.Clone, forInit: true, [$"note: a clone was kept at {cloneDirectory}"]);

        var repoId = SyncSetup.newRepoId(env.Random(16));
        var createdAt = env.Clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        File.WriteAllBytes(Path.Combine(cloneDirectory, ".gitattributes"), Encoding.UTF8.GetBytes("* -text\n"));
        File.WriteAllBytes(Path.Combine(cloneDirectory, "manifest.json"), ManifestFile.Render(repoId, $"total-recall {env.AppVersion}", createdAt));

        string[] kept = [$"note: a clone was kept at {cloneDirectory}"];
        var stageAttributes = await client.StageAsync(SyncGit.StageScope.Attributes, ct).ConfigureAwait(false);
        if (!stageAttributes.IsOk)
            return ClassifyFailure(stageAttributes.Failure, Step.Clone, forInit: true, kept);
        var stageManifest = await client.StageAsync(SyncGit.StageScope.Manifest, ct).ConfigureAwait(false);
        if (!stageManifest.IsOk)
            return ClassifyFailure(stageManifest.Failure, Step.Clone, forInit: true, kept);
        var commit = await client.CommitAsync("Initialise the total-recall sync repository", ct).ConfigureAwait(false);
        if (!commit.IsOk)
            return ClassifyFailure(commit.Failure, Step.Clone, forInit: true, kept);

        // 5.4: the push, never forced
        _phase = "pushing the first commit";
        var push = await client.PushAsync(p.Branch, onStderr: Progress, cancellationToken: ct).ConfigureAwait(false);
        if (!push.IsOk)
            return ClassifyFailure(push.Failure, Step.Push, forInit: true, kept);

        // 5.5, 5.6: record
        IReadOnlyList<string> notes;
        try
        {
            notes = await RecordAsync(p, new RecordPlan(RecordKind.Init, repoId, ReusedClone: false, SameRepository: false), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            Diagnostics.ExceptionLogger.LogChain(Console.Error, $"sync init ({_phase})", ex);
            return Failed(
                $"the repository was initialised but this machine could not record it ({_phase}: {ex.Message}): run `sync join {SyncSetup.displayRemote(p.Address)}`",
                "note: the repository was initialised");
        }

        var lines = new List<string>
        {
            $"initialised a new sync repository at {SyncSetup.displayRemote(p.Address)} (branch {p.Branch})",
            "this machine is set up for git sync (storage.mode = \"git\"), but syncing memories is not part of this build yet",
        };
        lines.AddRange(notes);
        return SetupResult.Done([.. lines]);
    }
}
