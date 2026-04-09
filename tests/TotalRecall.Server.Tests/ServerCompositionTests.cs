// tests/TotalRecall.Server.Tests/ServerCompositionTests.cs
//
// Plan 6 Task 6.3a — unit tests for ServerComposition.BuildRegistry.
//
// The test uses the Plan 4 fakes under tests/TotalRecall.Server.Tests/
// TestSupport/ so no real Sqlite / ONNX / FAISS is touched. The single
// verification contract is that BuildRegistry returns a ToolRegistry
// populated with the exact set of 32 production handlers, in the order
// the composition root registers them, with the tool names matching the
// wire protocol.
//
// This is the helper-based test the plan text calls out: it's fast,
// trim-safe, and catches any drift between the handler set on disk and
// the production composition root (e.g. a handler added to Handlers/
// without being wired into BuildRegistry).

namespace TotalRecall.Server.Tests;

using System.Collections.Generic;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public sealed class ServerCompositionTests
{
    private static ToolRegistry BuildWithFakes()
    {
        var store = new FakeSqliteStore();
        var vectors = new FakeVectorSearch();
        var embedder = new RecordingFakeEmbedder();
        var hybrid = new RecordingFakeHybridSearch();
        var fileIngester = new RecordingFakeFileIngester();
        var compactionLog = new FakeCompactionLog();
        var sessionLifecycle = new FakeSessionLifecycle();
        var statusOptions = new StatusOptions(
            DbPath: "/tmp/test.db",
            EmbeddingModel: "all-MiniLM-L6-v2",
            EmbeddingDimensions: 384);

        return ServerComposition.BuildRegistry(
            store, vectors, embedder, hybrid,
            fileIngester, compactionLog, sessionLifecycle, statusOptions);
    }

    [Fact]
    public void BuildRegistry_RegistersAllProductionHandlers()
    {
        var registry = BuildWithFakes();

        // Expected handler set — must stay in sync with ServerComposition.
        var expected = new[]
        {
            // Memory (12)
            "memory_store", "memory_search", "memory_get", "memory_update",
            "memory_delete", "memory_promote", "memory_demote", "memory_inspect",
            "memory_history", "memory_lineage", "memory_export", "memory_import",
            // KB (7)
            "kb_search", "kb_ingest_file", "kb_ingest_dir", "kb_list_collections",
            "kb_refresh", "kb_remove", "kb_summarize",
            // Session (3)
            "session_start", "session_end", "session_context",
            // Eval (5)
            "eval_report", "eval_benchmark", "eval_compare", "eval_snapshot",
            "eval_grow",
            // Config (2)
            "config_get", "config_set",
            // Misc (3)
            "status", "import_host", "compact_now",
        };

        Assert.Equal(expected.Length, registry.Count);

        var actual = new List<string>();
        foreach (var spec in registry.ListTools())
        {
            actual.Add(spec.Name);
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildRegistry_EveryHandlerIsLookupable()
    {
        var registry = BuildWithFakes();

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
        var registry = BuildWithFakes();

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
