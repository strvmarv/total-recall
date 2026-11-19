using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Two machines on one bare remote (milestone 1b-ii). <see cref="A"/> owns the remote and seeds it through
/// <c>A.Push.Seed</c>; <see cref="B"/> is built on the same remote with machine id <see cref="MachineB"/> and joins
/// through <see cref="JoinB"/>. Each machine has its own temporary root, database, clone, clocks (<c>Push.Now</c> and
/// <c>Now</c>) and recorder. The two cycles are meant to run one after the other, never at the same time.
/// </summary>
internal sealed class TwoMachineRig : IDisposable
{
    /// <summary>The machine id of <see cref="B"/> (<see cref="PullRig.MachineId"/> is A's).</summary>
    public const string MachineB = "7b3d5e90";

    public CycleRig A { get; } = new();
    public CycleRig B { get; }

    public TwoMachineRig() => B = new CycleRig(A.Push.Remote);

    /// <summary>A joins and puts a manifest and these records on the remote, then clones it (see <see cref="PushRig.Seed"/>).</summary>
    public string Seed(params SyncRecordModule.SyncRecord[] records) => A.Push.Seed(records);

    /// <summary>B joins the same repository as machine <see cref="MachineB"/> and clones the shared remote as it is now.</summary>
    public void JoinB()
    {
        B.Pull.Join(machineId: MachineB);
        Directory.CreateDirectory(Path.GetDirectoryName(B.Pull.Paths.CloneDirectory)!);
        B.Pull.Source.Env.RunGit(B.Pull.Source.Env.Root, "clone", B.Push.Remote.Url, B.Pull.Paths.CloneDirectory);
    }

    public Task<CycleOutcome> RunA(CancellationToken ct = default) => A.Run(ct);

    public Task<CycleOutcome> RunB(CancellationToken ct = default) => B.Run(ct);

    /// <summary>One cycle of A, then one of B.</summary>
    public async Task<(CycleOutcome A, CycleOutcome B)> RunBoth(CancellationToken ct = default)
    {
        var a = await RunA(ct);
        var b = await RunB(ct);
        return (a, b);
    }

    public void Dispose()
    {
        B.Dispose();
        A.Dispose();
    }
}
