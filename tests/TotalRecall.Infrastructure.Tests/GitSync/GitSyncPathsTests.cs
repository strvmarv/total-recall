using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncPathsTests
{
    private static string Dir(string name) => Path.Combine(Path.GetTempPath(), "tr-paths-" + name);

    private static string Db(string dir, string file = "total-recall.db") => Path.Combine(Dir(dir), file);

    private const string Remote = "git@github.com:alice/total-recall-sync.git";

    [Fact]
    public void TheLayoutIsTheSpecsLayout()
    {
        var p = GitSyncPaths.For(Db("a"), Remote, caseInsensitive: false);
        var dbKey = SyncGit.dbKey(false, "total-recall.db");
        var cloneKey = SyncGit.cloneKey(Remote);

        Assert.Equal(Path.GetFullPath(Dir("a")), p.DatabaseDirectory);
        Assert.Equal(Path.Combine(p.DatabaseDirectory, "sync", dbKey), p.SyncDirectory);
        Assert.Equal(Path.Combine(p.SyncDirectory, "lock"), p.LockPath);
        Assert.Equal(Path.Combine(p.SyncDirectory, "no-hooks"), p.NoHooksDirectory);
        Assert.Equal(Path.Combine(p.SyncDirectory, "empty"), p.EmptyFile);
        Assert.Equal(Path.Combine(p.SyncDirectory, cloneKey, "repo"), p.CloneDirectory);
    }

    [Fact]
    public void TwoDatabaseFilesInOneDirectory_ShareNothing()
    {
        var a = GitSyncPaths.For(Db("same", "a.db"), Remote, false);
        var b = GitSyncPaths.For(Db("same", "b.db"), Remote, false);
        Assert.NotEqual(a.SyncDirectory, b.SyncDirectory);
        Assert.NotEqual(a.LockPath, b.LockPath);
        Assert.NotEqual(a.CloneDirectory, b.CloneDirectory);
        Assert.Equal(a.DatabaseDirectory, b.DatabaseDirectory);
    }

    [Fact]
    public void TwoSpellingsOfOneRemote_GiveDifferentClonesUnderOneSyncDirectory()
    {
        var a = GitSyncPaths.For(Db("r"), "https://h/o/r.git", false);
        var b = GitSyncPaths.For(Db("r"), "https://h/o/r", false);
        Assert.Equal(a.SyncDirectory, b.SyncDirectory);
        Assert.Equal(a.LockPath, b.LockPath);
        Assert.NotEqual(a.CloneDirectory, b.CloneDirectory);
    }

    [Fact]
    public void TheSameInputsAlwaysGiveTheSamePaths()
    {
        var a = GitSyncPaths.For(Db("x"), Remote, false);
        var b = GitSyncPaths.For(Db("x"), Remote, false);
        Assert.Equal((a.SyncDirectory, a.LockPath, a.CloneDirectory, a.NoHooksDirectory, a.EmptyFile),
            (b.SyncDirectory, b.LockPath, b.CloneDirectory, b.NoHooksDirectory, b.EmptyFile));
    }

    [Fact]
    public void TheSameFileNameInTwoDirectories_SharesTheKeyButNotTheDirectory()
    {
        var a = GitSyncPaths.For(Db("one"), Remote, false);
        var b = GitSyncPaths.For(Db("two"), Remote, false);
        Assert.Equal(Path.GetFileName(a.SyncDirectory), Path.GetFileName(b.SyncDirectory));
        Assert.NotEqual(a.SyncDirectory, b.SyncDirectory);
    }

    [Fact]
    public void CaseInsensitivityCollapsesTheFileName_AndCaseSensitivityDoesNot()
    {
        Assert.Equal(
            GitSyncPaths.For(Db("c", "A.db"), Remote, true).SyncDirectory,
            GitSyncPaths.For(Db("c", "a.DB"), Remote, true).SyncDirectory);
        Assert.NotEqual(
            GitSyncPaths.For(Db("c", "A.db"), Remote, false).SyncDirectory,
            GitSyncPaths.For(Db("c", "a.db"), Remote, false).SyncDirectory);
    }

    [Fact]
    public void EveryPathIsUnderTheSyncDirectoryOfTheDatabaseDirectory()
    {
        var p = GitSyncPaths.For(Db("under"), "https://h/../../../../../../../../etc/x", false);
        var root = Path.Combine(p.DatabaseDirectory, "sync") + Path.DirectorySeparatorChar;
        foreach (var path in new[] { p.SyncDirectory, p.LockPath, p.NoHooksDirectory, p.EmptyFile, p.CloneDirectory })
            Assert.StartsWith(root, Path.GetFullPath(path));
    }

    [Fact]
    public void InvalidInputsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => GitSyncPaths.For("", Remote, false));
        Assert.Throws<ArgumentException>(() => GitSyncPaths.For(Db("x"), "", false));
        Assert.Throws<ArgumentException>(() => GitSyncPaths.For(Dir("x") + Path.DirectorySeparatorChar, Remote, false));
    }

    [Fact]
    public void EnsureCreated_CreatesTheFiles_IsIdempotent_AndNeverRewritesOrDeletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "tr-ensure-" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = GitSyncPaths.For(Path.Combine(root, "x.db"), Remote, false);
            p.EnsureCreated();
            Assert.True(Directory.Exists(p.SyncDirectory));
            Assert.True(Directory.Exists(p.NoHooksDirectory));
            Assert.Empty(Directory.GetFileSystemEntries(p.NoHooksDirectory));
            Assert.True(File.Exists(p.EmptyFile));
            Assert.Equal(0, new FileInfo(p.EmptyFile).Length);

            File.WriteAllText(p.EmptyFile, "x");
            var planted = Path.Combine(p.SyncDirectory, "planted.txt");
            File.WriteAllText(planted, "keep");
            var plantedHook = Path.Combine(p.NoHooksDirectory, "marker");
            File.WriteAllText(plantedHook, "keep");

            p.EnsureCreated();
            p.EnsureCreated();
            Assert.Equal("x", File.ReadAllText(p.EmptyFile));
            Assert.Equal("keep", File.ReadAllText(planted));
            Assert.Equal("keep", File.ReadAllText(plantedHook));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ForDatabase_HasTheSameSyncDirectoryAndLockAsFor_AndNoClone()
    {
        var db = Db("fordb");
        var withRemote = GitSyncPaths.For(db, Remote, false);
        var without = GitSyncPaths.ForDatabase(db, false);
        Assert.Equal(withRemote.DatabaseDirectory, without.DatabaseDirectory);
        Assert.Equal(withRemote.SyncDirectory, without.SyncDirectory);
        Assert.Equal(withRemote.LockPath, without.LockPath);
        Assert.Equal(withRemote.NoHooksDirectory, without.NoHooksDirectory);
        Assert.Equal(withRemote.EmptyFile, without.EmptyFile);
        Assert.Throws<InvalidOperationException>(() => without.CloneDirectory);
    }

    [Fact]
    public void ForDatabase_KeysOnTheFileName_AndFoldsCaseOnRequest()
    {
        Assert.NotEqual(
            GitSyncPaths.ForDatabase(Db("fdb2", "a.db"), false).SyncDirectory,
            GitSyncPaths.ForDatabase(Db("fdb2", "b.db"), false).SyncDirectory);
        Assert.Equal(
            GitSyncPaths.ForDatabase(Db("fdb3", "A.db"), true).SyncDirectory,
            GitSyncPaths.ForDatabase(Db("fdb3", "a.db"), true).SyncDirectory);
        Assert.NotEqual(
            GitSyncPaths.ForDatabase(Db("fdb4", "A.db"), false).SyncDirectory,
            GitSyncPaths.ForDatabase(Db("fdb4", "a.db"), false).SyncDirectory);
        Assert.Throws<ArgumentException>(() => GitSyncPaths.ForDatabase(" ", false));
    }

    [Fact]
    public void ForDatabase_EnsureCreated_MakesX()
    {
        var root = Path.Combine(Path.GetTempPath(), "tr-paths-fdb5-" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = GitSyncPaths.ForDatabase(Path.Combine(root, "x.db"), false);
            p.EnsureCreated();
            Assert.True(Directory.Exists(p.SyncDirectory));
            Assert.True(Directory.Exists(p.NoHooksDirectory));
            Assert.True(File.Exists(p.EmptyFile));
            Assert.Equal([p.NoHooksDirectory], Directory.GetDirectories(p.SyncDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GitOutcome_CarriesAValueOrAFailure()
    {
        var ok = GitOutcome<int>.Ok(7);
        Assert.True(ok.IsOk);
        Assert.Equal(7, ok.Value);
        Assert.Throws<InvalidOperationException>(() => ok.Failure);

        var failure = new GitResult(128, [], "fatal", SyncGit.FailureKind.Other, TimeSpan.Zero);
        var fail = GitOutcome<int>.Fail(failure);
        Assert.False(fail.IsOk);
        Assert.Same(failure, fail.Failure);
        Assert.Throws<InvalidOperationException>(() => fail.Value);

        Assert.Equal("Ok(7)", ok.ToString());
        Assert.Equal("Failed(exit 128, Other)", fail.ToString());
        Assert.Equal("default", default(GitOutcome<int>).ToString());
        var none = default(GitOutcome<NoValue>);
        Assert.False(none.IsOk);
        Assert.Throws<InvalidOperationException>(() => none.Failure);
        Assert.Throws<ArgumentNullException>(() => GitOutcome<int>.Fail(null!));
    }
}
