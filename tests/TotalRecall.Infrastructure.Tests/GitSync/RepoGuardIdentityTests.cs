using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class RepoGuardIdentityTests : IDisposable
{
    private const string Remote = "git@github.com:o/r.git";
    private const string IdA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string IdB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private readonly PlumbingRepo _source = new();
    private readonly GuardStore _store = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tr-guard-" + Guid.NewGuid().ToString("N"));

    private readonly string _dbPath;

    private GitSyncPaths Paths { get; }
    private RecordingGitProcess Recorder { get; } = new(new GitProcess());
    private GitClient Client { get; }
    private HostIdentity Host { get; }

    public RepoGuardIdentityTests()
    {
        _dbPath = Path.Combine(_root, "data", "total-recall.db");
        Paths = GitSyncPaths.For(_dbPath, Remote, caseInsensitive: false);
        Client = new GitClient(
            Recorder,
            GitExecutable.TryFind() ?? "git",
            Paths,
            new GitClientOptions { EnvironmentOverrides = _source.Env.EnvironmentOverrides });
        Host = new HostIdentity(() => new MachineIdResult(MachineIdKind.Found, "m"), _dbPath, false);
    }

    public void Dispose()
    {
        _store.Dispose();
        _source.Dispose();
        try { GitTestEnvironment.ForceDelete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] Manifest(string repoId, string format = "total-recall/v1") =>
        Encoding.UTF8.GetBytes($$"""{"format":"{{format}}","repo_id":"{{repoId}}"}""" + "\n");

    private void CommitManifest(byte[] bytes) { _source.Add("100644", _source.Blob(bytes), "manifest.json"); _source.Commit("manifest"); }

    private void Clone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.CloneDirectory)!);
        _source.Env.RunGit(_source.Env.Root, "clone", _source.WorkDir, Paths.CloneDirectory);
    }

    private RepoGuard Guard(string? remote = Remote, HostIdentity? host = null, GitClient? client = null) =>
        new(GuardSettings.For(remote), _store.Store, client ?? Client, host ?? Host, new StubProbe(() => 404), () => DateTimeOffset.UtcNow);

    private void Join(string repoId = IdA, string? remote = Remote, string? host = null)
    {
        if (remote is not null) _store.Store.SetMeta(SyncIdentity.MetaKeys.Remote, remote);
        _store.Store.SetMeta(SyncIdentity.MetaKeys.RepoId, repoId);
        _store.Store.SetMeta(SyncIdentity.MetaKeys.Host, host ?? Host.Fingerprint!);
    }

    private void AssertNoNetworkCommand()
    {
        foreach (var (command, _) in Recorder.Calls)
        {
            Assert.False(
                command.IsClone || command.IsLsRemote || command.IsFetch || command.IsPush || command.IsAddOrigin || command.IsSetOriginUrl,
                $"a network or remote-changing command ran: {command}");
        }
    }

    // ---- ReadManifestAsync ----------------------------------------------------------------------------------

    [GitFact]
    public async Task NoCloneDirectory_And_AnEmptyOne_AreNoClone_WithNoProcess()
    {
        Assert.Equal(ManifestReadKind.NoClone, (await Guard().ReadManifestAsync()).Kind);
        Directory.CreateDirectory(Paths.CloneDirectory);

        Assert.Equal(ManifestReadKind.NoClone, (await Guard().ReadManifestAsync()).Kind);
        Assert.Empty(Recorder.Calls);
    }

    [GitFact]
    public async Task AValidManifest_IsOk_WithTheRepoId()
    {
        CommitManifest(Manifest(IdA));
        Clone();

        var read = await Guard().ReadManifestAsync();

        Assert.Equal(ManifestReadKind.Ok, read.Kind);
        Assert.Equal(IdA, read.Manifest!.RepoId);
    }

    [GitFact]
    public async Task NoManifestFile_IsNoManifest()
    {
        _source.Add("100644", _source.Blob(Encoding.UTF8.GetBytes("x")), "other.txt");
        _source.Commit("other");
        Clone();

        Assert.Equal(ManifestReadKind.NoManifest, (await Guard().ReadManifestAsync()).Kind);
    }

    [GitFact]
    public async Task ASymlink_ASubmodule_AndADirectory_NamedManifestJson_AreInvalid()
    {
        foreach (var shape in new[] { "symlink", "submodule", "directory" })
        {
            using var rig = new RepoGuardIdentityTests();
            switch (shape)
            {
                case "symlink": rig._source.Add("120000", rig._source.Blob(Encoding.UTF8.GetBytes("target")), "manifest.json"); break;
                case "submodule": rig._source.Add("160000", new string('a', 40), "manifest.json"); break;
                default: rig._source.Add("100644", rig._source.Blob(Manifest(IdA)), "manifest.json/x"); break;
            }
            rig._source.Commit(shape);
            rig.Clone();

            var read = await rig.Guard().ReadManifestAsync();

            Assert.True(read.Kind == ManifestReadKind.Invalid, $"{shape}: {read.Kind} {read.Reason}");
        }
    }

    [GitFact]
    public async Task ANewerFormat_IsNewer()
    {
        CommitManifest(Manifest(IdA, "total-recall/v2"));
        Clone();

        var read = await Guard().ReadManifestAsync();

        Assert.Equal(ManifestReadKind.Newer, read.Kind);
        Assert.Equal(2, read.NewerVersion);
    }

    [GitFact]
    public async Task AHalfMadeClone_IsUnreadable()
    {
        _source.Env.RunGit(_source.Env.Root, "init", "-b", "main", Paths.CloneDirectory); // a .git with no commit

        var read = await Guard().ReadManifestAsync();

        Assert.Equal(ManifestReadKind.Unreadable, read.Kind);
        Assert.False(string.IsNullOrEmpty(read.Reason));
    }

    [GitFact]
    public async Task TheBlobCap_And_TheV1Cap()
    {
        CommitManifest([.. Manifest(IdA), .. Enumerable.Repeat((byte)' ', 70000)]);
        Clone();
        Assert.Equal(ManifestReadKind.Unreadable, (await Guard().ReadManifestAsync()).Kind); // over the 64 KiB read cap

        CommitManifest([.. Encoding.UTF8.GetBytes("""{"format":"total-recall/v2"}"""), .. Enumerable.Repeat((byte)' ', 65537)]);
        _source.Env.RunGit(Paths.CloneDirectory, "pull", "--ff-only", "origin", "main");
        Assert.Equal(ManifestReadKind.Unreadable, (await Guard().ReadManifestAsync()).Kind); // newer, but past the cap

        CommitManifest([.. Manifest(IdA), .. Enumerable.Repeat((byte)' ', 4097)]);
        _source.Env.RunGit(Paths.CloneDirectory, "pull", "--ff-only", "origin", "main");
        Assert.Equal(ManifestReadKind.Invalid, (await Guard().ReadManifestAsync()).Kind); // over the v1 cap
    }

    [GitFact]
    public async Task TheRevision_DecidesWhichManifestIsRead()
    {
        CommitManifest(Manifest(IdA));
        Clone();
        CommitManifest(Manifest(IdB));
        _source.Env.RunGit(Paths.CloneDirectory, "fetch", "origin"); // origin/main moves; HEAD does not

        var guard = Guard();

        Assert.Equal(IdA, (await guard.ReadManifestAsync()).Manifest!.RepoId);
        Assert.Equal(IdB, (await guard.ReadManifestAsync(SyncGit.Revision.NewRemoteBranch("main"))).Manifest!.RepoId);
    }

    // ---- EvaluateIdentityAsync ------------------------------------------------------------------------------

    [GitFact]
    public async Task EverythingAgreeing_IsJoined_AndCanSync()
    {
        CommitManifest(Manifest(IdA));
        Clone();
        Join();

        var verdict = await Guard().EvaluateIdentityAsync();

        Assert.True(verdict.Join.IsJoined);
        Assert.False(verdict.HostUnverified);
        Assert.True(verdict.CanSync);
        AssertNoNetworkCommand();
    }

    [GitFact]
    public async Task NotConfigured_NeverJoined_RemoteChanged_HostMoved_RepositoryReplaced()
    {
        CommitManifest(Manifest(IdA));
        Clone();

        Assert.Equal(SyncIdentity.NotJoinedReason.NotConfigured, Reason(await Guard(remote: null).EvaluateIdentityAsync()));
        Assert.Equal(SyncIdentity.NotJoinedReason.NeverJoined, Reason(await Guard().EvaluateIdentityAsync()));

        Join(remote: "git@github.com:o/other.git");
        Assert.Equal(SyncIdentity.NotJoinedReason.RemoteChanged, Reason(await Guard().EvaluateIdentityAsync()));

        Join(host: "some-other-host");
        Assert.Equal(SyncIdentity.NotJoinedReason.DataDirectoryMoved, Reason(await Guard().EvaluateIdentityAsync()));

        Join(repoId: IdB);
        Assert.Equal(SyncIdentity.NotJoinedReason.RepositoryReplaced, Reason(await Guard().EvaluateIdentityAsync()));

        AssertNoNetworkCommand();
    }

    private static SyncIdentity.NotJoinedReason Reason(GuardVerdict verdict) =>
        verdict.Join is SyncIdentity.JoinState.NotJoined notJoined ? notJoined.Item : throw new Xunit.Sdk.XunitException($"joined: {verdict}");

    [GitFact]
    public async Task TheRevision_ReachesTheIdentityCheck_ARecreatedRepositoryIsSeenBeforeTheReset()
    {
        CommitManifest(Manifest(IdA));
        Clone();
        Join(IdA);
        CommitManifest(Manifest(IdB));
        _source.Env.RunGit(Paths.CloneDirectory, "fetch", "origin");
        var guard = Guard();

        Assert.True((await guard.EvaluateIdentityAsync()).Join.IsJoined);
        Assert.Equal(
            SyncIdentity.NotJoinedReason.RepositoryReplaced,
            Reason(await guard.EvaluateIdentityAsync(SyncGit.Revision.NewRemoteBranch("main"))));
    }

    [GitFact]
    public async Task AFailingHostSource_IsHostUnverified_NeverMoved_AndCannotSync()
    {
        CommitManifest(Manifest(IdA));
        Clone();
        Join();
        var failing = new HostIdentity(() => new MachineIdResult(MachineIdKind.Failed, null), _dbPath, false);

        var verdict = await Guard(host: failing).EvaluateIdentityAsync();

        Assert.True(verdict.Join.IsJoined);
        Assert.True(verdict.HostUnverified);
        Assert.False(verdict.CanSync);
    }

    [GitFact]
    public async Task JoinedWithNoClone_IsJoined_CanSync_AndStartsNoProcess()
    {
        Join();

        var verdict = await Guard().EvaluateIdentityAsync();

        Assert.True(verdict.Join.IsJoined);
        Assert.Equal(ManifestReadKind.NoClone, verdict.Manifest.Kind);
        Assert.True(verdict.CanSync);
        Assert.Empty(Recorder.Calls);
    }

    [GitFact]
    public async Task ANewerManifest_CannotSync()
    {
        CommitManifest(Manifest(IdA, "total-recall/v2"));
        Clone();
        Join();

        var verdict = await Guard().EvaluateIdentityAsync();

        Assert.Equal(ManifestReadKind.Newer, verdict.Manifest.Kind);
        Assert.False(verdict.CanSync);
    }

    [GitFact]
    public async Task AMissingInvalidOrUnreadableManifest_CannotSync()
    {
        Join();
        _source.Add("100644", _source.Blob(Encoding.UTF8.GetBytes("x")), "other.txt");
        _source.Commit("other");
        Clone();
        Assert.False((await Guard().EvaluateIdentityAsync()).CanSync); // missing

        _source.Add("100644", _source.Blob(Encoding.UTF8.GetBytes("not json")), "manifest.json");
        _source.Commit("bad");
        _source.Env.RunGit(Paths.CloneDirectory, "pull", "--ff-only", "origin", "main");
        Assert.False((await Guard().EvaluateIdentityAsync()).CanSync); // invalid

        var unreadable = new GitClient(Recorder, "/nonexistent/git", Paths, new GitClientOptions { EnvironmentOverrides = _source.Env.EnvironmentOverrides });
        Assert.False((await Guard(client: unreadable).EvaluateIdentityAsync()).CanSync); // git cannot run
    }

    [Fact]
    public async Task NoRemote_AndNoClient_IsNotConfigured_WithoutTouchingTheClient()
    {
        var verdict = await new RepoGuard(GuardSettings.For(null), _store.Store, null, Host, new StubProbe(() => 404), () => DateTimeOffset.UtcNow)
            .EvaluateIdentityAsync();

        Assert.Equal(SyncIdentity.NotJoinedReason.NotConfigured, Reason(verdict));
        Assert.Empty(Recorder.Calls);
    }

    [Fact]
    public async Task ARemoteWithNoClient_ReadsTheManifestAsNoClone()
    {
        var guard = new RepoGuard(GuardSettings.For(Remote), _store.Store, null, Host, new StubProbe(() => 404), () => DateTimeOffset.UtcNow);

        Assert.Equal(ManifestReadKind.NoClone, (await guard.ReadManifestAsync()).Kind);
    }

    [Fact]
    public void TheHostnameIsNotAnInput_TheFingerprintIsTheCoreHashOfTheIdAndThePath()
    {
        Assert.Equal(SyncIdentity.hostFingerprint("m", Host.CanonicalPath!), Host.Fingerprint);
    }
}
