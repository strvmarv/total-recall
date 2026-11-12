// session_start pins the session's config snapshot: a snapshot for the
// current config exists afterwards, and a second session with the same config
// reuses it instead of adding a row.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;

public sealed class SessionLifecycleConfigSnapshotTests
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

    private static SessionLifecycle Build(SqliteStore store, SessionConfigSnapshot? snapshot) =>
        new(
            importers: new List<IImporter>(),
            store: store,
            compactionLog: new NoCompactionLog(),
            sessionId: "s-" + Guid.NewGuid().ToString("N"),
            nowMs: () => 1_000_000_000_000L,
            configSnapshot: snapshot);

    [Fact]
    public async Task SessionStart_CreatesTheSnapshot_AndAnotherSessionWithTheSameConfigReusesIt()
    {
        var conn = SqliteConnection.Open(":memory:");
        using (conn)
        {
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);
            var snapshots = new ConfigSnapshotStore(conn);
            Assert.Empty(snapshots.ListRecent(10));

            var first = new SessionConfigSnapshot(snapshots, "{\"a\":1}");
            await Build(store, first).EnsureInitializedAsync();

            var rows = snapshots.ListRecent(10);
            Assert.Single(rows);
            Assert.Equal("{\"a\":1}", rows[0].ConfigJson);
            Assert.Equal(rows[0].Id, first.Resolve());

            var second = new SessionConfigSnapshot(snapshots, "{\"a\":1}");
            await Build(store, second).EnsureInitializedAsync();

            Assert.Single(snapshots.ListRecent(10));
            Assert.Equal(rows[0].Id, second.Resolve());
        }
    }

    [Fact]
    public async Task SessionStart_WithoutAHolder_StillSucceeds()
    {
        var conn = SqliteConnection.Open(":memory:");
        using (conn)
        {
            MigrationRunner.RunMigrations(conn);
            var result = await Build(new SqliteStore(conn), null).EnsureInitializedAsync();
            Assert.NotNull(result);
        }
    }
}
