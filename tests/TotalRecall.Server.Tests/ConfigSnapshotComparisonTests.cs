// End to end: events logged by two sessions with different configs separate by
// snapshot id when read the way eval_compare reads them, named snapshots still
// resolve to the session rows, and events stored before this change (with the
// "default" / "" placeholders) stay readable and are never rewritten.

namespace TotalRecall.Server.Tests;

using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public sealed class ConfigSnapshotComparisonTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static MemorySearchHandler SearchHandler(RetrievalEventLog log, SessionConfigSnapshot holder) =>
        new(new RecordingFakeEmbedder(), new RecordingFakeHybridSearch(),
            retrievalLog: log, configSnapshot: holder);

    [Fact]
    public async Task TwoSessionsWithDifferentConfigs_EachSideHoldsOnlyItsOwnEvents()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var snapshots = new ConfigSnapshotStore(conn);
        var log = new RetrievalEventLog(conn);

        var before = new SessionConfigSnapshot(snapshots, "{\"threshold\":0.5}");
        var after = new SessionConfigSnapshot(snapshots, "{\"threshold\":0.7}");

        await SearchHandler(log, before).ExecuteAsync(Args("""{"query":"q1"}"""), CancellationToken.None);
        await SearchHandler(log, before).ExecuteAsync(Args("""{"query":"q2"}"""), CancellationToken.None);
        await SearchHandler(log, after).ExecuteAsync(Args("""{"query":"q3"}"""), CancellationToken.None);

        // Read the way eval_compare's provider does: resolve the ref, filter by id.
        var beforeId = before.Resolve()!;
        var afterId = snapshots.ResolveRef("latest")!;
        Assert.Equal(after.Resolve(), afterId);
        Assert.NotEqual(beforeId, afterId);

        var beforeEvents = log.GetEvents(new RetrievalEventQuery(ConfigSnapshotId: beforeId));
        var afterEvents = log.GetEvents(new RetrievalEventQuery(ConfigSnapshotId: afterId));

        Assert.Equal(new[] { "q1", "q2" }, beforeEvents.Select(e => e.QueryText).OrderBy(q => q));
        Assert.Equal(new[] { "q3" }, afterEvents.Select(e => e.QueryText));
    }

    [Fact]
    public async Task NamedSnapshotRequestedAfterTheSessionRow_ResolvesToTheSessionsEvents()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var snapshots = new ConfigSnapshotStore(conn);
        var log = new RetrievalEventLog(conn);
        var session = new SessionConfigSnapshot(snapshots, "{\"threshold\":0.5}");
        await SearchHandler(log, session).ExecuteAsync(Args("""{"query":"q1"}"""), CancellationToken.None);

        // eval_snapshot "baseline" with an unchanged config reuses the session's row.
        var namedId = snapshots.CreateSnapshot("{\"threshold\":0.5}", "baseline");

        Assert.Equal(session.Resolve(), namedId);
        var resolved = snapshots.ResolveRef("baseline");
        Assert.Equal(namedId, resolved);
        Assert.Single(log.GetEvents(new RetrievalEventQuery(ConfigSnapshotId: resolved)));
    }

    [Fact]
    public async Task EventsStoredBeforeTheChange_StayReadableAndUnrewritten()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var snapshots = new ConfigSnapshotStore(conn);
        var log = new RetrievalEventLog(conn);
        var compactionLog = new CompactionLog(conn);

        log.LogEvent(new RetrievalEventEntry(
            SessionId: "unknown", QueryText: "legacy", QuerySource: "assistant",
            Results: System.Array.Empty<RetrievalResultItem>(), TiersSearched: new[] { "hot_memory" },
            ConfigSnapshotId: "default", LatencyMs: 1));
        compactionLog.LogEvent(new CompactionLogEntry(
            SessionId: "s0", SourceTier: "hot", TargetTier: "warm",
            SourceEntryIds: new[] { "e1" }, TargetEntryId: null,
            DecayScores: new System.Collections.Generic.Dictionary<string, double>(),
            Reason: "session_end_decay", ConfigSnapshotId: ""));

        var session = new SessionConfigSnapshot(snapshots, "{\"a\":1}");
        await SearchHandler(log, session).ExecuteAsync(Args("""{"query":"new"}"""), CancellationToken.None);

        var legacy = log.GetEvents(new RetrievalEventQuery(ConfigSnapshotId: "default")).Single();
        Assert.Equal("legacy", legacy.QueryText);
        Assert.Equal(new[] { "" }, ConfigSnapshotTestSupport.CompactionSnapshotIds(conn));
        Assert.Equal(2, log.GetEvents(new RetrievalEventQuery()).Count);
        Assert.DoesNotContain(
            log.GetEvents(new RetrievalEventQuery(ConfigSnapshotId: session.Resolve())),
            e => e.QueryText == "legacy");
    }
}
