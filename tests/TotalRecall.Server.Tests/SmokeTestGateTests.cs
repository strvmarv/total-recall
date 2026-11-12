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
}
