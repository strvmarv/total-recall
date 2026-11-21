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
        var repo = MakeRepo("myrepo", "git@github.com:acme-org/widget-service.git");
        Assert.Equal("acme-org/widget-service", new ProjectResolver().Resolve(repo));
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

    private string MakeWorktree(string main, string name)
    {
        var admin = Path.Combine(main, ".git", "worktrees", name);
        Directory.CreateDirectory(admin);
        File.WriteAllText(Path.Combine(admin, "commondir"), "../..\n");
        var wt = Path.Combine(_root, name + "-dir");
        Directory.CreateDirectory(wt);
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {admin}\n");
        return wt;
    }

    [Fact]
    public void ResolveRoot_GitDirectory_IsItsParent()
    {
        var repo = MakeRepo("myrepo", null);
        var nested = Path.Combine(repo, "a", "b");
        Directory.CreateDirectory(nested);
        var r = new ProjectResolver();
        Assert.Equal(repo, r.ResolveRoot(repo));
        Assert.Equal(repo, r.ResolveRoot(nested));
    }

    [Fact]
    public void ResolveRoot_LinkedWorktree_IsMainRepo()
    {
        var main = MakeRepo("mainrepo", "https://github.com/o/mainrepo.git");
        var wt = MakeWorktree(main, "feature");
        var sub = Path.Combine(wt, "src");
        Directory.CreateDirectory(sub);
        var r = new ProjectResolver();
        Assert.Equal(main, r.ResolveRoot(wt));
        Assert.Equal(main, r.ResolveRoot(sub));
    }

    [Fact]
    public void ResolveRoot_Submodule_IsDirHoldingDotGitFile()
    {
        var super = MakeRepo("super", null);
        var modules = Path.Combine(super, ".git", "modules", "sub");
        Directory.CreateDirectory(modules);
        var sub = Path.Combine(super, "vendor", "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, ".git"), "gitdir: ../../.git/modules/sub\n");
        var deeper = Path.Combine(sub, "lib");
        Directory.CreateDirectory(deeper);
        var r = new ProjectResolver();
        Assert.Equal(sub, r.ResolveRoot(sub));
        Assert.Equal(sub, r.ResolveRoot(deeper));
    }

    [Fact]
    public void ResolveRoot_NoRepoOrMissing_IsNull()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        var r = new ProjectResolver();
        Assert.Null(r.ResolveRoot(plain));
        Assert.Null(r.ResolveRoot(Path.Combine(_root, "missing")));
    }
}
