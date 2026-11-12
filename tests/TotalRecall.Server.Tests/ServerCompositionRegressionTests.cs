// The composition root builds the regression check from the shared connection,
// the session's snapshot holder and the config. End to end: a config change
// between two sessions, events under each snapshot, and session_start reports
// the alert; with the shipped defaults (min_events 10) nine events are too few.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;

public sealed class ServerCompositionRegressionTests
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

    private static void LogEvents(RetrievalEventLog log, string snapshotId, int total, int misses)
    {
        for (var i = 0; i < total; i++)
        {
            log.LogEvent(new RetrievalEventEntry(
                SessionId: "s", QueryText: "q" + i, QuerySource: "assistant",
                Results: new[] { new RetrievalResultItem("e" + i, "warm", "memory", i < misses ? 0.1 : 0.9, 0) },
                TiersSearched: new[] { "warm" }, ConfigSnapshotId: snapshotId, LatencyMs: 50));
        }
    }

    private static async Task<SessionInitResult> StartSession(
        Microsoft.Data.Sqlite.SqliteConnection conn, RetrievalEventLog log, SessionConfigSnapshot snapshot)
    {
        var check = ServerComposition.BuildRegressionCheck(
            conn, log, snapshot, new ConfigLoader().LoadDefaults(),
            nowMs: () => DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeMilliseconds());
        var lifecycle = new SessionLifecycle(
            importers: new List<IImporter>(), store: new SqliteStore(conn), compactionLog: new NoCompactionLog(),
            sessionId: "s-" + Guid.NewGuid().ToString("N"), configSnapshot: snapshot, regressionCheck: check);
        return await lifecycle.EnsureInitializedAsync();
    }

    private static async Task<SessionInitResult> TwoSessionsAcrossAConfigChange(int eventsPerSnapshot, int missesBefore, int missesAfter)
    {
        var conn = SqliteConnection.Open(":memory:");
        using (conn)
        {
            MigrationRunner.RunMigrations(conn);
            var snapshots = new ConfigSnapshotStore(conn);
            var log = new RetrievalEventLog(conn);

            // Session 1 runs with config v1 and records retrievals under its snapshot.
            var first = new SessionConfigSnapshot(snapshots, "{\"v\":1}");
            await StartSession(conn, log, first);
            LogEvents(log, first.Resolve()!, eventsPerSnapshot, missesBefore);

            // Session 2 runs with the changed config v2.
            Thread.Sleep(5);
            var second = new SessionConfigSnapshot(snapshots, "{\"v\":2}");
            second.Resolve();
            LogEvents(log, second.Resolve()!, eventsPerSnapshot, missesAfter);

            // A later session with the same config v2 reuses the snapshot and sees both sides.
            var third = new SessionConfigSnapshot(snapshots, "{\"v\":2}");
            return await StartSession(conn, log, third);
        }
    }

    [Fact]
    public async Task ARegressionAcrossAConfigChange_IsReportedAtSessionStart()
    {
        var result = await TwoSessionsAcrossAConfigChange(eventsPerSnapshot: 10, missesBefore: 2, missesAfter: 6);

        var alert = Assert.Single(result.RegressionAlerts!);
        Assert.Equal("miss_rate", alert.Kind);
        Assert.Equal(0.2, alert.Previous!.Value, 6);
        Assert.Equal(0.6, alert.Current!.Value, 6);
    }

    [Fact]
    public async Task WithoutARegression_SessionStartReportsNothing()
        => Assert.Null((await TwoSessionsAcrossAConfigChange(10, 2, 2)).RegressionAlerts);

    [Fact]
    public async Task BelowTheShippedMinEvents_SessionStartReportsNothing()
        => Assert.Null((await TwoSessionsAcrossAConfigChange(9, 0, 9)).RegressionAlerts);
}
