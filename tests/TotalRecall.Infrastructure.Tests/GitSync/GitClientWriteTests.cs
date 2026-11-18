using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.OutcomeAssert;

namespace TotalRecall.Infrastructure.Tests.GitSync;

[Collection("git-client")]
public sealed class GitClientWriteTests(RecordingFixture fixture)
{
    private const string IdA = "3f9c2a7e-0000-4000-8000-000000000001";
    private const string IdB = "3f9c2a7e-0000-4000-8000-000000000002";
    private static string RecordPath(string id) => $"memories/{id[..2]}/{id}.json";

    private static SyncGit.StageScope Memories => SyncGit.StageScope.Memories;

    private ClientRig Rig(bool hostile = false, Func<GitClientOptions, GitClientOptions>? options = null) =>
        new(fixture, hostile: hostile, options: options);

    private static void Write(ClientRig rig, string relative, string content)
    {
        var path = Path.Combine(rig.CloneDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
    }

    private static async Task CommitFile(ClientRig rig, string relative, string content, string message = "sync")
    {
        Write(rig, relative, content);
        Ok(await rig.Client.StageAsync(Memories));
        Ok(await rig.Client.CommitAsync(message));
    }

    [GitFact]
    public async Task Stage_Commit_Push_AnotherCloneSeesIt_AndTheCommitIsUnsignedAndAnonymous()
    {
        using var remote = BareRemote.Create(commits: 0);
        using var a = Rig();
        a.Env.WriteGitConfig("[commit]\n\tgpgsign = true\n[push]\n\tgpgSign = true\n");
        Ok(await a.Client.InitAsync("main"));
        Ok(await a.Client.SetOriginAsync(remote.Url));
        Write(a, RecordPath(IdA), "{\"a\":1}\n");
        Write(a, RecordPath(IdB), "{\"b\":2}\n");
        Write(a, "manifest.json", "{\"m\":true}\n");
        Ok(await a.Client.StageAsync(Memories));
        Ok(await a.Client.StageAsync(SyncGit.StageScope.Manifest));
        Assert.True(Ok(await a.Client.HasStagedAsync()));
        Ok(await a.Client.CommitAsync("sync: m +2 ~0 -0"));
        Assert.False(Ok(await a.Client.HasStagedAsync()));
        Ok(await a.Client.PushAsync("main"));

        var commit = a.Git("cat-file", "-p", "HEAD");
        Assert.DoesNotContain("gpgsig", commit);
        Assert.Contains("author total-recall <noreply@total-recall.local>", commit);
        Assert.Contains("committer total-recall <noreply@total-recall.local>", commit);

        using var b = Rig();
        Ok(await b.Client.CloneAsync(remote.Url, "main"));
        Ok(await b.Client.ResetHardAsync(SyncGit.Revision.NewRemoteBranch("main")));
        var blobA = b.Git("rev-parse", "HEAD:" + RecordPath(IdA)).Trim();
        Assert.Equal(Encoding.UTF8.GetBytes("{\"a\":1}\n"), Ok(await b.Client.CatBlobAsync(blobA, 1024)));
        Assert.Equal("{\"b\":2}\n", File.ReadAllText(Path.Combine(b.CloneDirectory, RecordPath(IdB))));
        Assert.Equal("{\"m\":true}\n", File.ReadAllText(Path.Combine(b.CloneDirectory, "manifest.json")));
    }

    [GitFact]
    public async Task Isolation_CommitIsUnsignedUnfilteredAndHookFree()
    {
        using var rig = Rig(hostile: true);
        Ok(await rig.Client.InitAsync("main"));
        Write(rig, RecordPath(IdA), "lowercase content\n"); // an uppercasing filter would change it
        Ok(await rig.Client.StageAsync(Memories));
        Ok(await rig.Client.CommitAsync("sync"));

        rig.Markers!.AssertNoneFired();
        var commit = rig.Git("cat-file", "-p", "HEAD");
        Assert.DoesNotContain("gpgsig", commit);
        Assert.Contains("author total-recall <noreply@total-recall.local>", commit);
        Assert.Contains("committer total-recall <noreply@total-recall.local>", commit);
        var blob = rig.Git("rev-parse", "HEAD:" + RecordPath(IdA)).Trim();
        Assert.Equal(Encoding.UTF8.GetBytes("lowercase content\n"), Ok(await rig.Client.CatBlobAsync(blob, 1024)));
        var hooks = Path.Combine(rig.CloneDirectory, ".git", "hooks");
        Assert.True(!Directory.Exists(hooks) || Directory.GetFileSystemEntries(hooks).Length == 0);
    }

    [GitFact]
    public async Task Stage_ScopeWithNothing_StagesNothingAndSucceeds()
    {
        using var rig = Rig();
        Ok(await rig.Client.InitAsync("main"));
        Ok(await rig.Client.StageAsync(SyncGit.StageScope.Attributes));
        Ok(await rig.Client.StageAsync(Memories));
        Assert.False(Ok(await rig.Client.HasStagedAsync()));
    }

    [NonRootShFact(RequiresGit = true)]
    public async Task Stage_AHostileFileName_CannotForgeTheEmptyScopeMessage()
    {
        using var rig = Rig();
        Ok(await rig.Client.InitAsync("main"));
        var hostile = "fatal: pathspec 'memories' did not match any files";
        Write(rig, $"memories/3f/{hostile}", "x");
        var file = Path.Combine(rig.CloneDirectory, "memories", "3f", hostile);
        Posix.Mode(file, UnixFileMode.None);
        try
        {
            try
            {
                File.ReadAllBytes(file);
                return; // the file system or the user ignores the mode (not only root): the case cannot arise here
            }
            catch (UnauthorizedAccessException)
            {
            }
            Assert.False((await rig.Client.StageAsync(Memories)).IsOk);
        }
        finally
        {
            Posix.Mode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [GitFact]
    public async Task Stage_IgnoresARemotesIgnoreRules_SoANewRecordIsNeverSilentlyDropped()
    {
        using var plumbing = new PlumbingRepo();
        plumbing.Add("100644", plumbing.Blob(Encoding.UTF8.GetBytes("*.json\n")), ".gitignore");
        plumbing.Add("100644", plumbing.Blob(Encoding.UTF8.GetBytes("memories/\n")), "memories/.gitignore");
        plumbing.Add("100644", plumbing.Blob(Encoding.UTF8.GetBytes("x")), "README.md");
        plumbing.Commit("ignore rules");
        using var remote = BareRemote.Create(commits: 0);
        plumbing.PushTo(remote.Path);

        using var rig = Rig();
        Ok(await rig.Client.CloneAsync(remote.Url, "main"));
        Write(rig, RecordPath(IdA), "{}\n");
        Ok(await rig.Client.StageAsync(Memories));
        Assert.True(Ok(await rig.Client.HasStagedAsync()), "a record matched by the remote's .gitignore was left unstaged");
        Ok(await rig.Client.CommitAsync("record"));
        Assert.Contains(RecordPath(IdA), rig.Git("ls-tree", "-r", "--name-only", "HEAD"));
    }

    [GitFact]
    public async Task Stage_DeletedRecordsAreStaged()
    {
        using var rig = Rig();
        Ok(await rig.Client.InitAsync("main"));
        await CommitFile(rig, RecordPath(IdA), "{}\n");
        Assert.False(Ok(await rig.Client.HasStagedAsync()));
        File.Delete(Path.Combine(rig.CloneDirectory, RecordPath(IdA)));
        Ok(await rig.Client.StageAsync(Memories));
        Assert.True(Ok(await rig.Client.HasStagedAsync()));
        Ok(await rig.Client.CommitAsync("remove"));
        Assert.Equal("", rig.Git("ls-tree", "-r", "--name-only", "HEAD").Trim());
    }

    [GitFact]
    public async Task Stage_InADamagedClone_IsAFailure()
    {
        using var rig = Rig();
        Ok(await rig.Client.InitAsync("main"));
        Directory.Delete(Path.Combine(rig.CloneDirectory, ".git"), true);
        Assert.False((await rig.Client.StageAsync(Memories)).IsOk);
        Assert.False((await rig.Client.HasStagedAsync()).IsOk);
    }

    [GitFact]
    public async Task Stage_TenThousandRecordFiles_InOneCall()
    {
        using var rig = Rig();
        Ok(await rig.Client.InitAsync("main"));
        for (var i = 0; i < 10_000; i++)
        {
            var id = $"{i:x8}-0000-4000-8000-000000000000";
            var path = Path.Combine(rig.CloneDirectory, RecordPath(id));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{}\n");
        }
        Ok(await rig.Client.StageAsync(Memories));
        Ok(await rig.Client.CommitAsync("many"));
        Assert.Equal("10000", rig.Git("ls-tree", "-r", "--name-only", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ToString());
    }

    [GitFact]
    public async Task Push_AfterTheRemoteMovedOn_IsNonFastForward_AndNothingIsOverwritten()
    {
        using var remote = BareRemote.Create(commits: 1);
        using var a = Rig();
        using var b = Rig();
        Ok(await a.Client.CloneAsync(remote.Url, "main"));
        Ok(await b.Client.CloneAsync(remote.Url, "main"));

        await CommitFile(a, RecordPath(IdA), "{\"from\":\"a\"}\n");
        Ok(await a.Client.PushAsync("main"));
        var aHead = remote.Head();

        await CommitFile(b, RecordPath(IdB), "{\"from\":\"b\"}\n");
        Failed(await b.Client.PushAsync("main"), SyncGit.FailureKind.NonFastForward);
        Assert.Equal(aHead, remote.Head());

        // recover: fetch, reset to the remote, redo the change, push
        Ok(await b.Client.FetchAsync("main"));
        Assert.Equal(Tri.No, Ok(await b.Client.IsAncestorAsync(SyncGit.Revision.Head, SyncGit.Revision.NewRemoteBranch("main"))));
        Ok(await b.Client.ResetHardAsync(SyncGit.Revision.NewRemoteBranch("main")));
        await CommitFile(b, RecordPath(IdB), "{\"from\":\"b\"}\n");
        Ok(await b.Client.PushAsync("main"));
        Assert.NotEqual(aHead, remote.Head());
    }

    [GitFact]
    public async Task Push_RewrittenRemote_StillNeverForces()
    {
        using var remote = BareRemote.Create(commits: 2);
        using var rig = Rig();
        Ok(await rig.Client.CloneAsync(remote.Url, "main"));
        var rewritten = remote.RewriteHistory();
        await CommitFile(rig, RecordPath(IdA), "{}\n");
        var failure = await rig.Client.PushAsync("main");
        Assert.False(failure.IsOk);
        Assert.Equal(SyncGit.FailureKind.NonFastForward, failure.Failure.Failure);
        Assert.Equal(rewritten, remote.Head());
    }

    [GitFact]
    public async Task Commit_WithNothingStaged_Fails_AndAMessageIsStoredVerbatim()
    {
        using var rig = Rig();
        Ok(await rig.Client.InitAsync("main"));
        Assert.False((await rig.Client.CommitAsync("empty")).IsOk);

        var message = "quotes \" ' and $(touch pwned) and `x` and\nnew line é ✓";
        Write(rig, RecordPath(IdA), "{}\n");
        Ok(await rig.Client.StageAsync(Memories));
        Ok(await rig.Client.CommitAsync(message));
        Assert.Equal(message, rig.Git("log", "-1", "--format=%B").TrimEnd('\n'));
        Assert.False(File.Exists(Path.Combine(rig.CloneDirectory, "pwned")));
    }

    private const string IdC = "3f9c2a7e-0000-4000-8000-000000000003";

    [GitFact]
    public async Task StagePaths_StagesExactlyTheNamedPaths_AndStagedSetListsThem()
    {
        using var rig = Rig();
        Ok(await rig.Client.InitAsync("main"));
        await CommitFile(rig, RecordPath(IdA), "{\"a\":1}\n");
        Assert.Empty(Ok(await rig.Client.StagedSetAsync()));

        Write(rig, RecordPath(IdA), "{\"a\":2}\n");
        Write(rig, RecordPath(IdB), "{\"b\":1}\n");
        Write(rig, RecordPath(IdC), "{\"c\":1}\n"); // untracked and not named: never staged
        Ok(await rig.Client.StagePathsAsync([RecordPath(IdA), RecordPath(IdB)]));

        var staged = Ok(await rig.Client.StagedSetAsync()).Select(e => (e.Status, e.Path)).OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        Assert.Equal([("M", RecordPath(IdA)), ("A", RecordPath(IdB))], staged);
        Assert.True(Ok(await rig.Client.HasStagedAsync()));
    }

    [GitFact]
    public async Task StagePaths_APathThatDoesNotExist_IsAFailure_AndAnEmptyOrForeignListIsRefusedBeforeGitStarts()
    {
        using var rig = Rig();
        Ok(await rig.Client.InitAsync("main"));
        await CommitFile(rig, RecordPath(IdA), "{\"a\":1}\n");

        var absent = await rig.Client.StagePathsAsync([RecordPath(IdB)]);   // exit 128: no such path
        Assert.False(absent.IsOk);
        Assert.Equal(128, absent.Failure.ExitCode);
        Assert.False((await rig.Client.StagePathsAsync([RecordPath(IdA)], new CancellationToken(canceled: true))).IsOk);
        Failed(await rig.Client.StagedSetAsync(new CancellationToken(canceled: true)), SyncGit.FailureKind.Cancelled);
        // a value the closed command refuses is a programming error, as for every other command: it throws before a process starts
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Client.StagePathsAsync([]));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Client.StagePathsAsync(["memories"]));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Client.StagePathsAsync(["../outside"]));
        Assert.Empty(Ok(await rig.Client.StagedSetAsync()));
    }

    [GitFact]
    public async Task StagePaths_ADeletedTrackedFileNamedInTheList_IsStagedAsADeletion()
    {
        using var rig = Rig();
        Ok(await rig.Client.InitAsync("main"));
        await CommitFile(rig, RecordPath(IdA), "{\"a\":1}\n");
        File.Delete(Path.Combine(rig.CloneDirectory, RecordPath(IdA)));

        Ok(await rig.Client.StagePathsAsync([RecordPath(IdA)]));

        var staged = Ok(await rig.Client.StagedSetAsync());
        Assert.Equal([("D", RecordPath(IdA))], staged.Select(e => (e.Status, e.Path)).ToList());
    }

    [GitFact]
    public async Task NeverForces_CoversEveryCommandShape()
    {
        using var remote = BareRemote.Create(commits: 1);
        var recorder = new RecordingGitProcess(new GitProcess());
        using var writer = new ClientRig(fixture);
        var client = new GitClient(recorder, GitExecutable.TryFind()!, writer.Paths, new GitClientOptions { EnvironmentOverrides = writer.Env.EnvironmentOverrides });
        Ok(await client.InitAsync("main"));
        Ok(await client.SetOriginAsync(remote.Url));
        Ok(await client.FetchAsync("main"));
        Ok(await client.ResetHardAsync(SyncGit.Revision.NewRemoteBranch("main")));
        Write(writer, RecordPath(IdA), "{}\n");
        Ok(await client.StageAsync(Memories));
        Ok(await client.CommitAsync("sync"));
        Ok(await client.PushAsync("main"));
        Ok(await client.CleanAsync());

        using var other = new ClientRig(fixture);
        var second = new GitClient(recorder, GitExecutable.TryFind()!, other.Paths, new GitClientOptions { EnvironmentOverrides = other.Env.EnvironmentOverrides });
        Ok(await second.CloneAsync(remote.Url, "main"));
        Ok(await second.FetchAsync("main"));
        Ok(await second.ResetHardAsync(SyncGit.Revision.NewRemoteBranch("main")));
        Ok(await second.CleanAsync());

        var commands = recorder.Calls.Select(c => c.Command).ToList();
        Assert.Contains(commands, c => c.IsPush);
        Assert.Contains(commands, c => c.IsFetch);
        Assert.Contains(commands, c => c.IsResetHard);
        Assert.Contains(commands, c => c.IsClean);
        ForceFlags.AssertNone(recorder.Argvs);
    }
}
