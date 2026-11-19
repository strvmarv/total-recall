using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Milestone 1b-ii: the two-machine rig, on behaviour that already exists.</summary>
public sealed class TwoMachineTests : IDisposable
{
    private const string Id = "3f9c2a7e";

    private readonly TwoMachineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [GitFact]
    public async Task AMemoryAddedOnA_ReachesB_AfterTwoCycles()
    {
        _rig.Seed();
        _rig.A.Push.AddMemory(Tier.Warm, Id, "Use bun, not npm.");

        Assert.IsType<CycleOutcome.Done>(await _rig.RunA());
        _rig.JoinB();
        Assert.IsType<CycleOutcome.Done>(await _rig.RunB());

        var onB = _rig.B.Pull.Store.Get(Tier.Warm, ContentType.Memory, Id);
        Assert.NotNull(onB);
        Assert.Equal("Use bun, not npm.", onB!.Content);
        Assert.NotEqual(_rig.A.Pull.DbPath, _rig.B.Pull.DbPath);
        Assert.NotEqual(_rig.A.Pull.Paths.CloneDirectory, _rig.B.Pull.Paths.CloneDirectory);
    }
}
