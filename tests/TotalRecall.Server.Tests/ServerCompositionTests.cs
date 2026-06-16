// tests/TotalRecall.Server.Tests/ServerCompositionTests.cs
//
// Plan 6 Task 6.3a — unit tests for ServerComposition.BuildRegistry.
//
// The test uses the Plan 4 fakes under tests/TotalRecall.Server.Tests/
// TestSupport/ so no real Sqlite / ONNX / FAISS is touched. The single
// verification contract is that BuildRegistry returns a ToolRegistry
// populated with the exact set of 42 production handlers, in the order
// the composition root registers them, with the tool names matching the
// wire protocol.
//
// This is the helper-based test the plan text calls out: it's fast,
// trim-safe, and catches any drift between the handler set on disk and
// the production composition root (e.g. a handler added to Handlers/
// without being wired into BuildRegistry).

namespace TotalRecall.Server.Tests;

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

public sealed class ServerCompositionTests
{
    // Production always wires a RetrievalEventLog in both sqlite + cortex
    // modes, so the assistant-only memory_feedback tool is always present.
    // The fake registry mirrors that by passing a real (in-memory) log. The
    // returned connection owns the log's lifetime — callers dispose it.
    private static MsSqliteConnection OpenAndBuild(out ToolRegistry registry)
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var store = new FakeStore();
        var vectors = new FakeVectorSearch();
        var embedder = new RecordingFakeEmbedder();
        var hybrid = new RecordingFakeHybridSearch();
        var fileIngester = new RecordingFakeFileIngester();
        var compactionLog = new FakeCompactionLog();
        var sessionLifecycle = new FakeSessionLifecycle();
        var statusOptions = new StatusOptions(
            DbPath: "/tmp/test.db",
            EmbeddingModel: "bge-small-en-v1.5",
            EmbeddingDimensions: 384);

        registry = ServerComposition.BuildRegistry(
            store, vectors, embedder, hybrid,
            fileIngester, compactionLog, sessionLifecycle, statusOptions,
            retrievalLog: new RetrievalEventLog(conn));
        return conn;
    }

    // Task 1.7 — a registry built with querySource: "web-ui" must tag the
    // retrieval events its memory_search emits as "web-ui" (not the default
    // "assistant"), so eval_report's assistant-scoped stats aren't polluted
    // by browser-driven searches.
    [Fact]
    public async Task BuildRegistry_TagsWebUiSource_OnSearch()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var log = new RetrievalEventLog(conn);

        var store = new FakeStore();
        var vectors = new FakeVectorSearch();
        var embedder = new RecordingFakeEmbedder();
        var hybrid = new RecordingFakeHybridSearch();
        var fileIngester = new RecordingFakeFileIngester();
        var compactionLog = new FakeCompactionLog();
        var sessionLifecycle = new FakeSessionLifecycle();
        var statusOptions = new StatusOptions(
            DbPath: "/tmp/test.db",
            EmbeddingModel: "bge-small-en-v1.5",
            EmbeddingDimensions: 384);

        var registry = ServerComposition.BuildRegistry(
            store, vectors, embedder, hybrid,
            fileIngester, compactionLog, sessionLifecycle, statusOptions,
            retrievalLog: log,
            querySource: "web-ui");

        Assert.True(registry.TryGet("memory_search", out var handler));
        await handler!.ExecuteAsync(
            JsonDocument.Parse("""{"query":"hi"}""").RootElement, CancellationToken.None);

        var events = log.GetEvents(new RetrievalEventQuery());
        Assert.Equal("web-ui", events.Single().QuerySource);
    }

    [Fact]
    public void BuildRegistry_RegistersAllProductionHandlers()
    {
        using var conn = OpenAndBuild(out var registry);

        // Expected handler set — must stay in sync with ServerComposition.
        var expected = new[]
        {
            // Memory (19) — memory_feedback is assistant-only (not allowlisted)
            "memory_store", "memory_search", "memory_feedback",
            "memory_get", "memory_get_all",
            "memory_update", "memory_delete", "memory_promote", "memory_demote",
            "memory_pin", "memory_unpin",
            "memory_inspect", "memory_history", "memory_recent", "memory_list",
            "memory_lineage", "memory_export", "memory_import", "memory_extract",
            // KB (8)
            "kb_search", "kb_ingest_file", "kb_ingest_dir", "kb_list_collections",
            "kb_refresh", "kb_remove", "kb_summarize", "kb_resolve",
            // Session (4)
            "session_start", "session_end", "session_context", "session_refresh",
            // Eval (5)
            "eval_report", "eval_benchmark", "eval_compare", "eval_snapshot",
            "eval_grow",
            // Config (2)
            "config_get", "config_set",
            // Misc (4)
            "status", "import_host", "compact_now", "migrate_to_remote",
            // Insights (1)
            "insights",
        };

        Assert.Equal(expected.Length, registry.Count);

        var actual = new List<string>();
        foreach (var spec in registry.ListTools())
        {
            actual.Add(spec.Name);
        }

        Assert.Equal(expected, actual);
    }

    // When no RetrievalEventLog is supplied (e.g. the postgres/no-telemetry
    // path) memory_feedback must be absent from the registry.
    [Fact]
    public void BuildRegistry_OmitsMemoryFeedback_WhenNoRetrievalLog()
    {
        var store = new FakeStore();
        var vectors = new FakeVectorSearch();
        var embedder = new RecordingFakeEmbedder();
        var hybrid = new RecordingFakeHybridSearch();
        var fileIngester = new RecordingFakeFileIngester();
        var compactionLog = new FakeCompactionLog();
        var sessionLifecycle = new FakeSessionLifecycle();
        var statusOptions = new StatusOptions(
            DbPath: "/tmp/test.db",
            EmbeddingModel: "bge-small-en-v1.5",
            EmbeddingDimensions: 384);

        var registry = ServerComposition.BuildRegistry(
            store, vectors, embedder, hybrid,
            fileIngester, compactionLog, sessionLifecycle, statusOptions,
            retrievalLog: null);

        Assert.False(registry.TryGet("memory_feedback", out _));
    }

    // The assistant-only memory_feedback tool registers in BOTH composition
    // modes whenever a RetrievalEventLog is wired — which production always
    // does for sqlite + cortex.
    [Fact]
    public void BuildRegistry_RegistersMemoryFeedback_WhenRetrievalLogWired()
    {
        using var conn = OpenAndBuild(out var registry);
        Assert.True(registry.TryGet("memory_feedback", out var handler));
        Assert.NotNull(handler);
        Assert.Equal("memory_feedback", handler!.Name);
    }

    [Fact]
    public void BuildRegistry_EveryHandlerIsLookupable()
    {
        using var conn = OpenAndBuild(out var registry);

        foreach (var spec in registry.ListTools())
        {
            Assert.True(registry.TryGet(spec.Name, out var handler));
            Assert.NotNull(handler);
            Assert.Equal(spec.Name, handler!.Name);
        }
    }

    [Fact]
    public void BuildRegistry_ToolSpecsHaveSchemas()
    {
        using var conn = OpenAndBuild(out var registry);

        foreach (var spec in registry.ListTools())
        {
            Assert.False(string.IsNullOrEmpty(spec.Name));
            Assert.False(string.IsNullOrEmpty(spec.Description));
            Assert.Equal(
                System.Text.Json.JsonValueKind.Object,
                spec.InputSchema.ValueKind);
        }
    }
}
