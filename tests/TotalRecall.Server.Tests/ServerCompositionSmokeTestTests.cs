// The composition root builds the smoke-test delegate from the meta store, the
// build version and the backend: sqlite and cortex keep one local record;
// Postgres `_meta` is shared across machines, so its record is per machine.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;
using Xunit;

public sealed class ServerCompositionSmokeTestTests
{
    private sealed class DictMetaStore : IMetaStore
    {
        public readonly Dictionary<string, string> Values = new();
        public string? GetMeta(string key) => Values.TryGetValue(key, out var v) ? v : null;
        public void SetMeta(string key, string value) => Values[key] = value;
        public void DeleteMeta(string key) => Values.Remove(key);
    }

    private static BenchmarkResult Pass()
        => new(22, 19.0 / 22, 1.0, 1.0, 1.0, 5.0, Array.Empty<BenchmarkDetail>());

    [Fact]
    public void LocalBackend_UsesTheSingleSmokeTestVersionKey()
    {
        var meta = new DictMetaStore();
        var smoke = ServerComposition.BuildSmokeTest(meta, "4.6.0", shared: false, run: _ => Task.FromResult(Pass()));

        var result = smoke();

        Assert.True(result!.Passed);
        Assert.Equal("4.6.0", meta.GetMeta("smoke_test_version"));
        Assert.Null(smoke()); // recorded: not run again
    }

    [Fact]
    public void SharedBackend_KeepsTheRecordPerMachine()
    {
        var meta = new DictMetaStore();
        // Another machine already recorded this version in the shared database.
        meta.SetMeta("smoke_test_version:box-a", "4.6.0");
        meta.SetMeta("smoke_test_version", "4.6.0");
        var smoke = ServerComposition.BuildSmokeTest(
            meta, "4.6.0", shared: true, machineName: "box-b", run: _ => Task.FromResult(Pass()));

        var result = smoke();

        Assert.NotNull(result);
        Assert.Equal("4.6.0", meta.GetMeta("smoke_test_version:box-b"));
        Assert.Null(smoke());
    }

    [Fact]
    public void SharedBackend_SameMachineSecondSession_Skips()
    {
        var meta = new DictMetaStore();
        meta.SetMeta("smoke_test_version:box-a", "4.6.0");
        var calls = 0;
        var smoke = ServerComposition.BuildSmokeTest(
            meta, "4.6.0", shared: true, machineName: "box-a", run: _ => { calls++; return Task.FromResult(Pass()); });

        Assert.Null(smoke());
        Assert.Equal(0, calls);
    }

    [Fact]
    public void NewVersion_RunsAgain()
    {
        var meta = new DictMetaStore();
        meta.SetMeta("smoke_test_version", "4.5.0");
        var smoke = ServerComposition.BuildSmokeTest(meta, "4.6.0", shared: false, run: _ => Task.FromResult(Pass()));

        Assert.NotNull(smoke());
        Assert.Equal("4.6.0", meta.GetMeta("smoke_test_version"));
    }

    [Fact]
    public void DefaultRunner_ReportsAMissingCorpusAsAnInabilityToRun()
    {
        // No `run` injected: the default resolves eval/ files from the binary
        // location. Point it at a directory with none and the gate must report it,
        // not throw.
        var meta = new DictMetaStore();
        var smoke = ServerComposition.BuildSmokeTest(
            meta, "4.6.0", shared: false, embedder: new TotalRecall.Server.Tests.TestSupport.RecordingFakeEmbedder(),
            evalDir: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tr-no-eval-" + Guid.NewGuid().ToString("N")));

        var result = smoke();

        Assert.False(result!.Passed);
        Assert.Contains("could not run", result.Notes);
        Assert.Null(meta.GetMeta("smoke_test_version"));
    }
}
