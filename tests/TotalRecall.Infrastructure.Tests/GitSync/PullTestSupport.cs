using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>A theory that needs a usable <c>git</c> (2.28 or later); skipped with a reason when there is none.</summary>
internal sealed class GitTheoryAttribute : TheoryAttribute
{
    public GitTheoryAttribute() => Skip = GitFactAttribute.ComputeReason();
}

/// <summary>
/// Everything a pull test needs with real git: a scratch repository standing for the remote (<see cref="Source"/>,
/// committed to through plumbing so symlinks and submodules are possible), a migrated database file for a joined
/// machine, a clone of the remote, a recording and faulting git process, a client, a guard and a fetcher.
/// </summary>
internal sealed class PullRig : IDisposable
{
    public const string RemoteText = "git@github.com:o/r.git";
    public const string RepoId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string MachineId = "4f2a9c1e";

    public PlumbingRepo Source { get; } = new();
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tr-pull-" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Root, "data", "total-recall.db");
    public MsSqliteConnection Connection { get; }
    public SqliteStore Store { get; }
    public GitSyncPaths Paths { get; }
    public RecordingGitProcess Recorder { get; }
    public Func<SyncGit.GitCommand, GitResult?> Fault { get; set; } = _ => null;
    public GitClient Client { get; }
    public HostIdentity Host { get; }
    public RepoGuard Guard { get; private set; }
    public string Head { get; private set; } = "";

    public PullRig()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        Connection = SyncDb.OpenMigrated(DbPath);
        Store = new SqliteStore(Connection);
        Paths = GitSyncPaths.For(DbPath, RemoteText, caseInsensitive: false);
        Recorder = new RecordingGitProcess(new FaultingGitProcess(new GitProcess(), c => Fault(c)));
        Client = new GitClient(
            Recorder,
            GitExecutable.TryFind() ?? "git",
            Paths,
            new GitClientOptions { EnvironmentOverrides = Source.Env.EnvironmentOverrides });
        Host = new HostIdentity(() => new MachineIdResult(MachineIdKind.Found, "m"), DbPath, false);
        Guard = NewGuard(RemoteText);
    }

    public RepoGuard NewGuard(string? remote, HostIdentity? host = null) =>
        Guard = new RepoGuard(GuardSettings.For(remote), Store, Client, host ?? Host, new StubProbe(() => 404), () => DateTimeOffset.UtcNow);

    public GitSyncFetcher Fetcher(string branch = "main") => new(Client, Guard, Store, branch);

    /// <summary>The identity keys a joined machine has, and a machine id so the scan and the apply can stamp.</summary>
    public void Join(string repoId = RepoId, string? host = null)
    {
        Store.SetMeta(SyncIdentity.MetaKeys.Remote, RemoteText);
        Store.SetMeta(SyncIdentity.MetaKeys.RepoId, repoId);
        Store.SetMeta(SyncIdentity.MetaKeys.Host, host ?? Host.Fingerprint!);
        Store.SetMeta(SyncIdentity.MetaKeys.Machine, MachineId);
    }

    // ---- the remote ----

    public static byte[] ManifestBytes(string repoId = RepoId, string format = "total-recall/v1") =>
        Encoding.UTF8.GetBytes($$"""{"format":"{{format}}","repo_id":"{{repoId}}"}""" + "\n");

    public void PutManifest(byte[]? bytes = null) => PutRaw("manifest.json", bytes ?? ManifestBytes());

    public void PutRaw(string path, byte[] bytes, string mode = "100644") => Source.Add(mode, Source.Blob(bytes), path);

    public void PutText(string path, string text) => PutRaw(path, Encoding.UTF8.GetBytes(text));

    /// <summary>Writes a record file at its canonical path in the next commit.</summary>
    public void PutRecord(SyncRecord.SyncRecord record)
    {
        var bytes = RecordWriter.Write(record).Bytes ?? throw new InvalidOperationException("the record cannot be encoded");
        PutRaw(SyncValidation.recordPath(record.Id), bytes);
    }

    public void Remove(string path) => Source.Remove(path);

    /// <summary>Commits the index of the remote and returns the new head.</summary>
    public string Commit(string message = "commit") => Head = Source.Commit(message);

    /// <summary>Clones the remote into the sync clone directory (what <c>join</c> leaves).</summary>
    public void Clone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.CloneDirectory)!);
        Source.Env.RunGit(Source.Env.Root, "clone", Source.WorkDir, Paths.CloneDirectory);
    }

    public string CloneGit(params string[] args) => Source.Env.RunGit(Paths.CloneDirectory, args);

    public string? CloneRef(string name)
    {
        var (code, output, _) = Source.Env.TryRunGit(Paths.CloneDirectory, "rev-parse", "--verify", "--quiet", name);
        return code == 0 ? output.Trim() : null;
    }

    public string? Last => Store.GetMeta(SyncIdentity.MetaKeys.LastSha);

    public void SetLast(string? sha)
    {
        if (sha is null) Store.DeleteMeta(SyncIdentity.MetaKeys.LastSha);
        else Store.SetMeta(SyncIdentity.MetaKeys.LastSha, sha);
    }

    /// <summary>The commands of one kind recorded so far.</summary>
    public int Count(Func<SyncGit.GitCommand, bool> kind) => Recorder.Calls.Count(c => kind(c.Command));

    public void Dispose()
    {
        Connection.Dispose();
        MsSqliteConnection.ClearAllPools();
        try { GitTestEnvironment.ForceDelete(Root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Source.Dispose();
    }
}
