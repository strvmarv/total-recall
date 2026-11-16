using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.GitSync;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// A user config file backed by a real temporary file, with recorded calls and faults a test can switch on:
/// the same text <see cref="ConfigWriter.RenderUserOverrides"/> gives, written only when asked.
/// </summary>
internal sealed class FakeUserConfig(string path) : IUserConfigFile
{
    public string Path { get; } = path;
    public List<IReadOnlyList<(string Key, object Value)>> Renders { get; } = [];
    public List<string> Writes { get; } = [];
    public Func<int, Exception?>? RenderFault { get; set; }
    public Func<int, Exception?>? WriteFault { get; set; }

    public string Render(IReadOnlyList<(string Key, object Value)> keys)
    {
        Renders.Add(keys);
        if (RenderFault?.Invoke(Renders.Count) is { } ex)
            throw ex;
        return ConfigWriter.RenderUserOverrides(Path, keys);
    }

    public void Write(string text)
    {
        if (WriteFault?.Invoke(Writes.Count + 1) is { } ex)
            throw ex;
        Writes.Add(text);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, text);
    }

    public string Current => File.Exists(Path) ? File.ReadAllText(Path) : "";
}

/// <summary>
/// Answers a canned result for chosen commands instead of running them, and can run a hook just before a real command
/// (to make a race or damage a clone between two steps of a service).
/// </summary>
internal sealed class FaultingGitProcess(IGitProcess inner, Func<SyncGit.GitCommand, GitResult?> fault, Action<SyncGit.GitCommand>? before = null) : IGitProcess
{
    public static GitResult Failure(SyncGit.FailureKind kind, string stderr = "canned failure", int exit = 128) =>
        new(exit, [], stderr, kind, TimeSpan.Zero);

    public static GitResult Text(string stdout) =>
        new(0, Encoding.UTF8.GetBytes(stdout), "", null, TimeSpan.Zero);

    public Task<GitResult> RunAsync(SyncGit.GitCommand command, GitContext context, GitRunOptions options, CancellationToken cancellationToken)
    {
        var canned = fault(command);
        if (canned is not null)
            return Task.FromResult(canned);
        before?.Invoke(command);
        return inner.RunAsync(command, context, options, cancellationToken);
    }
}

/// <summary>
/// Everything a setup-service test needs: a temporary data directory, a clean git home for the client (or a hostile one),
/// a fake config file, a stub public probe, a recording process and the injected clock, random source and host identity.
/// Disposing it checks that no recorded git argument forces anything and removes the tree.
/// </summary>
internal sealed class SetupRig : IDisposable
{
    private int _seed = 1;

    /// <summary>Makes the injected random bytes differ from another rig's (each rig starts at 1).</summary>
    public int Seed { set => _seed = value; }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tr-setup-" + Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Root, "data", "total-recall.db");
    public string ConfigPath => Path.Combine(Root, "data", "config.toml");
    public GitTestEnvironment GitEnv { get; } = new();
    public MarkerFiles? Markers { get; }
    public FakeUserConfig Config { get; }
    public RecordingGitProcess Recorder { get; }
    public List<string> Reported { get; } = [];
    public StubProbe Probe { get; set; } = new(() => 404);
    public DateTimeOffset Now { get; set; } = new(2026, 11, 16, 10, 30, 0, TimeSpan.Zero);
    public string MachineIdText { get; set; } = "machine-one";
    public MachineIdKind MachineIdKind { get; set; } = MachineIdKind.Found;
    public Core.Config.GitSyncConfig Settings { get; set; } = GuardSettings.For(null);
    public string Mode { get; set; } = "local";
    public string? PathVariable { get; set; }
    public Func<SyncGit.GitCommand, GitResult?> Fault { get; set; } = _ => null;
    public Action<SyncGit.GitCommand>? Before { get; set; }
    public Func<string, Task>? RecordFault { get; set; }
    public Action<string, string>? Move { get; set; }
    public string? LoadError { get; set; }
    public bool CaseInsensitive { get; set; }
    public HostIdentity Host { get; }

    public SetupRig(bool hostile = false)
    {
        Directory.CreateDirectory(Path.Combine(Root, "data"));
        if (hostile)
            Markers = GitEnv.WriteHostileConfig();
        else
            GitEnv.WriteGitConfig("");
        Config = new FakeUserConfig(ConfigPath);
        Recorder = new RecordingGitProcess(new FaultingGitProcess(new GitProcess(), c => Fault(c), c => Before?.Invoke(c)));
        Host = new HostIdentity(
            () => new MachineIdResult(MachineIdKind, MachineIdKind == MachineIdKind.Failed ? null : MachineIdText),
            DbPath,
            CaseInsensitive);
    }

    public SyncSetupEnvironment Env() => new(
        DbPath,
        CaseInsensitive,
        Settings,
        Mode,
        PathVariable,
        Recorder,
        new GitClientOptions { EnvironmentOverrides = GitEnv.EnvironmentOverrides },
        Probe,
        Config,
        Host,
        () => Now,
        n =>
        {
            var seed = _seed++;
            return Enumerable.Range(0, n).Select(i => (byte)(seed * 16 + i)).ToArray();
        },
        "9.9.9",
        Reported.Add,
        RecordFault,
        Move,
        LoadError);

    public SyncSetupService Service() => new(Env());

    public string SyncDirectory => Path.Combine(Path.GetDirectoryName(DbPath)!, "sync");

    public GitSyncPaths PathsFor(string address) => GitSyncPaths.For(DbPath, address, CaseInsensitive);

    /// <summary>Asserts no database file and no sync directory exist: a refusal before the lock creates neither.</summary>
    public void AssertNothingCreated()
    {
        Assert.False(File.Exists(DbPath), "a database file was created");
        Assert.False(Directory.Exists(SyncDirectory), "the sync directory was created");
    }

    public void Dispose()
    {
        MsSqliteConnection.ClearAllPools();
        try
        {
            ForceFlags.AssertNone(Recorder.Argvs);
        }
        finally
        {
            try
            {
                GitTestEnvironment.ForceDelete(Root);
            }
            catch (IOException)
            {
            }
            GitEnv.Dispose();
        }
    }
}
