using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.OutcomeAssert;

namespace TotalRecall.Infrastructure.Tests.GitSync;

[Collection("git-client")]
public sealed class GitClientSetupTests(RecordingFixture fixture)
{
    private static SyncGit.Revision Rev(string sha) => SyncGit.Revision.NewSha(sha);
    private static SyncGit.Revision Remote => SyncGit.Revision.NewRemoteBranch("main");
    private static SyncGit.RemoteRef Url(string url) => SyncGit.RemoteRef.NewUrl(url);

    private ClientRig Rig(bool hostile = false, string? dbPath = null, Func<GitClientOptions, GitClientOptions>? options = null, string? executable = null) =>
        new(fixture, hostile: hostile, dbPath: dbPath, options: options, executable: executable);

    // ---- version ----------------------------------------------------------------------------------------------

    [GitFact]
    public async Task Version_IsReportedAndSupported()
    {
        using var rig = Rig();
        var (major, minor) = Ok(await rig.Client.VersionAsync());
        Assert.True(SyncGit.isSupportedVersion(major, minor));
    }

    [ShFact]
    public async Task Version_TooOld_AndNoGit_AndGarbage()
    {
        using var d = new StubDirectory();
        using var old = Rig(executable: d.Script("echo 'git version 2.20.0'\n", "old"));
        Failed(await old.Client.VersionAsync(), SyncGit.FailureKind.GitTooOld);

        using var missing = Rig(executable: d.PathOf("not-there"));
        Failed(await missing.Client.VersionAsync(), SyncGit.FailureKind.NoGit);

        using var garbage = Rig(executable: d.Script("echo 'hello'\n", "garbage"));
        Failed(await garbage.Client.VersionAsync(), SyncGit.FailureKind.Other);
    }

    [GitFact]
    public async Task EnsureCreated_RunsOnFirstUse_AndNotBefore()
    {
        using var rig = Rig();
        Assert.False(Directory.Exists(rig.Paths.SyncDirectory), "the constructor touched the file system");
        Ok(await rig.Client.VersionAsync());
        Assert.False(Directory.Exists(rig.Paths.SyncDirectory), "VersionAsync needs no sync directory");

        Ok(await rig.Client.InitAsync("main"));
        Assert.True(Directory.Exists(rig.Paths.NoHooksDirectory));
        Assert.Empty(Directory.GetFileSystemEntries(rig.Paths.NoHooksDirectory));
        Assert.True(File.Exists(rig.Paths.EmptyFile));
        Assert.Equal(0, new FileInfo(rig.Paths.EmptyFile).Length);
    }

    [GitFact]
    public async Task Version_OnAFreshDataDirectory()
    {
        using var rig = Rig();
        Assert.False(Directory.Exists(Path.GetDirectoryName(rig.DbPath)));
        var outcome = await rig.Client.VersionAsync();
        Assert.True(outcome.IsOk, outcome.IsOk ? "" : outcome.Failure.Failure?.ToString());
    }

    [GitFact]
    public async Task SyncDirectoryThatCannotBeCreated_IsAFailedOutcomeOfKindOther()
    {
        using var rig = Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.DbPath)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(rig.DbPath)!, "sync"), "a file where the directory should be");
        Failed(await rig.Client.InitAsync("main"), SyncGit.FailureKind.Other);
    }

    // ---- isolation --------------------------------------------------------------------------------------------

    [GitFact]
    public async Task Isolation_UserConfigDoesNotApply()
    {
        using var remote = BareRemote.Create(commits: 1);
        using var init = Rig(hostile: true);
        Ok(await init.Client.InitAsync("main"));
        Assert.Equal("main", init.Git("symbolic-ref", "--short", "HEAD").Trim());
        var hooks = Path.Combine(init.CloneDirectory, ".git", "hooks");
        Assert.True(!Directory.Exists(hooks) || Directory.GetFileSystemEntries(hooks).Length == 0, "a template was copied into .git/hooks");
        init.Markers!.AssertNoneFired();
        // the user's defaults for the object format and the reference format do not apply either
        Assert.Equal("sha1", init.Git("rev-parse", "--show-object-format").Trim());
        Assert.True(Directory.Exists(Path.Combine(init.CloneDirectory, ".git", "refs", "heads")));
        Assert.False(Directory.Exists(Path.Combine(init.CloneDirectory, ".git", "reftable")));

        using var clone = Rig(hostile: true);
        Ok(await clone.Client.CloneAsync(remote.Url, "main"));
        Assert.True(File.Exists(Path.Combine(clone.CloneDirectory, "file-0-0.txt")));
        clone.Markers!.AssertNoneFired();
        // `clone.defaultRemoteName=upstream` would leave no `origin` for every later fetch and push
        Assert.Equal("origin", clone.Git("remote").Trim());
    }

    private static readonly string[] HostVariables =
        ["GIT_DIR", "GIT_WORK_TREE", "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0", "GIT_CONFIG_KEY_1", "GIT_CONFIG_VALUE_1", "GIT_AUTHOR_NAME", "GIT_TRACE"];

    [GitFact]
    public async Task HostEnvironment_IsIgnored()
    {
        var saved = HostVariables.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            using var remote = BareRemote.Create(commits: 1);
            using var other = Rig();
            using var rig = Rig();
            var markers = new MarkerFiles(Path.Combine(rig.Root, "markers"));
            Directory.CreateDirectory(markers.Directory);
            var hooks = Path.Combine(rig.Root, "evil-hooks");
            Directory.CreateDirectory(hooks);
            foreach (var hook in new[] { "post-checkout", "pre-commit" })
            {
                var path = Path.Combine(hooks, hook);
                File.WriteAllText(path, markers.Script("hook-" + hook));
                if (!OperatingSystem.IsWindows())
                    Posix.Executable(path);
            }

            var otherRepo = Path.Combine(other.Root, "other-repo");
            Directory.CreateDirectory(otherRepo);
            other.Clean.RunGit(otherRepo, "init");

            Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(otherRepo, ".git"));
            Environment.SetEnvironmentVariable("GIT_WORK_TREE", otherRepo);
            // injected configuration: a hooks path (which the client's own override outranks) and a URL rewrite (which it
            // does not set, so dropping the scrub would send the clone to a path that does not exist)
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "2");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "core.hooksPath");
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", hooks);
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_1", "url.file:///nonexistent-replacement/.insteadOf");
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_1", remote.Url);
            Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "evil");
            Environment.SetEnvironmentVariable("GIT_TRACE", "1");

            Ok(await rig.Client.InitAsync("main"));
            Assert.True(Directory.Exists(Path.Combine(rig.CloneDirectory, ".git")));
            using var cloneRig = Rig();
            var segments = new List<string>();
            Ok(await cloneRig.Client.CloneAsync(remote.Url, "main", onStderr: segments.Add));
            Assert.True(File.Exists(Path.Combine(cloneRig.CloneDirectory, "file-0-0.txt")));
            markers.AssertNoneFired();
            Assert.DoesNotContain(segments, s => s.Contains("trace", StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(Path.Combine(otherRepo, ".git", "refs", "remotes")), "the other repository was touched");
        }
        finally
        {
            foreach (var (n, v) in saved)
                Environment.SetEnvironmentVariable(n, v);
        }
    }

    // ---- clone, ls-remote, fetch ------------------------------------------------------------------------------

    [GitFact]
    public async Task Clone_IsShallowWithOneCommit()
    {
        using var remote = BareRemote.Create(commits: 3);
        using var rig = Rig();
        Ok(await rig.Client.CloneAsync(remote.Url, "main"));
        Assert.Equal("1", rig.Git("rev-list", "--count", "HEAD").Trim());
        Assert.True(File.Exists(Path.Combine(rig.CloneDirectory, ".git", "shallow")));
        Assert.Equal(remote.Head(), rig.Git("rev-parse", "HEAD").Trim());
    }

    [GitFact]
    public async Task LsRemote_ReturnsTheHeadWithoutFetching()
    {
        using var remote = BareRemote.Create(commits: 2);
        using var rig = Rig();
        Ok(await rig.Client.CloneAsync(remote.Url, "main"));
        remote.PushCommit();
        var before = rig.ObjectCount();
        var head = Ok(await rig.Client.LsRemoteAsync(SyncGit.RemoteRef.Origin, "main"));
        Assert.Equal(remote.Head(), head);
        Assert.Equal(before, rig.ObjectCount());
        Assert.Equal(remote.Head(), Ok(await rig.Client.LsRemoteAsync(Url(remote.Url), "main")));
    }

    [GitFact]
    public async Task LsRemote_EmptyRemote_MissingBranch_AndExactRefOnly()
    {
        using var empty = BareRemote.Create(commits: 0);
        using var rigA = Rig();
        Assert.Null(Ok(await rigA.Client.LsRemoteAsync(Url(empty.Url), "main")));

        using var remote = BareRemote.Create(commits: 2);
        Assert.Null(Ok(await rigA.Client.LsRemoteAsync(Url(remote.Url), "other")));

        // `refs/heads/foo/refs/heads/main` also matches the pattern `refs/heads/main` by suffix
        var older = rigA.Clean.RunGit(remote.Path, "rev-parse", "main~1").Trim();
        rigA.Clean.RunGit(remote.Path, "update-ref", "refs/heads/foo/refs/heads/main", older);
        Assert.Equal(remote.Head(), Ok(await rigA.Client.LsRemoteAsync(Url(remote.Url), "main")));
    }

    [GitFact]
    public async Task Clone_EmptyRemoteBranch_FailsAsRemoteBranchMissing()
    {
        using var empty = BareRemote.Create(commits: 0);
        using var rig = Rig();
        Failed(await rig.Client.CloneAsync(empty.Url, "main"), SyncGit.FailureKind.RemoteBranchMissing);
    }

    [GitFact]
    public async Task Clone_PlainPathRemote_IsRefusedWhenRelative()
    {
        using var rig = Rig();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Client.CloneAsync("relative/path", "main"));
    }

    [GitFact]
    public async Task Fetch_IsIncremental_AndTheCloneStaysShallow()
    {
        using var remote = BareRemote.Create(commits: 1, filesInFirstCommit: 250);
        using var rig = Rig();
        Ok(await rig.Client.CloneAsync(remote.Url, "main"));
        remote.PushCommit("file-0-0.txt", "changed once\n");
        remote.PushCommit("file-0-0.txt", "changed twice\n");
        var before = rig.ObjectCount();
        Assert.True(before >= 250, $"the clone holds {before} objects");

        var segments = new List<string>();
        Ok(await rig.Client.FetchAsync("main", onStderr: segments.Add));
        Ok(await rig.Client.ResetHardAsync(Remote));

        Assert.Equal(remote.Head(), rig.Git("rev-parse", "HEAD").Trim());
        Assert.True(File.Exists(Path.Combine(rig.CloneDirectory, ".git", "shallow")));
        var growth = rig.ObjectCount() - before;
        Assert.InRange(growth, 1, 20);
        Assert.Contains(segments, s => s.Contains('%'));
    }

    [GitFact]
    public async Task RewrittenRemote_StillFetches_AndAncestryIsNo()
    {
        using var remote = BareRemote.Create(commits: 2);
        using var rig = Rig();
        Ok(await rig.Client.CloneAsync(remote.Url, "main"));
        var old = remote.Head();
        remote.RewriteHistory();

        Ok(await rig.Client.FetchAsync("main"));
        Assert.Equal(Tri.No, Ok(await rig.Client.IsAncestorAsync(Rev(old), Remote)));
        Assert.Equal(Tri.Unknown, Ok(await rig.Client.IsAncestorAsync(Rev(new string('1', 40)), Remote)));
        Assert.Equal(Tri.Yes, Ok(await rig.Client.IsAncestorAsync(Rev(old), Rev(old))));
    }

    // ---- refs and origin --------------------------------------------------------------------------------------

    [GitFact]
    public async Task RevParse_And_UpdateLastSynced()
    {
        using var remote = BareRemote.Create(commits: 1);
        using var rig = Rig();
        Ok(await rig.Client.CloneAsync(remote.Url, "main"));
        var first = remote.Head();
        Assert.Equal(first, Ok(await rig.Client.RevParseAsync(SyncGit.Revision.Head)));
        Assert.Null(Ok(await rig.Client.RevParseAsync(SyncGit.Revision.LastSynced)));

        Ok(await rig.Client.UpdateLastSyncedAsync(first));
        Assert.Equal(first, Ok(await rig.Client.RevParseAsync(SyncGit.Revision.LastSynced)));

        var second = remote.PushCommit();
        Ok(await rig.Client.FetchAsync("main"));
        Ok(await rig.Client.ResetHardAsync(Remote));
        Ok(await rig.Client.UpdateLastSyncedAsync(second));
        Assert.Equal(second, Ok(await rig.Client.RevParseAsync(SyncGit.Revision.LastSynced)));

        // a sha the clone does not hold changes nothing
        Assert.False((await rig.Client.UpdateLastSyncedAsync(new string('2', 40))).IsOk);
        Assert.Equal(second, Ok(await rig.Client.RevParseAsync(SyncGit.Revision.LastSynced)));

        // and the ref can be moved back
        Ok(await rig.Client.UpdateLastSyncedAsync(first));
        Assert.Equal(first, Ok(await rig.Client.RevParseAsync(SyncGit.Revision.LastSynced)));
    }

    [GitFact]
    public async Task SetOrigin_AddsThenUpdates()
    {
        using var rig = Rig();
        Ok(await rig.Client.InitAsync("main"));
        Ok(await rig.Client.SetOriginAsync("https://example.invalid/one.git"));
        Assert.Equal("https://example.invalid/one.git", rig.Git("remote", "get-url", "origin").Trim());
        Ok(await rig.Client.SetOriginAsync("https://example.invalid/two.git"));
        Assert.Equal("https://example.invalid/two.git", rig.Git("remote", "get-url", "origin").Trim());
    }

    // ---- a damaged clone inside another repository ------------------------------------------------------------

    [GitFact]
    public async Task DamagedClone_InsideAnotherRepository_NeverTouchesIt()
    {
        using var scratch = new GitTestEnvironment();
        var repoA = Path.Combine(scratch.Root, "A");
        Directory.CreateDirectory(repoA);
        scratch.WriteGitConfig("[user]\n\tname = t\n\temail = t@x\n[commit]\n\tgpgsign = false\n");
        scratch.RunGit(repoA, "init");
        File.WriteAllText(Path.Combine(repoA, "tracked.txt"), "one\n");
        scratch.RunGit(repoA, "add", "tracked.txt");
        scratch.RunGit(repoA, "commit", "-m", "first");
        File.WriteAllText(Path.Combine(repoA, "tracked.txt"), "dirty\n");
        File.WriteAllText(Path.Combine(repoA, "untracked.txt"), "keep me\n");
        var parentOfClone = new DirectoryInfo(GitSyncPaths.For(Path.Combine(repoA, "total-recall.db"), "https://example.invalid/o/r.git", false).CloneDirectory).Parent!.FullName;
        using var rig = Rig(dbPath: Path.Combine(repoA, "total-recall.db"), options: o => o with { WorkingDirectory = parentOfClone });
        Assert.StartsWith(Path.GetFullPath(repoA), rig.CloneDirectory);

        // an empty `.git` directory passes the client's own check; A is protected by git's refusal, by `--git-dir` and by
        // the ceiling directory (A itself), each of which alone would stop a search upwards
        Directory.CreateDirectory(Path.Combine(rig.CloneDirectory, ".git"));
        var status = scratch.RunGit(repoA, "status", "--porcelain", "--untracked-files=all", "--", ".", ":(exclude)sync");
        Failed(await rig.Client.ResetHardAsync(SyncGit.Revision.Head), SyncGit.FailureKind.NotARepository);
        Failed(await rig.Client.CleanAsync(), SyncGit.FailureKind.NotARepository);
        Assert.Equal(status, scratch.RunGit(repoA, "status", "--porcelain", "--untracked-files=all", "--", ".", ":(exclude)sync"));
        Assert.Equal("dirty\n", File.ReadAllText(Path.Combine(repoA, "tracked.txt")).Replace("\r\n", "\n"));
        Assert.True(File.Exists(Path.Combine(repoA, "untracked.txt")));

        // no `.git` at all: refused by the client itself, without starting git
        Directory.Delete(Path.Combine(rig.CloneDirectory, ".git"));
        status = scratch.RunGit(repoA, "status", "--porcelain", "--untracked-files=all", "--", ".", ":(exclude)sync");
        var calls = rig.Recorder.Calls.Count;
        Failed(await rig.Client.ResetHardAsync(SyncGit.Revision.Head), SyncGit.FailureKind.NotARepository);
        Failed(await rig.Client.CleanAsync(), SyncGit.FailureKind.NotARepository);
        Assert.Equal(calls, rig.Recorder.Calls.Count);
        Assert.Equal(status, scratch.RunGit(repoA, "status", "--porcelain", "--untracked-files=all", "--", ".", ":(exclude)sync"));
    }

    [GitFact]
    public async Task APreCancelledCall_StartsNothing_AndAMissingSyncDirectoryIsRecreated()
    {
        using var rig = Rig();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var calls = rig.Recorder.Calls.Count;
        Failed(await rig.Client.InitAsync("main", cancelled.Token), SyncGit.FailureKind.Cancelled);
        Assert.Equal(calls, rig.Recorder.Calls.Count);
        Assert.False(Directory.Exists(rig.Paths.SyncDirectory));

        Ok(await rig.Client.InitAsync("main"));
        Directory.Delete(rig.Paths.SyncDirectory, true);
        Ok(await rig.Client.InitAsync("main")); // would be NoGit if the working directory stayed missing
    }

    [GitFact]
    public async Task IsAncestor_WithoutARepository_IsNotARepository()
    {
        using var rig = Rig();
        Failed(await rig.Client.IsAncestorAsync(SyncGit.Revision.Head, SyncGit.Revision.Head), SyncGit.FailureKind.NotARepository);
    }

    [GitFact]
    public async Task CloneIsSound_IsTrueForARealClone_AndFalseForAMissingOrHalfMadeOne()
    {
        using var rig = Rig();
        Assert.False(rig.Client.CloneIsSound(), "no directory");

        Directory.CreateDirectory(rig.CloneDirectory);
        Assert.False(rig.Client.CloneIsSound(), "a directory without .git");

        Ok(await rig.Client.InitAsync("main"));
        Assert.True(rig.Client.CloneIsSound());
    }

    [ShFact]
    public async Task CloneIsSound_IsFalseWhenGitIsALink()
    {
        using var rig = Rig();
        Ok(await rig.Client.InitAsync("main"));
        var git = Path.Combine(rig.CloneDirectory, ".git");
        var realGit = Path.Combine(rig.Root, "real-git");
        Directory.Move(git, realGit);
        Directory.CreateSymbolicLink(git, realGit);
        Assert.False(rig.Client.CloneIsSound());
    }

    [ShFact]
    public async Task RemoveStaleLocks_DoesNotFollowLinks()
    {
        using var rig = Rig();
        Ok(await rig.Client.InitAsync("main"));
        var outside = Path.Combine(rig.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "target.lock"), "keep");
        var git = Path.Combine(rig.CloneDirectory, ".git");
        File.CreateSymbolicLink(Path.Combine(git, "link.lock"), Path.Combine(outside, "target.lock"));
        Directory.CreateSymbolicLink(Path.Combine(git, "linked-dir"), outside);
        Assert.Equal(0, rig.Client.RemoveStaleLocks());
        Assert.True(File.Exists(Path.Combine(outside, "target.lock")));

        // a `.git` that is itself a link is left alone
        var realGit = Path.Combine(rig.Root, "real-git");
        Directory.Move(git, realGit);
        Directory.CreateSymbolicLink(git, realGit);
        File.WriteAllText(Path.Combine(realGit, "index.lock"), "");
        Assert.Equal(0, rig.Client.RemoveStaleLocks());
        Assert.True(File.Exists(Path.Combine(realGit, "index.lock")));
    }

    [GitShFact]
    public async Task ALinkInTheClonePath_IsNeverFollowed_ByAnyCloneScopedCall()
    {
        using var scratch = new GitTestEnvironment();
        scratch.WriteGitConfig("[user]\n\tname = t\n\temail = t@x\n[commit]\n\tgpgsign = false\n");
        var user = Path.Combine(scratch.Root, "users-own-repo");
        Directory.CreateDirectory(user);
        scratch.RunGit(user, "init");
        File.WriteAllText(Path.Combine(user, "tracked.txt"), "one\n");
        scratch.RunGit(user, "add", "tracked.txt");
        scratch.RunGit(user, "commit", "-m", "first");
        File.WriteAllText(Path.Combine(user, "notes.txt"), "untracked notes\n");
        Directory.CreateDirectory(Path.Combine(user, "memories"));
        File.WriteAllText(Path.Combine(user, "memories", "m.txt"), "memory\n");
        var before = scratch.RunGit(user, "status", "--porcelain", "--untracked-files=all");

        async Task AssertEverythingRefused(ClientRig rig)
        {
            int CloneScopedCalls() => rig.Recorder.Calls.Count(c => SyncGit.isCloneScoped(c.Command));
            var calls = CloneScopedCalls();
            Failed(await rig.Client.ResetHardAsync(SyncGit.Revision.Head), SyncGit.FailureKind.NotARepository);
            Failed(await rig.Client.CleanAsync(), SyncGit.FailureKind.NotARepository);
            Failed(await rig.Client.StageAsync(SyncGit.StageScope.Memories), SyncGit.FailureKind.NotARepository);
            Failed(await rig.Client.CommitAsync("x"), SyncGit.FailureKind.NotARepository);
            Failed(await rig.Client.FetchAsync("main"), SyncGit.FailureKind.NotARepository);
            Failed(await rig.Client.PushAsync("main"), SyncGit.FailureKind.NotARepository);
            Failed(await rig.Client.RevParseAsync(SyncGit.Revision.Head), SyncGit.FailureKind.NotARepository);
            Failed(await rig.Client.HasStagedAsync(), SyncGit.FailureKind.NotARepository);
            Assert.Equal(0, rig.Client.RemoveStaleLocks());
            Assert.Equal(calls, CloneScopedCalls()); // nothing was started against the clone
            Assert.Equal(before, scratch.RunGit(user, "status", "--porcelain", "--untracked-files=all"));
        }

        // `repo` itself is a link to the user's repository
        using (var rig = Rig())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(rig.CloneDirectory)!);
            Directory.CreateSymbolicLink(rig.CloneDirectory, user);
            await AssertEverythingRefused(rig);
        }

        // `.git` is a link to the user's own `.git`
        using (var rig = Rig())
        {
            Directory.CreateDirectory(rig.CloneDirectory);
            Directory.CreateSymbolicLink(Path.Combine(rig.CloneDirectory, ".git"), Path.Combine(user, ".git"));
            await AssertEverythingRefused(rig);
        }

        // the <cloneKey> directory is a link
        using (var rig = Rig())
        {
            var real = Path.Combine(rig.Root, "real-key-dir");
            Directory.CreateDirectory(Path.Combine(real, "repo"));
            scratch.RunGit(Path.Combine(real, "repo"), "init");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetDirectoryName(rig.CloneDirectory)!)!);
            Directory.CreateSymbolicLink(Path.GetDirectoryName(rig.CloneDirectory)!, real);
            await AssertEverythingRefused(rig);
        }

        // a `.git` that is a file (`gitdir:` pointer)
        using (var rig = Rig())
        {
            Directory.CreateDirectory(rig.CloneDirectory);
            File.WriteAllText(Path.Combine(rig.CloneDirectory, ".git"), $"gitdir: {Path.Combine(user, ".git")}\n");
            await AssertEverythingRefused(rig);
        }
    }

    [GitFact]
    public async Task RemoveStaleLocks_RemovesOnlyLocksInsideDotGit()
    {
        using var rig = Rig();
        Ok(await rig.Client.InitAsync("main"));
        var git = Path.Combine(rig.CloneDirectory, ".git");
        File.WriteAllText(Path.Combine(git, "index.lock"), "");
        File.WriteAllText(Path.Combine(git, "HEAD.lock"), "");
        File.WriteAllText(Path.Combine(git, "refs", "heads", "main.lock"), "");
        File.WriteAllText(Path.Combine(rig.CloneDirectory, "keep.lock"), "");
        Directory.CreateDirectory(Path.Combine(rig.CloneDirectory, "memories"));
        File.WriteAllText(Path.Combine(rig.CloneDirectory, "memories", "x.lock"), "");

        Assert.Equal(3, rig.Client.RemoveStaleLocks());
        Assert.False(File.Exists(Path.Combine(git, "index.lock")));
        Assert.False(File.Exists(Path.Combine(git, "HEAD.lock")));
        Assert.False(File.Exists(Path.Combine(git, "refs", "heads", "main.lock")));
        Assert.True(File.Exists(Path.Combine(rig.CloneDirectory, "keep.lock")));
        Assert.True(File.Exists(Path.Combine(rig.CloneDirectory, "memories", "x.lock")));
        rig.Git("status"); // works again
        Assert.Equal(0, rig.Client.RemoveStaleLocks());
    }

    [GitFact]
    public async Task ASha256Remote_CanBeClonedAndRead()
    {
        using var scratch = new GitTestEnvironment();
        scratch.WriteGitConfig("[user]\n\tname = t\n\temail = t@x\n[commit]\n\tgpgsign = false\n");
        var work = Path.Combine(scratch.Root, "work");
        var bare = Path.Combine(scratch.Root, "remote.git");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(bare);
        scratch.RunGit(work, "init", "--object-format=sha256", "-b", "main");
        scratch.RunGit(bare, "init", "--bare", "--object-format=sha256", "-b", "main");
        File.WriteAllText(Path.Combine(work, "a.txt"), "hello\n");
        scratch.RunGit(work, "add", "a.txt");
        scratch.RunGit(work, "commit", "-m", "one");
        scratch.RunGit(work, "push", bare, "HEAD:refs/heads/main");
        var head = scratch.RunGit(work, "rev-parse", "HEAD").Trim();
        Assert.Equal(64, head.Length);

        using var rig = Rig();
        Assert.Equal(head, Ok(await rig.Client.LsRemoteAsync(Url(new Uri(bare).AbsoluteUri), "main")));
        Ok(await rig.Client.CloneAsync(new Uri(bare).AbsoluteUri, "main"));
        Assert.Equal(head, Ok(await rig.Client.RevParseAsync(SyncGit.Revision.Head)));
        Assert.Equal(["a.txt"], rig.Git("ls-tree", "--name-only", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [ShFact]
    public async Task ADatabaseDirectoryWithAPathListSeparator_FailsClosed_ExceptForTheVersionCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "tr-colon-" + Guid.NewGuid().ToString("N"), "d" + Path.PathSeparator + "b");
        using var rig = Rig(dbPath: Path.Combine(root, "tr.db"));
        try
        {
            Ok(await rig.Client.VersionAsync());
            var failure = await rig.Client.LsRemoteAsync(Url("file:///nonexistent/x.git"), "main");
            Assert.False(failure.IsOk);
            Assert.Equal(SyncGit.FailureKind.Other, failure.Failure.Failure);
            Assert.Contains("ceiling", failure.Failure.StderrText);
            Failed(await rig.Client.InitAsync("main"), SyncGit.FailureKind.Other);
            Assert.False(Directory.Exists(rig.CloneDirectory));
        }
        finally
        {
            var top = Directory.GetParent(root)!.FullName;
            if (Directory.Exists(top))
                Directory.Delete(top, true);
        }
    }

    [GitFact]
    public async Task CeilingDirectory_KeepsCommandsOutOfTheEnclosingRepository()
    {
        using var remote = BareRemote.Create(commits: 1);
        using var scratch = new GitTestEnvironment();
        var enclosing = Path.Combine(scratch.Root, "enclosing");
        Directory.CreateDirectory(enclosing);
        scratch.RunGit(enclosing, "init");
        // the enclosing repository's own configuration rewrites the remote's URL to a path that does not exist
        scratch.RunGit(enclosing, "config", $"url.file:///nonexistent-replacement/.insteadOf", remote.Url);

        using var rig = Rig(dbPath: Path.Combine(enclosing, "data", "total-recall.db"));
        var head = Ok(await rig.Client.LsRemoteAsync(Url(remote.Url), "main"));
        Assert.Equal(remote.Head(), head);
    }
}
