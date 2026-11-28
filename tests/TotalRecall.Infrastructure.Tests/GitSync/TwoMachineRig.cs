using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Two machines on one bare remote (milestone 1b-ii). <see cref="A"/> owns the remote and seeds it through
/// <c>A.Push.Seed</c>; <see cref="B"/> is built on the same remote with machine id <see cref="MachineB"/> and joins
/// through <see cref="JoinB"/>. Each machine has its own temporary root, database, clone, clocks (<c>Push.Now</c> and
/// <c>Now</c>) and recorder. The cycles are meant to run one after the other, never at the same time. A third machine
/// <see cref="T"/> (machine id <see cref="MachineT"/>, built on the same remote the first time it is used) joins through
/// <see cref="JoinT"/>; <see cref="RemoveOnRemote"/> is an ordinary commit by yet another machine.
/// </summary>
internal sealed class TwoMachineRig : IDisposable
{
    /// <summary>The machine id of <see cref="B"/> (<see cref="PullRig.MachineId"/> is A's).</summary>
    public const string MachineB = "7b3d5e90";

    /// <summary>The machine id of <see cref="T"/>.</summary>
    public const string MachineT = "7b3d6f21";

    private readonly Lazy<CycleRig> _t;

    public CycleRig A { get; } = new();
    public CycleRig B { get; }

    /// <summary>The third machine on A's remote, built when first used (the tests that never use it pay nothing).</summary>
    public CycleRig T => _t.Value;

    public TwoMachineRig()
    {
        B = new CycleRig(A.Push.Remote);
        _t = new Lazy<CycleRig>(() => new CycleRig(A.Push.Remote));
    }

    /// <summary>A joins and puts a manifest and these records on the remote, then clones it (see <see cref="PushRig.Seed"/>).</summary>
    public string Seed(params SyncRecordModule.SyncRecord[] records) => A.Push.Seed(records);

    /// <summary>B joins the same repository as machine <see cref="MachineB"/> and clones the shared remote as it is now.</summary>
    public void JoinB()
    {
        B.Pull.Join(machineId: MachineB);
        Directory.CreateDirectory(Path.GetDirectoryName(B.Pull.Paths.CloneDirectory)!);
        B.Pull.Source.Env.RunGit(B.Pull.Source.Env.Root, "clone", B.Push.Remote.Url, B.Pull.Paths.CloneDirectory);
    }

    /// <summary>T joins the same repository as machine <see cref="MachineT"/> and clones the shared remote as it is now.</summary>
    public void JoinT()
    {
        T.Pull.Join(machineId: MachineT);
        Directory.CreateDirectory(Path.GetDirectoryName(T.Pull.Paths.CloneDirectory)!);
        T.Pull.Source.Env.RunGit(T.Pull.Source.Env.Root, "clone", T.Push.Remote.Url, T.Pull.Paths.CloneDirectory);
    }

    public Task<CycleOutcome> RunA(CancellationToken ct = default) => A.Run(ct);

    public Task<CycleOutcome> RunB(CancellationToken ct = default) => B.Run(ct);

    public Task<CycleOutcome> RunT(CancellationToken ct = default) => T.Run(ct);

    /// <summary>
    /// Another machine removes a record by an ordinary commit: a fresh scratch clone of the shared remote (under A's test
    /// root), <c>git rm</c> of the record's path, a commit and a push to <c>main</c>. No machine's clone is touched.
    /// </summary>
    public void RemoveOnRemote(string id)
    {
        var env = A.Pull.Source.Env;
        var dir = Path.Combine(env.Root, "remover-" + Guid.NewGuid().ToString("N"));
        env.RunGit(env.Root, "clone", A.Push.Remote.Url, dir);
        env.RunGit(dir, "rm", "-q", SyncValidation.recordPath(id));
        env.RunGit(dir, "-c", "user.name=Other", "-c", "user.email=other@example.com", "-c", "commit.gpgsign=false",
            "commit", "-q", "-m", "remove " + id);
        env.RunGit(dir, "push", "-q", "origin", "HEAD:main");
    }

    /// <summary>One cycle of A, then one of B.</summary>
    public async Task<(CycleOutcome A, CycleOutcome B)> RunBoth(CancellationToken ct = default)
    {
        var a = await RunA(ct);
        var b = await RunB(ct);
        return (a, b);
    }

    public void Dispose()
    {
        if (_t.IsValueCreated)
            _t.Value.Dispose();
        B.Dispose();
        A.Dispose();
    }
}
