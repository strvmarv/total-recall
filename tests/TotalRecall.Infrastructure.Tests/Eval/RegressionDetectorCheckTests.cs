using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Eval;

public sealed class RegressionDetectorCheckTests
{
    private sealed class Fixture : IDisposable
    {
        public MsSqliteConnection Conn { get; } = SqliteConnection.Open(":memory:");
        public ConfigSnapshotStore Snapshots { get; }
        public RetrievalEventLog Log { get; }

        public Fixture()
        {
            MigrationRunner.RunMigrations(Conn);
            Snapshots = new ConfigSnapshotStore(Conn);
            Log = new RetrievalEventLog(Conn);
        }

        /// <summary>Snapshots are ordered by a millisecond timestamp; keep rows apart.</summary>
        public string Snapshot(string json, string? name = null)
        {
            Thread.Sleep(5);
            return Snapshots.CreateSnapshot(json, name);
        }

        public void LogEvents(string snapshotId, int total, int misses, long? latencyMs = 50, string source = "assistant")
        {
            for (var i = 0; i < total; i++)
            {
                var score = i < misses ? 0.1 : 0.9;
                Log.LogEvent(new RetrievalEventEntry(
                    SessionId: "s", QueryText: "q" + i, QuerySource: source,
                    Results: new[] { new RetrievalResultItem("e" + i, "warm", "memory", score, 0) },
                    TiersSearched: new[] { "warm" }, ConfigSnapshotId: snapshotId, LatencyMs: latencyMs));
            }
        }

        public RegressionDetector Detector(SessionConfigSnapshot session, IConfigSnapshotStore? store = null,
            Action<string>? onError = null)
            => new(store ?? Snapshots, Log, session, RegressionOptions.Default, 0.65,
                // Three hours on: every event is past the 60-minute grace window, so none is pending.
                nowMs: () => DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeMilliseconds(),
                onError: onError);

        public void Dispose() => Conn.Dispose();
    }

    private sealed class ThrowingListStore : IConfigSnapshotStore
    {
        public string CreateSnapshot(string configJson, string? name = null) => "session-snapshot";
        public ConfigSnapshotRow? GetLatest() => throw new NotSupportedException();
        public ConfigSnapshotRow? GetById(string id) => throw new NotSupportedException();
        public ConfigSnapshotRow? GetByName(string name) => throw new NotSupportedException();
        public IReadOnlyList<ConfigSnapshotRow> ListRecent(int limit) => throw new InvalidOperationException("disk gone");
        public string? ResolveRef(string nameOrId) => throw new NotSupportedException();
    }

    private sealed class ThrowingCreateStore : IConfigSnapshotStore
    {
        public string CreateSnapshot(string configJson, string? name = null) => throw new InvalidOperationException("no db");
        public ConfigSnapshotRow? GetLatest() => null;
        public ConfigSnapshotRow? GetById(string id) => null;
        public ConfigSnapshotRow? GetByName(string name) => null;
        public IReadOnlyList<ConfigSnapshotRow> ListRecent(int limit) => Array.Empty<ConfigSnapshotRow>();
        public string? ResolveRef(string nameOrId) => null;
    }

    [Fact]
    public void TwoSnapshotsWithEnoughEvents_ReportsTheRegression()
    {
        using var fx = new Fixture();
        var older = fx.Snapshot("{\"v\":1}");
        fx.LogEvents(older, total: 10, misses: 2);
        var session = new SessionConfigSnapshot(fx.Snapshots, "{\"v\":2}");
        fx.Snapshot("{\"v\":2}"); // the session's row (Resolve() below reuses it)
        fx.LogEvents(session.Resolve()!, total: 10, misses: 5);

        var found = fx.Detector(session).Check();

        var f = Assert.Single(found!);
        Assert.Equal(RegressionFinding.MissRate, f.Kind);
        Assert.Equal(0.2, f.Previous, 6);
        Assert.Equal(0.5, f.Current, 6);
    }

    [Fact]
    public void AnEventlessSnapshotBetween_IsSkipped_AndTheNearestOneWithEventsIsUsed()
    {
        using var fx = new Fixture();
        var older = fx.Snapshot("{\"v\":1}");
        fx.LogEvents(older, 10, 1);
        fx.Snapshot("{\"v\":1.5}", name: "pre-change:x"); // config set left a row with no events
        var session = new SessionConfigSnapshot(fx.Snapshots, "{\"v\":2}");
        fx.Snapshot("{\"v\":2}");
        fx.LogEvents(session.Resolve()!, 10, 6);

        var found = fx.Detector(session).Check();

        var f = Assert.Single(found!);
        Assert.Equal(0.1, f.Previous, 6);
        Assert.Equal(0.6, f.Current, 6);
    }

    [Fact]
    public void SingleSnapshot_IsNull()
    {
        using var fx = new Fixture();
        var session = new SessionConfigSnapshot(fx.Snapshots, "{\"v\":1}");
        fx.LogEvents(session.Resolve()!, 20, 20);
        Assert.Null(fx.Detector(session).Check());
    }

    [Fact]
    public void PreviousSnapshotWithoutEvents_IsNull()
    {
        using var fx = new Fixture();
        fx.Snapshot("{\"v\":1}");
        var session = new SessionConfigSnapshot(fx.Snapshots, "{\"v\":2}");
        fx.Snapshot("{\"v\":2}");
        fx.LogEvents(session.Resolve()!, 20, 20);
        Assert.Null(fx.Detector(session).Check());
    }

    [Fact]
    public void OnlyAssistantSourceEvents_AreCompared()
    {
        using var fx = new Fixture();
        var older = fx.Snapshot("{\"v\":1}");
        fx.LogEvents(older, 10, 0);
        var session = new SessionConfigSnapshot(fx.Snapshots, "{\"v\":2}");
        fx.Snapshot("{\"v\":2}");
        var cur = session.Resolve()!;
        fx.LogEvents(cur, 10, 0);                          // assistant: no regression
        fx.LogEvents(cur, 30, 30, source: "benchmark");    // other source: all misses, must be ignored
        Assert.Null(fx.Detector(session).Check());
    }

    [Fact]
    public void SessionSnapshotOutsideTheRecentWindow_IsNull()
    {
        using var fx = new Fixture();
        var session = new SessionConfigSnapshot(fx.Snapshots, "{\"v\":0}");
        var id = session.Resolve()!;
        fx.LogEvents(id, 10, 10);
        for (var i = 1; i <= 25; i++) fx.Snapshot("{\"v\":" + i + "}"); // 25 newer rows push it out of ListRecent(20)
        Assert.Null(fx.Detector(session).Check());
    }

    [Fact]
    public void UnresolvedSessionSnapshot_IsNull()
    {
        using var fx = new Fixture();
        var session = new SessionConfigSnapshot(new ThrowingCreateStore(), "{}", onError: _ => { });
        Assert.Null(fx.Detector(session, store: new ThrowingCreateStore()).Check());
    }

    [Fact]
    public void AStoreFailure_ReturnsNull_AndWritesOnePrefixedLine()
    {
        using var fx = new Fixture();
        var lines = new List<string>();
        var session = new SessionConfigSnapshot(new ThrowingListStore(), "{}");
        var detector = fx.Detector(session, store: new ThrowingListStore(), onError: lines.Add);

        Assert.Null(detector.Check());

        var line = Assert.Single(lines);
        Assert.StartsWith("[total-recall]", line);
        Assert.Contains("disk gone", line);
    }
}
