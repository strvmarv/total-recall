using System.IO;
using TotalRecall.Infrastructure.Memory;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Memory;

public class ProjectResolverTests : IDisposable
{
    private readonly string _root;

    public ProjectResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "trpr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeRepo(string name, string? remoteUrl)
    {
        var repo = Path.Combine(_root, name);
        var git = Path.Combine(repo, ".git");
        Directory.CreateDirectory(git);
        var config = remoteUrl is null
            ? "[core]\n\trepositoryformatversion = 0\n"
            : $"[core]\n\trepositoryformatversion = 0\n[remote \"origin\"]\n\turl = {remoteUrl}\n";
        File.WriteAllText(Path.Combine(git, "config"), config);
        return repo;
    }

    [Fact]
    public void Resolves_https_remote_to_slug()
    {
        var repo = MakeRepo("myrepo", "https://github.com/strvmarv/total-recall.git");
        Assert.Equal("strvmarv/total-recall", new ProjectResolver().Resolve(repo));
    }

    [Fact]
    public void Resolves_ssh_remote_to_slug()
    {
        var repo = MakeRepo("myrepo", "git@github.com:radancy-pe/rai-ops-cortex.git");
        Assert.Equal("radancy-pe/rai-ops-cortex", new ProjectResolver().Resolve(repo));
    }

    [Fact]
    public void Falls_back_to_lowercase_folder_name_when_no_remote()
    {
        var repo = MakeRepo("MyRepo", null);
        Assert.Equal("myrepo", new ProjectResolver().Resolve(repo));
    }

    [Fact]
    public void Resolves_from_nested_subdirectory()
    {
        var repo = MakeRepo("myrepo", "https://github.com/o/r.git");
        var nested = Path.Combine(repo, "a", "b", "c");
        Directory.CreateDirectory(nested);
        Assert.Equal("o/r", new ProjectResolver().Resolve(nested));
    }

    [Fact]
    public void Returns_null_when_no_git_ancestor()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        Assert.Null(new ProjectResolver().Resolve(plain));
    }

    [Fact]
    public void Returns_null_for_nonexistent_dir()
        => Assert.Null(new ProjectResolver().Resolve(Path.Combine(_root, "does-not-exist")));

    [Fact]
    public void Resolves_linked_worktree_via_commondir_to_main_repo()
    {
        // main repo with origin
        var main = MakeRepo("mainrepo", "https://github.com/o/mainrepo.git");
        var mainGit = Path.Combine(main, ".git");
        // worktree admin dir under main/.git/worktrees/wt
        var wtAdmin = Path.Combine(mainGit, "worktrees", "wt");
        Directory.CreateDirectory(wtAdmin);
        File.WriteAllText(Path.Combine(wtAdmin, "commondir"), "../..\n");
        // linked worktree dir with a .git FILE pointing at the admin dir
        var wt = Path.Combine(_root, "feature-branch");
        Directory.CreateDirectory(wt);
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {wtAdmin}\n");

        Assert.Equal("o/mainrepo", new ProjectResolver().Resolve(wt));
    }

    [Fact]
    public void Memoizes_same_cwd_and_redetects_different_cwd()
    {
        var a = MakeRepo("repo-a", "https://github.com/o/a.git");
        var b = MakeRepo("repo-b", "https://github.com/o/b.git");
        var r = new ProjectResolver();
        Assert.Equal("o/a", r.Resolve(a));
        Assert.Equal("o/a", r.Resolve(a)); // cached
        Assert.Equal("o/b", r.Resolve(b)); // different key re-resolves
    }
}
