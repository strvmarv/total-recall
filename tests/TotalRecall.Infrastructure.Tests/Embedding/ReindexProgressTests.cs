using TotalRecall.Infrastructure.Embedding;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Embedding;

public sealed class ReindexProgressTests
{
    [Fact]
    public void StartsIdle()
    {
        var p = new ReindexProgress();
        Assert.Equal(ReindexProgress.Phase.Idle, p.State);
        Assert.Equal(0, p.Done);
    }

    [Fact]
    public void BeginRunning_SetsTotalsAndModel()
    {
        var p = new ReindexProgress();
        p.BeginRunning(total: 100, model: "bge-small-en-v1.5", startedAtUnixMs: 123);
        Assert.Equal(ReindexProgress.Phase.Running, p.State);
        Assert.Equal(100, p.Total);
        Assert.Equal("bge-small-en-v1.5", p.Model);
    }

    [Fact]
    public void Advance_IsCumulativeAndThreadSafe()
    {
        var p = new ReindexProgress();
        p.BeginRunning(1000, "m", 0);
        System.Threading.Tasks.Parallel.For(0, 1000, _ => p.Advance(1));
        Assert.Equal(1000, p.Done);
    }

    [Fact]
    public void Complete_And_Fail_SetTerminalState()
    {
        var p = new ReindexProgress();
        p.BeginRunning(10, "m", 0);
        p.Complete();
        Assert.Equal(ReindexProgress.Phase.Completed, p.State);

        var q = new ReindexProgress();
        q.BeginRunning(10, "m", 0);
        q.Fail("boom");
        Assert.Equal(ReindexProgress.Phase.Failed, q.State);
        Assert.Equal("boom", q.Error);
    }

    [Fact]
    public void Snapshot_IsConsistent()
    {
        var p = new ReindexProgress();
        p.BeginRunning(50, "m", 7);
        p.Advance(20);
        var s = p.Snapshot();
        Assert.Equal(ReindexProgress.Phase.Running, s.State);
        Assert.Equal(20, s.Done);
        Assert.Equal(50, s.Total);
    }
}
