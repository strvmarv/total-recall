// session_start reports retrieval regressions found by the injected check in
// `regressionAlerts`: kind, a message naming the metric and the change, and the
// numbers. No check, a null or empty result, and a throwing check all leave it
// null and never fail session_start.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Importers;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public sealed class SessionLifecycleRegressionTests
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

    private static async Task<SessionInitResult> Run(Func<IReadOnlyList<RegressionFinding>?>? check)
    {
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
                regressionCheck: check);
            return await lifecycle.EnsureInitializedAsync();
        }
    }

    [Fact]
    public async Task NoCheck_LeavesAlertsNull()
        => Assert.Null((await Run(null)).RegressionAlerts);

    [Fact]
    public async Task CheckReturningNull_LeavesAlertsNull()
        => Assert.Null((await Run(() => null)).RegressionAlerts);

    [Fact]
    public async Task CheckReturningAnEmptyList_LeavesAlertsNull()
        => Assert.Null((await Run(() => Array.Empty<RegressionFinding>())).RegressionAlerts);

    [Fact]
    public async Task ThrowingCheck_LeavesAlertsNull_AndSessionStartSucceeds()
    {
        var result = await Run(() => throw new InvalidOperationException("boom"));
        Assert.Null(result.RegressionAlerts);
        Assert.False(string.IsNullOrEmpty(result.SessionId));
    }

    [Fact]
    public async Task MissRateFinding_BecomesAnAlertNamingTheMetricAndTheChange()
    {
        var result = await Run(() => new[]
        {
            new RegressionFinding(RegressionFinding.MissRate, Previous: 0.2, Current: 0.45, Delta: 0.25, Threshold: 0.1),
        });

        var alert = Assert.Single(result.RegressionAlerts!);
        Assert.Equal("miss_rate", alert.Kind);
        Assert.Equal("Miss rate rose from 20% to 45% (+25 points; threshold 10 points)", alert.Message);
        Assert.Equal(0.2, alert.Previous);
        Assert.Equal(0.45, alert.Current);
        Assert.Equal(0.25, alert.Delta);
        Assert.Equal(0.1, alert.Threshold);
    }

    [Fact]
    public async Task LatencyFinding_BecomesAnAlertNamingTheMetricAndTheChange()
    {
        var result = await Run(() => new[]
        {
            new RegressionFinding(RegressionFinding.Latency, Previous: 40, Current: 120, Delta: 3.0, Threshold: 2.0),
        });

        var alert = Assert.Single(result.RegressionAlerts!);
        Assert.Equal("latency", alert.Kind);
        Assert.Equal("Average retrieval latency rose from 40 ms to 120 ms (3.0x; threshold 2.0x)", alert.Message);
        Assert.Equal(40, alert.Previous);
        Assert.Equal(120, alert.Current);
        Assert.Equal(3.0, alert.Delta);
        Assert.Equal(2.0, alert.Threshold);
    }

    [Fact]
    public async Task Messages_AreCultureInvariant()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var result = await Run(() => new[]
            {
                new RegressionFinding(RegressionFinding.Latency, 40.5, 121.2, 2.9925, 2.0),
            });
            Assert.DoesNotContain(",", result.RegressionAlerts![0].Message);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public async Task Alerts_AreSerializedIntoTheSessionStartResponse()
    {
        var result = new SessionInitResult(
            SessionId: "s", Project: null, ImportSummary: new List<ImportSummaryRow>(),
            WarmSweep: null, WarmPromoted: 0, ProjectDocs: null, HotEntryCount: 0, Context: "",
            TierSummary: new TierSummary(0, 0, 0, 0, 0, 0), Hints: new List<Hint>(),
            LastSessionAge: null, SmokeTest: null,
            RegressionAlerts: new List<RegressionAlert>
            {
                new("miss_rate", "Miss rate rose", Previous: 0.2, Current: 0.45, Delta: 0.25, Threshold: 0.1),
            },
            Storage: "sqlite", HotContextTruncated: false, SessionAgeHint: null);
        var handler = new SessionStartHandler(new FakeSessionLifecycle { ResultToReturn = result });

        var response = await handler.ExecuteAsync(null, CancellationToken.None);

        using var doc = JsonDocument.Parse(response.Content[0].Text);
        var alert = doc.RootElement.GetProperty("regressionAlerts")[0];
        Assert.Equal("miss_rate", alert.GetProperty("kind").GetString());
        Assert.Equal("Miss rate rose", alert.GetProperty("message").GetString());
        Assert.Equal(0.2, alert.GetProperty("previous").GetDouble());
        Assert.Equal(0.45, alert.GetProperty("current").GetDouble());
        Assert.Equal(0.25, alert.GetProperty("delta").GetDouble());
        Assert.Equal(0.1, alert.GetProperty("threshold").GetDouble());
    }
}
