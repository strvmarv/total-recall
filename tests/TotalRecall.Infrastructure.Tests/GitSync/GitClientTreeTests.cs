using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.OutcomeAssert;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Builds commits with plumbing commands in a scratch repository and publishes them to a bare remote.</summary>
internal sealed class PlumbingRepo : IDisposable
{
    private readonly GitTestEnvironment _env = new();
    private readonly string _dir;

    public PlumbingRepo()
    {
        _dir = Path.Combine(_env.Root, "plumbing");
        Directory.CreateDirectory(_dir);
        _env.WriteGitConfig("[user]\n\tname = t\n\temail = t@x\n[commit]\n\tgpgsign = false\n");
        _env.RunGit(_dir, "init", "-b", "main");
    }

    public string Git(params string[] args) => _env.RunGit(_dir, args);

    public string Blob(byte[] content)
    {
        var temp = Path.Combine(_env.Root, "blob-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(temp, content);
        return Git("hash-object", "-w", temp).Trim();
    }

    public void Add(string mode, string sha, string path) => Git("update-index", "--add", "--cacheinfo", $"{mode},{sha},{path}");

    /// <summary>Commits the current index and returns the commit.</summary>
    public string Commit(string message)
    {
        var tree = Git("write-tree").Trim();
        var parent = _env.TryRunGit(_dir, "rev-parse", "--verify", "--quiet", "HEAD");
        var args = parent.ExitCode == 0
            ? new[] { "commit-tree", tree, "-p", parent.Output.Trim(), "-m", message }
            : ["commit-tree", tree, "-m", message];
        var commit = Git(args).Trim();
        Git("update-ref", "refs/heads/main", commit);
        return commit;
    }

    public void Remove(string path) => Git("update-index", "--force-remove", path);

    public void PushTo(string remotePath) => Git("push", remotePath, "main:refs/heads/main");

    public void Dispose() => _env.Dispose();
}

[Collection("git-client")]
public sealed class GitClientTreeTests(RecordingFixture fixture)
{
    private const string GoodId = "3f9c2a7e-0000-4000-8000-000000000000";
    private static SyncGit.Revision Head => SyncGit.Revision.Head;
    private static SyncGit.Revision Sha(string sha) => SyncGit.Revision.NewSha(sha);

    private ClientRig Rig(bool hostile = false) => new(fixture, hostile: hostile);

    [GitFact]
    public async Task LsTree_ListsEveryKindOfEntry_AndEachIsJudgedForTheRightReason()
    {
        using var plumbing = new PlumbingRepo();
        var good = Encoding.UTF8.GetBytes("{}\n");
        var goodSha = plumbing.Blob(good);
        plumbing.Add("100644", goodSha, $"memories/3f/{GoodId}.json");
        plumbing.Add("120000", plumbing.Blob(Encoding.UTF8.GetBytes("../../etc/passwd")), $"memories/3f/{GoodId.Replace("3f9c", "3f9d")}.json");
        plumbing.Add("160000", new string('7', 40), $"memories/3f/3f000000-0000-4000-8000-0000000000aa.json");
        plumbing.Add("100644", goodSha, $"memories/4f/{GoodId}.json"); // wrong shard
        plumbing.Add("100644", goodSha, "memories/ab/..%2f..%2fx.json");
        plumbing.Add("100644", goodSha, "memories/ab/ab.json");
        plumbing.Add("100644", plumbing.Blob(new byte[1_048_577]), "memories/3f/3f111111-0000-4000-8000-000000000000.json"); // 1 MiB + 1
        plumbing.Add("100644", goodSha, $"memories/3f/sub/{GoodId}.json");
        plumbing.Add("100644", goodSha, "manifest.json");
        plumbing.Commit("tree");

        using var remote = BareRemote.Create(commits: 0);
        plumbing.PushTo(remote.Path);
        using var rig = Rig();
        Ok(await rig.Client.CloneAsync(remote.Url, "main"));

        var entries = Ok(await rig.Client.LsTreeAsync(Head));
        var verdicts = entries.ToDictionary(e => e.Path, e => SyncTree.classifyRecordEntry(e));

        Assert.Equal(SyncTree.Verdict.NewAccept(GoodId, goodSha, good.Length), verdicts[$"memories/3f/{GoodId}.json"]);
        Reason(verdicts, $"memories/3f/{GoodId.Replace("3f9c", "3f9d")}.json", SyncTree.RejectReason.Symlink);
        Reason(verdicts, "memories/3f/3f000000-0000-4000-8000-0000000000aa.json", SyncTree.RejectReason.Submodule);
        Reason(verdicts, $"memories/4f/{GoodId}.json", SyncTree.RejectReason.BadShard);
        Reason(verdicts, "memories/ab/..%2f..%2fx.json", SyncTree.RejectReason.BadId);
        Reason(verdicts, "memories/ab/ab.json", SyncTree.RejectReason.BadId);
        Reason(verdicts, "memories/3f/3f111111-0000-4000-8000-000000000000.json", SyncTree.RejectReason.TooLarge);
        Reason(verdicts, $"memories/3f/sub/{GoodId}.json", SyncTree.RejectReason.BadPath);
        Assert.DoesNotContain("manifest.json", verdicts.Keys); // not under memories/

        var root = Ok(await rig.Client.LsTreeRootAsync(Head));
        Assert.Equal(["manifest.json", "memories"], root.Select(e => e.Path).Order(StringComparer.Ordinal));

        // `core.symlinks=false`: a symlink entry of the tree is checked out as a plain file, never as a link
        var symlinkEntry = Path.Combine(rig.CloneDirectory, "memories", "3f", $"{GoodId.Replace("3f9c", "3f9d")}.json");
        Assert.True(File.Exists(symlinkEntry));
        Assert.Null(new FileInfo(symlinkEntry).LinkTarget);
        Assert.Equal("tree", root.Single(e => e.Path == "memories").Kind);
        Assert.Equal("blob", root.Single(e => e.Path == "manifest.json").Kind);
    }

    private static void Reason(IReadOnlyDictionary<string, SyncTree.Verdict> verdicts, string path, SyncTree.RejectReason expected)
    {
        Assert.True(verdicts.TryGetValue(path, out var verdict), $"{path} was not listed");
        Assert.Equal(SyncTree.Verdict.NewReject(path, expected), verdict);
    }

    [GitFact]
    public async Task CatBlob_IsByteForByte_RefusesAnOversizedBlob_AndHonoursTheProcessCap()
    {
        using var plumbing = new PlumbingRepo();
        byte[] bytes = [(byte)'a', 0, (byte)'\r', (byte)'\n', (byte)'b', (byte)'\r', 0xFF, 0x00, (byte)'z'];
        var sha = plumbing.Blob(bytes);
        var big = plumbing.Blob(new byte[1000]);
        plumbing.Add("100644", sha, "a.bin");
        plumbing.Add("100644", big, "big.bin");
        plumbing.Commit("blobs");
        using var remote = BareRemote.Create(commits: 0);
        plumbing.PushTo(remote.Path);

        using var rig = Rig();
        Ok(await rig.Client.CloneAsync(remote.Url, "main"));
        Assert.Equal(bytes, Ok(await rig.Client.CatBlobAsync(sha, 1024)));
        Assert.Equal(bytes, Ok(await rig.Client.CatBlobAsync(sha, bytes.Length))); // exactly the limit
        Failed(await rig.Client.CatBlobAsync(sha, bytes.Length - 1), SyncGit.FailureKind.OutputTooLarge);
        Failed(await rig.Client.CatBlobAsync(big, 100), SyncGit.FailureKind.OutputTooLarge);

        using var capped = new ClientRig(fixture, options: o => o with { MaxStdoutBytes = 100 });
        Ok(await capped.Client.CloneAsync(remote.Url, "main"));
        Failed(await capped.Client.CatBlobAsync(big, 1_000_000), SyncGit.FailureKind.OutputTooLarge);
    }

    [GitFact]
    public async Task Autocrlf_DoesNotChangeBytes()
    {
        using var remote = BareRemote.Create(commits: 0);
        var crlf = Encoding.UTF8.GetBytes("a\r\nb\r\n");
        var path = $"memories/3f/{GoodId}.json";

        using var writer = new ClientRig(fixture);
        writer.Env.WriteGitConfig("[core]\n\tautocrlf = true\n");
        Ok(await writer.Client.InitAsync("main"));
        Ok(await writer.Client.SetOriginAsync(remote.Url));
        var file = Path.Combine(writer.CloneDirectory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, crlf);
        Ok(await writer.Client.StageAsync(SyncGit.StageScope.Memories));
        Ok(await writer.Client.CommitAsync("crlf"));
        var blob = writer.Git("rev-parse", "HEAD:" + path).Trim();
        Assert.Equal(crlf, Ok(await writer.Client.CatBlobAsync(blob, 1024)));
        Ok(await writer.Client.PushAsync("main"));

        using var reader = new ClientRig(fixture);
        reader.Env.WriteGitConfig("[core]\n\tautocrlf = true\n");
        Ok(await reader.Client.CloneAsync(remote.Url, "main"));
        Ok(await reader.Client.ResetHardAsync(SyncGit.Revision.NewRemoteBranch("main")));
        Assert.Equal(crlf, File.ReadAllBytes(Path.Combine(reader.CloneDirectory, path)));
    }

    [GitFact]
    public async Task DiffTree_ReportsAddedModifiedDeletedAndTypeChanged_ButNoRename()
    {
        using var plumbing = new PlumbingRepo();
        var one = plumbing.Blob(Encoding.UTF8.GetBytes("one\n"));
        var two = plumbing.Blob(Encoding.UTF8.GetBytes("two\n"));
        var long1 = plumbing.Blob(Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(0, 200).Select(i => "line " + i)) + "\n"));
        string P(string stem) => $"memories/3f/3f{stem}000-0000-4000-8000-000000000000.json";

        plumbing.Add("100644", one, P("a")); // modified later
        plumbing.Add("100644", one, P("b")); // deleted later
        plumbing.Add("100644", one, P("c")); // type-changed later
        plumbing.Add("100644", long1, P("d")); // renamed later
        var c1 = plumbing.Commit("one");

        plumbing.Add("100644", two, P("a"));
        plumbing.Remove(P("b"));
        plumbing.Add("120000", plumbing.Blob(Encoding.UTF8.GetBytes("target")), P("c"));
        plumbing.Remove(P("d"));
        plumbing.Add("100644", long1, P("e")); // same content as d, a new path (`diff-tree -M` would pair them; the client's argv has `--no-renames`, pinned in SyncGitTests)
        plumbing.Add("100644", two, P("f")); // added
        var c2 = plumbing.Commit("two");

        using var remote = BareRemote.Create(commits: 0);
        plumbing.PushTo(remote.Path);
        using var rig = Rig();
        Ok(await rig.Client.InitAsync("main"));
        Ok(await rig.Client.SetOriginAsync(remote.Url));
        Ok(await rig.Client.FetchAsync("main")); // full history: both commits are present

        var diff = Ok(await rig.Client.DiffTreeAsync(Sha(c1), Sha(c2)));
        var byPath = diff.ToDictionary(e => e.Path);
        Assert.Equal(SyncTree.DiffStatus.Modified, byPath[P("a")].Status);
        Assert.Equal(SyncTree.DiffStatus.Deleted, byPath[P("b")].Status);
        Assert.Equal(SyncTree.DiffStatus.TypeChanged, byPath[P("c")].Status);
        Assert.Equal(SyncTree.DiffStatus.Deleted, byPath[P("d")].Status); // a rename is a delete ...
        Assert.Equal(SyncTree.DiffStatus.Added, byPath[P("e")].Status); // ... and an add
        Assert.Equal(SyncTree.DiffStatus.Added, byPath[P("f")].Status);
        Assert.Equal(6, diff.Count);

        Assert.Equal(
            SyncTree.DiffVerdict.NewRejectedDiff(P("c"), SyncTree.RejectReason.Symlink),
            SyncTree.classifyDiffEntry(byPath[P("c")]));
        Assert.Equal(
            SyncTree.DiffVerdict.NewGone("3fb000-0000-4000-8000-000000000000"),
            SyncTree.classifyDiffEntry(byPath[P("b")]));

        Assert.Empty(Ok(await rig.Client.DiffTreeAsync(Sha(c2), Sha(c2))));

        // a commit a shallow clone does not hold is a failure, never an empty diff
        using var shallow = Rig();
        Ok(await shallow.Client.CloneAsync(remote.Url, "main"));
        var failed = await shallow.Client.DiffTreeAsync(Sha(c1), Head);
        Assert.False(failed.IsOk);
        Assert.Equal(SyncGit.FailureKind.Other, failed.Failure.Failure);
    }
}

/// <summary>An <see cref="IGitProcess"/> that returns a fixed result and records the options it was given.</summary>
internal sealed class FakeGitProcess(GitResult result) : IGitProcess
{
    public List<GitRunOptions> Options { get; } = [];

    public Task<GitResult> RunAsync(SyncGit.GitCommand command, GitContext context, GitRunOptions options, CancellationToken cancellationToken)
    {
        Options.Add(options);
        return Task.FromResult(result);
    }
}

public sealed class GitClientFakeProcessTests
{
    private static (GitClient Client, string Root) Client(FakeGitProcess fake)
    {
        var root = Path.Combine(Path.GetTempPath(), "tr-fake-" + Guid.NewGuid().ToString("N"));
        var paths = GitSyncPaths.For(Path.Combine(root, "x.db"), "https://example.invalid/o/r.git", false);
        Directory.CreateDirectory(Path.Combine(paths.CloneDirectory, ".git"));
        return (new GitClient(fake, "git", paths), root);
    }

    [Fact]
    public async Task CatBlob_ACallersLimitOnlyEverLowersTheProcessDefault()
    {
        var fake = new FakeGitProcess(new GitResult(0, [1, 2, 3], "", null, TimeSpan.Zero));
        var (client, root) = Client(fake);
        try
        {
            Assert.True((await client.CatBlobAsync(new string('a', 40), int.MaxValue)).IsOk);
            Assert.True((await client.CatBlobAsync(new string('a', 40), 100)).IsOk);
            Assert.Equal(GitClient.DefaultMaxStdoutBytes, fake.Options[0].MaxStdoutBytes);
            Assert.Equal(100, fake.Options[1].MaxStdoutBytes);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AMalformedListing_IsAFailureOfKindOther_WithoutTheOutput()
    {
        var fake = new FakeGitProcess(new GitResult(0, Encoding.UTF8.GetBytes("100644 blob nothex 1\tmemories/x\0"), "", null, TimeSpan.Zero));
        var (client, root) = Client(fake);
        try
        {
            var outcome = await client.LsTreeAsync(SyncGit.Revision.Head);
            Assert.False(outcome.IsOk);
            Assert.Equal(SyncGit.FailureKind.Other, outcome.Failure.Failure);
            Assert.StartsWith("unreadable listing: record 0", outcome.Failure.StderrText);
            Assert.Empty(outcome.Failure.Stdout);

            var diff = await client.DiffTreeAsync(SyncGit.Revision.Head, SyncGit.Revision.Head);
            Assert.False(diff.IsOk);
            Assert.Equal(SyncGit.FailureKind.Other, diff.Failure.Failure);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
