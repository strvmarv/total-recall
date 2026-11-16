using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

[Collection("process-environment")]
public sealed class GitTestSupportTests
{
    [GitFact]
    public void RunGit_IgnoresGitVariablesOfTheProcess_AndReadsTheRedirectedConfig()
    {
        var saved = Environment.GetEnvironmentVariable("GIT_DIR");
        try
        {
            Environment.SetEnvironmentVariable("GIT_DIR", "/definitely/not/a/repo");
            using var env = new GitTestEnvironment();
            env.WriteGitConfig("[user]\n\tname = Redirected Name\n");
            Assert.Equal("Redirected Name", env.RunGit(env.Root, "config", "user.name").Trim());
            var repo = Path.Combine(env.Root, "r");
            Directory.CreateDirectory(repo);
            env.RunGit(repo, "init"); // would fail if GIT_DIR were honoured
            Assert.True(Directory.Exists(Path.Combine(repo, ".git")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_DIR", saved);
        }
    }

    [GitFact]
    public void TheHostileConfig_Configures_AndFiresNothingBySelf()
    {
        using var env = new GitTestEnvironment();
        var markers = env.WriteHostileConfig();
        Assert.Equal("true", env.RunGit(env.Root, "config", "commit.gpgsign").Trim());
        Assert.Equal("trunk", env.RunGit(env.Root, "config", "init.defaultBranch").Trim());
        markers.AssertNoneFired();
        File.WriteAllText(Path.Combine(markers.Directory, "x"), "");
        Assert.ThrowsAny<Exception>(markers.AssertNoneFired);
    }

    [GitFact]
    public void TheHostileConfig_Bites_WhenGitIsRunWithoutIsolation()
    {
        using var env = new GitTestEnvironment();
        var markers = env.WriteHostileConfig();
        var repo = Path.Combine(env.Root, "r");
        Directory.CreateDirectory(repo);
        env.RunGit(repo, "init");
        Assert.Equal("trunk", env.RunGit(repo, "symbolic-ref", "--short", "HEAD").Trim());

        File.WriteAllText(Path.Combine(repo, "f.txt"), "abc\n");
        env.RunGit(repo, "add", "f.txt");
        Assert.Equal("ABC", env.RunGit(repo, "show", ":f.txt").Trim());

        // signing is on and no identity is configured: the commit is refused, but its hooks have already run
        var (code, _, _) = env.TryRunGit(repo, "commit", "-m", "x");
        Assert.NotEqual(0, code);
        env.RunGit(repo, "-c", "user.name=t", "-c", "user.email=t@x", "-c", "commit.gpgsign=false", "commit", "-m", "x");

        var clone = Path.Combine(env.Root, "r2");
        env.RunGit(env.Root, "clone", repo, clone);

        foreach (var expected in new[] { "hook-pre-commit", "hook-commit-msg", "hook-post-commit", "hook-post-checkout", "fsmonitor", "filter" })
            Assert.Contains(expected, markers.Fired);
    }

    [GitFact]
    public void BareRemote_CanBeEmpty_AndPushCommitWorksTwiceAndAfterAnotherClientPushed()
    {
        using var empty = BareRemote.Create(commits: 0);
        using var env = new GitTestEnvironment();
        Assert.Equal("", env.RunGit(empty.Path, "for-each-ref").Trim());
        var first = empty.PushCommit("nested/dir/f.txt", "same");
        var second = empty.PushCommit("nested/dir/f.txt", "same");
        Assert.NotEqual(first, second);

        using var other = BareRemote.Create(commits: 1);
        var scratch = Path.Combine(env.Root, "other");
        env.RunGit(env.Root, "clone", other.Path, scratch);
        env.RunGit(scratch, "-c", "user.name=t", "-c", "user.email=t@x", "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "elsewhere");
        env.RunGit(scratch, "push", "origin", "HEAD:refs/heads/main");
        var mine = other.PushCommit();
        Assert.Equal(mine, other.Head());
        Assert.Equal("3", env.RunGit(other.Path, "rev-list", "--count", "main").Trim());
    }

    [Fact]
    public void ForceFlags_DoesNotMistakeACommitMessageForAnOption_AndCatchesAbbreviations()
    {
        var ctx = new SyncGit.BuildContext("/d/repo", "/d/no-hooks", "/d/empty", false);
        foreach (var message in new[] { "-fix: x", "+x", ":x", "--force" })
            ForceFlags.AssertNone([SyncGit.build(ctx, SyncGit.GitCommand.NewCommit(message)).ResultValue.ToArray()]);
        foreach (var bad in new[] { "--forc", "--mirr", "--del", "--force-if-includes", "--prun" })
            Assert.ThrowsAny<Exception>(() => ForceFlags.AssertNone([["push", bad]]));
    }

    [GitFact]
    public void BareRemote_ProducesTheRequestedHistory_AndCanMove()
    {
        using var remote = BareRemote.Create(commits: 3, filesInFirstCommit: 250);
        using var env = new GitTestEnvironment();
        Assert.Equal("3", env.RunGit(remote.Path, "rev-list", "--count", "main").Trim());
        var counts = env.RunGit(remote.Path, "count-objects", "-v");
        var packed = counts.Split('\n').Where(l => l.StartsWith("count:") || l.StartsWith("in-pack:")).Sum(l => int.Parse(l.Split(':')[1].Trim()));
        Assert.True(packed >= 250, counts);
        Assert.StartsWith("file://", remote.Url);

        var before = remote.Head();
        var after = remote.PushCommit();
        Assert.NotEqual(before, after);
        Assert.Equal("4", env.RunGit(remote.Path, "rev-list", "--count", "main").Trim());

        var rewritten = remote.RewriteHistory();
        Assert.NotEqual(after, rewritten);
        var (code, _, _) = env.TryRunGit(remote.Path, "merge-base", "--is-ancestor", after, rewritten);
        Assert.Equal(1, code);
    }

    [Fact]
    public void ForceFlags_ThrowsOnAnythingThatCouldForce_AndPassesTheRealPushAndFetch()
    {
        foreach (var bad in new[] { "--force", "-f", "--force-with-lease", "--force-with-lease=main", "+HEAD:refs/heads/main", "--mirror", ":refs/heads/main", "-fdx" })
            Assert.ThrowsAny<Exception>(() => ForceFlags.AssertNone([["push", bad]]));

        var ctx = new SyncGit.BuildContext("/d/repo", "/d/no-hooks", "/d/empty", false);
        foreach (var command in new[] { SyncGit.GitCommand.NewPush("main"), SyncGit.GitCommand.NewFetch("main"), SyncGit.GitCommand.Clean, SyncGit.GitCommand.NewStage(SyncGit.StageScope.Memories) })
        {
            var argv = SyncGit.build(ctx, command).ResultValue.ToArray();
            ForceFlags.AssertNone([argv]);
        }
        Assert.ThrowsAny<Exception>(() => ForceFlags.AssertNone([["status", "-fd"]]));
    }

    [ShFact]
    public async Task RecordingGitProcess_RecordsTheBuiltArgv_AndDelegates()
    {
        using var d = new StubDirectory();
        var recorder = new RecordingGitProcess(new GitProcess());
        var script = d.Script("exit 0\n");
        var result = await recorder.RunAsync(SyncGit.GitCommand.Version, d.Context(script), new GitRunOptions(TimeSpan.FromSeconds(10)), default);
        Assert.Equal(0, result.ExitCode);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal(["--version"], call.Argv);
    }
}
