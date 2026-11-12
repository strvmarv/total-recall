// tests/TotalRecall.Server.Tests/SessionLifecycleSmokeTestTests.cs
//
// session_start reports the first-run smoke test in `smokeTest` (issue #51).
// The lifecycle owns the skip rules (a background re-embed that has not
// finished, an embedder that is not the bundled local model) and contains any
// failure of the smoke delegate: session_start must never fail because of it.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public sealed class SessionLifecycleSmokeTestTests
{
    private sealed class NoCompactionLog : ICompactionLogReader
    {
        public long? GetLastTimestampExcludingReason(string excludedReason) => null;
        public IReadOnlyList<CompactionAnalyticsRow> GetAllForAnalytics(long? sinceTimestamp = null)
            => Array.Empty<CompactionAnalyticsRow>();
        public IReadOnlyList<CompactionMovementRow> GetRecentMovements(int limit)
            => Array.Empty<CompactionMovementRow>();
        public CompactionMovementRow? GetByTargetEntryId(string targetEntryId) => null;
    }

    private sealed class DescribedEmbedder : IEmbedder
    {
        public DescribedEmbedder(EmbedderDescriptor d) { Descriptor = d; }
        public EmbedderDescriptor Descriptor { get; }
        public float[] Embed(string text) => new float[Descriptor.Dimensions];
    }

    private static readonly SmokeTestResult Ok = new(true, "22 queries, exact match 86.4% (threshold 80%)");

    private static async Task<(SessionInitResult Result, int Calls)> Run(
        Func<SmokeTestResult?>? smoke,
        ReindexProgress? reindex = null,
        IEmbedder? embedder = null)
    {
        var calls = 0;
        Func<SmokeTestResult?>? counted = smoke is null ? null : () => { calls++; return smoke(); };
        var conn = SqliteConnection.Open(":memory:");
        using (conn)
        {
            MigrationRunner.RunMigrations(conn);
            var lifecycle = new SessionLifecycle(
                importers: new List<IImporter>(),
                store: new SqliteStore(conn),
                compactionLog: new NoCompactionLog(),
                sessionId: "s-" + Guid.NewGuid().ToString("N"),
                nowMs: () => 1_000_000_000_000L,
                embedder: embedder,
                reindexProgress: reindex,
                smokeTest: counted);
            return (await lifecycle.EnsureInitializedAsync(), calls);
        }
    }

    private static DescribedEmbedder Local() => new(new EmbedderDescriptor("local", "bge-small-en-v1.5", "r", 384));

    [Fact]
    public async Task NoSmokeTestWired_LeavesSmokeTestNull()
        => Assert.Null((await Run(null)).Result.SmokeTest);

    [Fact]
    public async Task SmokeTestWired_PopulatesSmokeTestOnFirstInit()
    {
        var (result, calls) = await Run(() => Ok);
        Assert.Equal(Ok, result.SmokeTest);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SmokeTestReturningNull_LeavesSmokeTestNull()
        => Assert.Null((await Run(() => null)).Result.SmokeTest);

    [Fact]
    public async Task SecondEnsureInitialized_DoesNotRunTheSmokeTestAgain()
    {
        var calls = 0;
        var conn = SqliteConnection.Open(":memory:");
        using (conn)
        {
            MigrationRunner.RunMigrations(conn);
            var lifecycle = new SessionLifecycle(
                new List<IImporter>(), new SqliteStore(conn), new NoCompactionLog(),
                sessionId: "s", smokeTest: () => { calls++; return Ok; });
            await lifecycle.EnsureInitializedAsync();
            await lifecycle.EnsureInitializedAsync();
        }
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ThrowingSmokeTest_LeavesSmokeTestNull_AndSessionStartSucceeds()
    {
        var (result, _) = await Run(() => throw new InvalidOperationException("boom"));
        Assert.Null(result.SmokeTest);
        Assert.False(string.IsNullOrEmpty(result.SessionId));
    }

    [Theory]
    [InlineData(ReindexProgress.Phase.Idle)]
    [InlineData(ReindexProgress.Phase.Running)]
    public async Task ReindexNotFinished_SkipsTheSmokeTest(ReindexProgress.Phase phase)
    {
        var progress = new ReindexProgress();
        if (phase == ReindexProgress.Phase.Running) progress.BeginRunning(10, "m", 1);

        var (result, calls) = await Run(() => Ok, progress, Local());

        Assert.Null(result.SmokeTest);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(ReindexProgress.Phase.Completed)]
    [InlineData(ReindexProgress.Phase.Failed)]
    public async Task ReindexFinished_RunsTheSmokeTest(ReindexProgress.Phase phase)
    {
        var progress = new ReindexProgress();
        progress.BeginRunning(10, "m", 1);
        if (phase == ReindexProgress.Phase.Completed) progress.Complete(); else progress.Fail("x");

        var (result, calls) = await Run(() => Ok, progress, Local());

        Assert.Equal(Ok, result.SmokeTest);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("openai", 384)]
    [InlineData("bedrock", 1024)]
    [InlineData("local", 768)]
    public async Task EmbedderIsNotTheBundledLocalModel_SkipsTheSmokeTest(string provider, int dims)
    {
        var embedder = new DescribedEmbedder(new EmbedderDescriptor(provider, "m", "r", dims));

        var (result, calls) = await Run(() => Ok, embedder: embedder);

        Assert.Null(result.SmokeTest);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task BundledLocalEmbedder_RunsTheSmokeTest()
    {
        var (result, calls) = await Run(() => Ok, embedder: Local());
        Assert.Equal(Ok, result.SmokeTest);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SmokeTestResult_IsSerializedInTheSessionStartPayload()
    {
        var (result, _) = await Run(() => new SmokeTestResult(false, "exact match rate 72.7% is below the 80% threshold"));

        var json = JsonSerializer.Serialize(result, JsonContext.Default.SessionInitResult);
        using var doc = JsonDocument.Parse(json);
        var smoke = doc.RootElement.GetProperty("smokeTest");
        Assert.False(smoke.GetProperty("passed").GetBoolean());
        Assert.Contains("below the 80% threshold", smoke.GetProperty("notes").GetString());
    }
}
