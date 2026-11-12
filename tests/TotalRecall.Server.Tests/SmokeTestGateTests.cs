// tests/TotalRecall.Server.Tests/SmokeTestGateTests.cs
//
// The version-gated first-run smoke test (issue #51). The gate decides whether
// the smoke queries run (no completed run recorded for this version), runs the
// injected benchmark delegate under a cancellation cap, maps the result to
// SmokeTestResult, and records the version only after a run COMPLETES.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;
using Xunit;

public sealed class SmokeTestGateTests
{
    private sealed class DictMetaStore : IMetaStore
    {
        public readonly Dictionary<string, string> Values = new();
        public bool ThrowOnSet;
        public string? GetMeta(string key) => Values.TryGetValue(key, out var v) ? v : null;
        public void SetMeta(string key, string value)
        {
            if (ThrowOnSet) throw new InvalidOperationException("database is locked");
            Values[key] = value;
        }
        public void DeleteMeta(string key) => Values.Remove(key);
    }

    private static BenchmarkResult Result(double exact, int total = 22)
        => new(total, exact, 1.0, 1.0, 1.0, 5.0, Array.Empty<BenchmarkDetail>());

    private sealed class CountingRunner
    {
        public int Calls;
        public Func<CancellationToken, Task<BenchmarkResult>> Run(BenchmarkResult result)
            => _ => { Calls++; return Task.FromResult(result); };
    }

    [Fact]
    public void NoRecordedVersion_RunsAndReportsPass_RecordsVersion()
    {
        var meta = new DictMetaStore();
        var runner = new CountingRunner();
        var gate = new SmokeTestGate(meta, "4.6.0", runner.Run(Result(19.0 / 22)));

        var result = gate.RunIfNeeded();

        Assert.NotNull(result);
        Assert.True(result!.Passed);
        Assert.Contains("86.4%", result.Notes);
        Assert.Contains("80%", result.Notes);
        Assert.Equal(1, runner.Calls);
        Assert.Equal("4.6.0", meta.GetMeta(SmokeTestGate.MetaKey));
    }

    [Fact]
    public void ExactMatchBelowThreshold_ReportsFailureNamingTheThreshold_StillRecordsVersion()
    {
        var meta = new DictMetaStore();
        var runner = new CountingRunner();
        var gate = new SmokeTestGate(meta, "4.6.0", runner.Run(Result(17.0 / 22)));

        var result = gate.RunIfNeeded();

        Assert.NotNull(result);
        Assert.False(result!.Passed);
        Assert.Contains("77.3%", result.Notes);
        Assert.Contains("below", result.Notes);
        Assert.Contains("80%", result.Notes);
        // The run completed, so the version is recorded: a threshold miss is a
        // real result, not an inability to run.
        Assert.Equal("4.6.0", meta.GetMeta(SmokeTestGate.MetaKey));
    }

    [Fact]
    public void ExactMatchExactlyAtThreshold_Passes()
    {
        var gate = new SmokeTestGate(new DictMetaStore(), "4.6.0", new CountingRunner().Run(Result(0.8, total: 10)));
        Assert.True(gate.RunIfNeeded()!.Passed);
    }

    [Fact]
    public void VersionAlreadyRecorded_SkipsWithoutRunning()
    {
        var meta = new DictMetaStore();
        meta.SetMeta(SmokeTestGate.MetaKey, "4.6.0");
        var runner = new CountingRunner();
        var gate = new SmokeTestGate(meta, "4.6.0", runner.Run(Result(1.0)));

        Assert.Null(gate.RunIfNeeded());
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void SecondCallOnSameVersion_DoesNotRunAgain()
    {
        var meta = new DictMetaStore();
        var runner = new CountingRunner();
        var gate = new SmokeTestGate(meta, "4.6.0", runner.Run(Result(1.0)));

        Assert.NotNull(gate.RunIfNeeded());
        Assert.Null(gate.RunIfNeeded());
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public void RecordedVersionDiffers_RunsAgainAndRecordsTheNewVersion()
    {
        var meta = new DictMetaStore();
        meta.SetMeta(SmokeTestGate.MetaKey, "4.5.0");
        var runner = new CountingRunner();
        var gate = new SmokeTestGate(meta, "4.6.0", runner.Run(Result(1.0)));

        Assert.NotNull(gate.RunIfNeeded());
        Assert.Equal(1, runner.Calls);
        Assert.Equal("4.6.0", meta.GetMeta(SmokeTestGate.MetaKey));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public void UnresolvedVersion_SkipsAndRecordsNothing(string version)
    {
        var meta = new DictMetaStore();
        var runner = new CountingRunner();
        var gate = new SmokeTestGate(meta, version, runner.Run(Result(1.0)));

        Assert.Null(gate.RunIfNeeded());
        Assert.Equal(0, runner.Calls);
        Assert.Null(meta.GetMeta(SmokeTestGate.MetaKey));
    }

    [Fact]
    public void CustomMetaKey_IsUsedForTheGate()
    {
        var meta = new DictMetaStore();
        meta.SetMeta(SmokeTestGate.MetaKey, "4.6.0"); // another machine's record
        var runner = new CountingRunner();
        var gate = new SmokeTestGate(meta, "4.6.0", runner.Run(Result(1.0)), metaKey: "smoke_test_version:box-a");

        Assert.NotNull(gate.RunIfNeeded());
        Assert.Equal("4.6.0", meta.GetMeta("smoke_test_version:box-a"));
    }

    [Fact]
    public void ThrowingRunner_ReportsFailureWithReason_RecordsNothing_NeverThrows()
    {
        var meta = new DictMetaStore();
        var gate = new SmokeTestGate(meta, "4.6.0",
            _ => throw new System.IO.FileNotFoundException("benchmark corpus not found: eval/corpus/memories.jsonl"));

        var result = gate.RunIfNeeded();

        Assert.NotNull(result);
        Assert.False(result!.Passed);
        Assert.Contains("could not run", result.Notes);
        Assert.Contains("corpus not found", result.Notes);
        Assert.Null(meta.GetMeta(SmokeTestGate.MetaKey));
    }

    [Fact]
    public void FaultedTask_IsTreatedLikeAThrow()
    {
        var meta = new DictMetaStore();
        var gate = new SmokeTestGate(meta, "4.6.0",
            _ => Task.FromException<BenchmarkResult>(new InvalidOperationException("embedder failed")));

        var result = gate.RunIfNeeded();

        Assert.False(result!.Passed);
        Assert.Contains("embedder failed", result.Notes);
        Assert.Null(meta.GetMeta(SmokeTestGate.MetaKey));
    }

    [Fact]
    public void RunnerThatHonoursTheTokenPastTheCap_ReportsTimeout_RecordsNothing()
    {
        var meta = new DictMetaStore();
        var gate = new SmokeTestGate(meta, "4.6.0",
            async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Result(1.0);
            },
            timeout: TimeSpan.FromMilliseconds(50));

        var result = gate.RunIfNeeded();

        Assert.NotNull(result);
        Assert.False(result!.Passed);
        Assert.Contains("did not finish", result.Notes);
        Assert.Null(meta.GetMeta(SmokeTestGate.MetaKey));
    }

    [Fact]
    public void SynchronousRunnerThatPollsTheTokenBetweenSteps_IsCancelledAtTheCap()
    {
        // BenchmarkRunner is synchronous and checks the token between embeds, so
        // the cap must reach a runner that never awaits.
        var meta = new DictMetaStore();
        var gate = new SmokeTestGate(meta, "4.6.0",
            ct =>
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    Thread.Sleep(5);
                }
            },
            timeout: TimeSpan.FromMilliseconds(50));

        var result = gate.RunIfNeeded();

        Assert.False(result!.Passed);
        Assert.Contains("did not finish", result.Notes);
        Assert.Null(meta.GetMeta(SmokeTestGate.MetaKey));
    }

    [Fact]
    public void FailingVersionWrite_KeepsTheRealResult()
    {
        var meta = new DictMetaStore { ThrowOnSet = true };
        var gate = new SmokeTestGate(meta, "4.6.0", new CountingRunner().Run(Result(19.0 / 22)));

        var result = gate.RunIfNeeded();

        Assert.NotNull(result);
        Assert.True(result!.Passed);
    }

    [Fact]
    public void FailingVersionRead_ReportsFailureAndNeverThrows()
    {
        var gate = new SmokeTestGate(new ThrowingReadMetaStore(), "4.6.0", new CountingRunner().Run(Result(1.0)));

        var result = gate.RunIfNeeded();

        // The gate cannot tell whether a run is owed; staying silent is wrong
        // only if it throws, so it reports and moves on.
        Assert.NotNull(result);
        Assert.False(result!.Passed);
    }

    private sealed class ThrowingReadMetaStore : IMetaStore
    {
        public string? GetMeta(string key) => throw new InvalidOperationException("database is locked");
        public void SetMeta(string key, string value) { }
        public void DeleteMeta(string key) { }
    }
}
