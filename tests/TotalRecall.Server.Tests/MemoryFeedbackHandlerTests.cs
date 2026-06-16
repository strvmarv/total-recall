// Task 1.6 — MemoryFeedbackHandler contract tests.
//
// The assistant-only `memory_feedback` tool records the outcome of a prior
// retrieval (by retrievalId returned from memory_search / kb_search). It is
// idempotent: an unknown id is a no-op that returns {"updated": false}.

namespace TotalRecall.Server.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Server.Handlers;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

public sealed class MemoryFeedbackHandlerTests
{
    // ---------------- helpers ----------------

    private static MsSqliteConnection NewDb()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return conn;
    }

    private static RetrievalEventEntry MakeEntry() =>
        new(
            SessionId: "sess-1",
            QueryText: "what is x?",
            QuerySource: "assistant",
            Results: new[]
            {
                new RetrievalResultItem("e1", "hot", "memory", 0.95, 1),
                new RetrievalResultItem("e2", "warm", "memory", 0.80, 2),
            },
            TiersSearched: new[] { "hot", "warm" },
            ConfigSnapshotId: "cfg-1",
            LatencyMs: 12,
            TotalCandidatesScanned: 100);

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    // ---------------- tests ----------------

    [Fact]
    public async Task Feedback_SetsOutcomeUsed_OnKnownId()
    {
        using var db = NewDb();
        var log = new RetrievalEventLog(db);
        var id = log.LogEvent(MakeEntry());
        var handler = new MemoryFeedbackHandler(log);

        var result = await handler.ExecuteAsync(
            Args($$"""{"retrievalId":"{{id}}","used":true,"signal":"answered"}"""), default);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.True(doc.RootElement.GetProperty("updated").GetBoolean());

        var row = log.GetEvents(new RetrievalEventQuery()).Single();
        Assert.Equal(true, row.OutcomeUsed);
        Assert.Equal("answered", row.OutcomeSignal);
    }

    [Fact]
    public async Task Feedback_UnknownId_IsNoOp_ReturnsUpdatedFalse()
    {
        using var db = NewDb();
        var handler = new MemoryFeedbackHandler(new RetrievalEventLog(db));

        var result = await handler.ExecuteAsync(
            Args("""{"retrievalId":"does-not-exist"}"""), default);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.False(doc.RootElement.GetProperty("updated").GetBoolean());
    }

    [Fact]
    public async Task Feedback_MissingRetrievalId_Throws()
    {
        using var db = NewDb();
        var handler = new MemoryFeedbackHandler(new RetrievalEventLog(db));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{}"""), default));
    }

    [Fact]
    public async Task Feedback_EmptyRetrievalId_Throws()
    {
        using var db = NewDb();
        var handler = new MemoryFeedbackHandler(new RetrievalEventLog(db));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(Args("""{"retrievalId":""}"""), default));
    }

    [Fact]
    public async Task Feedback_NullArguments_Throws()
    {
        using var db = NewDb();
        var handler = new MemoryFeedbackHandler(new RetrievalEventLog(db));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.ExecuteAsync(null, default));
    }

    [Fact]
    public async Task Feedback_UsedDefaultsToTrue_WhenOmitted()
    {
        using var db = NewDb();
        var log = new RetrievalEventLog(db);
        var id = log.LogEvent(MakeEntry());
        var handler = new MemoryFeedbackHandler(log);

        await handler.ExecuteAsync(Args($$"""{"retrievalId":"{{id}}"}"""), default);

        Assert.Equal(true, log.GetEvents(new RetrievalEventQuery()).Single().OutcomeUsed);
    }

    [Fact]
    public async Task Feedback_UsedFalse_SetsOutcomeUsedFalse()
    {
        using var db = NewDb();
        var log = new RetrievalEventLog(db);
        var id = log.LogEvent(MakeEntry());
        var handler = new MemoryFeedbackHandler(log);

        await handler.ExecuteAsync(
            Args($$"""{"retrievalId":"{{id}}","used":false}"""), default);

        Assert.Equal(false, log.GetEvents(new RetrievalEventQuery()).Single().OutcomeUsed);
    }

    [Fact]
    public void Metadata_NameAndSchema()
    {
        using var db = NewDb();
        var handler = new MemoryFeedbackHandler(new RetrievalEventLog(db));

        Assert.Equal("memory_feedback", handler.Name);
        Assert.False(string.IsNullOrEmpty(handler.Description));
        Assert.Equal(JsonValueKind.Object, handler.InputSchema.ValueKind);
    }
}
