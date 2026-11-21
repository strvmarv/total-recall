using System;
using System.IO;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class ProjectIdentityRuleTests : IDisposable
{
    private readonly string _root;
    private readonly string _home;

    public ProjectIdentityRuleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "trpi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _home = Path.Combine(_root, "home", "me");
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeRepo(string name, string? remoteUrl, string? parent = null)
    {
        var repo = Path.Combine(parent ?? _root, name);
        var git = Path.Combine(repo, ".git");
        Directory.CreateDirectory(git);
        var config = remoteUrl is null
            ? "[core]\n\trepositoryformatversion = 0\n"
            : $"[core]\n\trepositoryformatversion = 0\n[remote \"origin\"]\n\turl = {remoteUrl}\n";
        File.WriteAllText(Path.Combine(git, "config"), config);
        return repo;
    }

    private static string MakeWorktree(string repo, string worktreeDir, string name = "wt")
    {
        var admin = Path.Combine(repo, ".git", "worktrees", name);
        Directory.CreateDirectory(admin);
        File.WriteAllText(Path.Combine(admin, "commondir"), "../..\n");
        Directory.CreateDirectory(worktreeDir);
        File.WriteAllText(Path.Combine(worktreeDir, ".git"), $"gitdir: {admin}\n");
        return worktreeDir;
    }

    private ProjectIdentity Derive(string? headCwd, string dirName = "-x", string? home = null) =>
        ProjectIdentityRule.Derive(headCwd, dirName, new ProjectResolver(), home ?? _home);

    [Fact]
    public void Identity_LiveRepoWithRemote_SlugRemoteRoot()
    {
        var repo = MakeRepo("whatever", "https://github.com/Owner/Repo.git");
        var id = Derive(repo);
        Assert.Equal("owner/repo", id.Key);
        Assert.Equal("remote", id.KeySource);
        Assert.Equal(repo, id.Root);
        Assert.Equal(repo, id.Cwd);
    }

    [Fact]
    public void Identity_LiveRepoNoRemote_FolderLowerPath()
    {
        var repo = MakeRepo("MyRepo", null);
        var id = Derive(Path.Combine(repo));
        Assert.Equal("myrepo", id.Key);
        Assert.Equal("path", id.KeySource);
        Assert.Equal(repo, id.Root);
    }

    [Fact]
    public void Identity_LiveWorktree_SameRootAsDeleted()
    {
        var repo = MakeRepo("Repo", "https://github.com/o/Repo.git");
        var wt = MakeWorktree(repo, Path.Combine(repo, ".claude", "worktrees", "x"));

        var live = Derive(wt);
        Directory.Delete(wt, recursive: true);
        var deleted = Derive(wt);

        Assert.Equal(repo, live.Root);
        Assert.Equal(live.Root, deleted.Root);
        Assert.Equal("o/repo", live.Key);
        Assert.Equal(live.Key, deleted.Key);
    }

    [Theory]
    [InlineData(".claude")]
    [InlineData(".zoid")]
    public void Identity_MissingWorktree_CutBackResolvesRepo(string tool)
    {
        var repo = MakeRepo("Repo", "https://github.com/o/Repo.git");
        var id = Derive(Path.Combine(repo, tool, "worktrees", "x"));
        Assert.Equal("o/repo", id.Key);
        Assert.Equal("remote", id.KeySource);
        Assert.Equal(repo, id.Root);
    }

    [Fact]
    public void Identity_MissingZoidWorktree_Same()
    {
        var repo = MakeRepo("Repo", null);
        var id = Derive(Path.Combine(repo, ".zoid", "worktrees", "x", "sub"));
        Assert.Equal("repo", id.Key);
        Assert.Equal("path", id.KeySource);
        Assert.Equal(repo, id.Root);
    }

    [Fact]
    public void Identity_MissingWorktreeAndRepo_LastSegmentLower()
    {
        var id = Derive("/gone/Repo/.claude/worktrees/x");
        Assert.Equal("repo", id.Key);
        Assert.Equal("path", id.KeySource);
        Assert.Equal("/gone/Repo", id.Root);
    }

    [Fact]
    public void Identity_MissingPlainDir_RootIsCwd()
    {
        var id = Derive("/gone/Plain/Dir");
        Assert.Equal("dir", id.Key);
        Assert.Equal("path", id.KeySource);
        Assert.Equal("/gone/Plain/Dir", id.Root);
    }

    [Fact]
    public void Identity_ExistingDirNoRepo_RootNull_KeyFolder()
    {
        var dir = Path.Combine(_root, "NoRepoHere");
        Directory.CreateDirectory(dir);
        var id = Derive(dir);
        Assert.Equal("norepohere", id.Key);
        Assert.Equal("path", id.KeySource);
        Assert.Null(id.Root);
    }

    [Fact]
    public void Identity_NoHeadCwd_DecodedDirName()
    {
        var id = Derive(null, "-home-u-proj");
        Assert.Equal("/home/u/proj", id.Cwd);
        Assert.Equal("proj", id.Key);
        Assert.Equal("path", id.KeySource);
        Assert.Equal("/home/u/proj", id.Root);

        Assert.Equal("/home/u/proj", Derive("  ", "-home-u-proj").Cwd);
    }

    [Fact]
    public void Identity_NoCwdAtAll_Unknown()
    {
        var id = Derive(null, "");
        Assert.Equal("unknown", id.Key);
        Assert.Equal("path", id.KeySource);
        Assert.Null(id.Root);
        Assert.Null(id.Cwd);
    }

    [Fact]
    public void Identity_OddPaths_WindowsCwd_SplitsOnBackslash()
    {
        var id = Derive(@"C:\Users\me\src\Repo\.claude\worktrees\x");
        Assert.Equal("repo", id.Key);
        Assert.Equal("path", id.KeySource);
        Assert.Equal(@"C:\Users\me\src\Repo", id.Root);

        var plain = Derive(@"C:\Users\me\src\Other");
        Assert.Equal("other", plain.Key);
        Assert.Equal(@"C:\Users\me\src\Other", plain.Root);
    }

    [Fact]
    public void Identity_OddPaths_MixedSeparatorsAndTrailingSeparator()
    {
        var id = Derive("/w/Repo\\.claude/worktrees/x/");
        Assert.Equal("repo", id.Key);
        Assert.Equal("/w/Repo", id.Root);

        var plain = Derive("/w/Repo/");
        Assert.Equal("repo", plain.Key);
        Assert.Equal("/w/Repo", plain.Root);
    }

    [Fact]
    public void Identity_OddPaths_CwdIsHome_RootNull()
    {
        var id = Derive(_home);
        Assert.Null(id.Root);

        Directory.CreateDirectory(Path.Combine(_home, ".git"));
        var withGit = Derive(_home);
        Assert.Null(withGit.Root);
    }

    [Fact]
    public void Identity_OddPaths_MissingCwdIsHome_RootNull()
    {
        var id = ProjectIdentityRule.Derive("/gone/home/me", "-x", new ProjectResolver(), "/gone/home/me/");
        Assert.Null(id.Root);
        Assert.Equal("me", id.Key);
    }

    [Fact]
    public void Identity_OddPaths_RootIsAncestorOfHome_RootNull()
    {
        var repo = MakeRepo("x", "https://github.com/o/x.git");
        var home = Path.Combine(repo, "home");
        Directory.CreateDirectory(home);
        var id = Derive(repo, home: home);
        Assert.Null(id.Root);
        Assert.Equal("o/x", id.Key);

        // A sibling that merely shares a name prefix is not an ancestor of home.
        var sibling = MakeRepo("xy", null);
        Assert.Equal(sibling, Derive(sibling, home: home).Root);
    }

    [Fact]
    public void Identity_OddPaths_FilesystemRoot_RootNull()
    {
        var id = Derive("/");
        Assert.Null(id.Root);
        Assert.Equal("unknown", id.Key);
        Assert.Equal("path", id.KeySource);

        Assert.Null(Derive("/.claude/worktrees/x").Root);
        Assert.Null(Derive(@"C:\").Root);
    }

    [Fact]
    public void Identity_OddPaths_HomeThatIsAGitRepo_RootNull_ForADeeperCwdToo()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".git"));
        var sub = Path.Combine(_home, "notes", "daily");
        Directory.CreateDirectory(sub);

        Assert.Null(Derive(_home).Root);
        Assert.Null(Derive(sub).Root);
    }

    [Fact]
    public void Identity_UnknownHome_OnlySkipsTheHomeCheck()
    {
        var repo = MakeRepo("x", "https://github.com/o/x.git");
        Assert.Equal(repo, Derive(repo, home: "").Root);
        Assert.Null(Derive("/", home: "").Root);
    }
}
