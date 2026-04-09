// tests/TotalRecall.Server.Tests/EndToEndStdioTests.cs
//
// Plan 4 Task 4.12 — end-to-end stdio driver test for McpServer. Drives the
// server in-process via StringReader / StringWriter (the same pattern used
// by McpServerTests) rather than spawning `total-recall serve` as a
// subprocess: the subprocess path depends on the Plan 6 composition root,
// which has not landed yet. The plan text explicitly allows the in-process
// approach, and it keeps the test isolated from the real embedder / SQLite /
// FAISS stack.
//
// Scenarios covered:
//
//   1. FullHandshake_InitializeToolsListCallShutdown: scripts the full MCP
//      lifecycle (initialize -> tools/list -> tools/call memory_store ->
//      tools/call memory_search -> shutdown) through one RunAsync pass and
//      asserts response shape at every step plus the recorded fake-side
//      effects.
//
//   2. ToolsCall_UnknownTool_ReturnsError: verifies that a tools/call for a
//      name that is not registered produces a JSON-RPC error with the
//      -32603 code McpServer.HandleToolsCallAsync emits via the catch-all
//      InvalidOperationException path.
//
//   3. NotificationsInitialized_FiresOnInitializedCallback: wires a
//      TaskCompletionSource into the onInitialized callback and asserts it
//      fires when the server receives notifications/initialized.
//
// Design notes:
//
//   - We reuse the Plan 4 fakes (FakeSqliteStore, FakeVectorSearch,
//     RecordingFakeEmbedder, RecordingFakeHybridSearch) so no real I/O
//     happens. MemorySearchHandler's NextResult defaults to an empty array.
//
//   - The end-to-end test does NOT touch the production composition root;
//     it builds a ToolRegistry inline with just memory_store + memory_search
//     handlers, matching the plan's "test-only wiring" scope note.
//
//   - Task 4.12 flipped JsonContext's DefaultIgnoreCondition to Never so
//     nullable DTO fields serialize as literal JSON null (matching TS). The
//     JSON-RPC envelope keeps per-field WhenWritingNull overrides so success
//     responses do not emit "error":null and vice versa — this test verifies
//     that envelope discipline holds end-to-end.

namespace TotalRecall.Server.Tests;

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Server.Handlers;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;

public sealed class EndToEndStdioTests
{
    private static string[] Lines(string stdout) =>
        stdout.Replace("\r\n", "\n").TrimEnd('\n').Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static JsonElement Parse(string line) =>
        JsonDocument.Parse(line).RootElement.Clone();

    private static (ToolRegistry registry,
                    FakeSqliteStore store,
                    FakeVectorSearch vectors,
                    RecordingFakeEmbedder embedder,
                    RecordingFakeHybridSearch hybrid)
        BuildTestRegistry()
    {
        var store = new FakeSqliteStore { NextInsertId = "entry-e2e" };
        var vectors = new FakeVectorSearch();
        var embedder = new RecordingFakeEmbedder();
        var hybrid = new RecordingFakeHybridSearch();

        var registry = new ToolRegistry();
        registry.Register(new MemoryStoreHandler(store, embedder, vectors));
        registry.Register(new MemorySearchHandler(embedder, hybrid));

        return (registry, store, vectors, embedder, hybrid);
    }

    [Fact]
    public async Task FullHandshake_InitializeToolsListCallShutdown()
    {
        // Script of newline-delimited JSON-RPC messages matching the plan
        // text. The memory_search default NextResult is an empty array.
        var script =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_store\",\"arguments\":{\"content\":\"hello world\",\"tier\":\"hot\"}}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"memory_search\",\"arguments\":{\"query\":\"hello\",\"topK\":10}}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"shutdown\"}\n";

        var input = new StringReader(script);
        var output = new StringWriter();

        var (registry, store, vectors, embedder, hybrid) = BuildTestRegistry();
        var server = new McpServer(input, output, registry);

        var code = await server.RunAsync();
        Assert.Equal(0, code);

        var lines = Lines(output.ToString());
        Assert.Equal(5, lines.Length);

        // ---- Response 1: initialize ----
        var init = Parse(lines[0]);
        Assert.Equal(1, init.GetProperty("id").GetInt32());
        var initResult = init.GetProperty("result");
        Assert.Equal("2024-11-05", initResult.GetProperty("protocolVersion").GetString());
        Assert.Equal("total-recall", initResult.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(initResult.GetProperty("capabilities").TryGetProperty("tools", out _));
        // Envelope discipline: success response must NOT emit "error":null.
        Assert.False(init.TryGetProperty("error", out _));

        // ---- Response 2: tools/list ----
        var list = Parse(lines[1]);
        Assert.Equal(2, list.GetProperty("id").GetInt32());
        var tools = list.GetProperty("result").GetProperty("tools");
        Assert.Equal(JsonValueKind.Array, tools.ValueKind);
        Assert.Equal(2, tools.GetArrayLength());

        var t0 = tools[0];
        var t1 = tools[1];
        Assert.Equal("memory_store", t0.GetProperty("name").GetString());
        Assert.Equal("memory_search", t1.GetProperty("name").GetString());
        foreach (var t in new[] { t0, t1 })
        {
            Assert.True(t.TryGetProperty("description", out var desc));
            Assert.Equal(JsonValueKind.String, desc.ValueKind);
            Assert.False(string.IsNullOrEmpty(desc.GetString()));
            Assert.True(t.TryGetProperty("inputSchema", out var schema));
            Assert.Equal(JsonValueKind.Object, schema.ValueKind);
            Assert.Equal("object", schema.GetProperty("type").GetString());
        }

        // ---- Response 3: tools/call memory_store ----
        var store3 = Parse(lines[2]);
        Assert.Equal(3, store3.GetProperty("id").GetInt32());
        var storeContent = store3.GetProperty("result").GetProperty("content");
        Assert.Equal(1, storeContent.GetArrayLength());
        var storeItem0 = storeContent[0];
        Assert.Equal("text", storeItem0.GetProperty("type").GetString());
        var storeText = storeItem0.GetProperty("text").GetString()!;
        using (var payloadDoc = JsonDocument.Parse(storeText))
        {
            var payload = payloadDoc.RootElement;
            Assert.True(payload.TryGetProperty("id", out var idProp));
            Assert.Equal("entry-e2e", idProp.GetString());
        }

        // ---- Response 4: tools/call memory_search ----
        var search4 = Parse(lines[3]);
        Assert.Equal(4, search4.GetProperty("id").GetInt32());
        var searchContent = search4.GetProperty("result").GetProperty("content");
        Assert.Equal(1, searchContent.GetArrayLength());
        var searchText = searchContent[0].GetProperty("text").GetString()!;
        using (var searchDoc = JsonDocument.Parse(searchText))
        {
            var arr = searchDoc.RootElement;
            Assert.Equal(JsonValueKind.Array, arr.ValueKind);
            Assert.Equal(0, arr.GetArrayLength());
        }

        // ---- Response 5: shutdown ----
        var shut = Parse(lines[4]);
        Assert.Equal(5, shut.GetProperty("id").GetInt32());
        var shutResult = shut.GetProperty("result");
        Assert.Equal(JsonValueKind.Object, shutResult.ValueKind);
        // {} empty object.
        var enumerator = shutResult.EnumerateObject();
        Assert.False(enumerator.MoveNext());

        // ---- Recorded side effects ----
        // memory_store now goes through InsertWithEmbedding (transactional
        // content + vec insert), so the split Insert/vec.InsertEmbedding
        // path is no longer exercised.
        Assert.Single(store.InsertWithEmbeddingCalls);
        var insert = store.InsertWithEmbeddingCalls[0];
        Assert.True(insert.Tier.IsHot);
        Assert.True(insert.Type.IsMemory);
        Assert.Equal("hello world", insert.Opts.Content);
        Assert.Equal(384, insert.Embedding.Length);

        // Embedder called twice: once for memory_store, once for memory_search.
        Assert.Equal(2, embedder.Calls.Count);
        Assert.Equal("hello world", embedder.Calls[0]);
        Assert.Equal("hello", embedder.Calls[1]);

        Assert.Single(hybrid.Calls);
        Assert.Equal("hello", hybrid.Calls[0].Query);
        Assert.Equal(10, hybrid.Calls[0].Opts.TopK);
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsError()
    {
        var script =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"does_not_exist\",\"arguments\":{}}}\n";

        var input = new StringReader(script);
        var output = new StringWriter();

        var (registry, _, _, _, _) = BuildTestRegistry();
        var server = new McpServer(input, output, registry);

        await server.RunAsync();

        var lines = Lines(output.ToString());
        Assert.Equal(2, lines.Length);

        // First response is initialize OK.
        var init = Parse(lines[0]);
        Assert.Equal(1, init.GetProperty("id").GetInt32());
        Assert.True(init.TryGetProperty("result", out _));

        // Second response is the error for the unknown tool.
        var err = Parse(lines[1]);
        Assert.Equal(2, err.GetProperty("id").GetInt32());
        // McpServer.HandleToolsCallAsync throws InvalidOperationException for
        // unknown tool names, which the outer catch translates to -32603.
        var errorObj = err.GetProperty("error");
        Assert.Equal(-32603, errorObj.GetProperty("code").GetInt32());
        Assert.Contains("does_not_exist", errorObj.GetProperty("message").GetString());
        // Envelope discipline: error response must NOT emit "result":null.
        Assert.False(err.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task NotificationsInitialized_FiresOnInitializedCallback()
    {
        var script =
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"shutdown\"}\n";

        var input = new StringReader(script);
        var output = new StringWriter();

        var (registry, _, _, _, _) = BuildTestRegistry();
        var tcs = new TaskCompletionSource();
        var server = new McpServer(input, output, registry, onInitialized: () =>
        {
            tcs.TrySetResult();
            return Task.CompletedTask;
        });

        var code = await server.RunAsync();
        Assert.Equal(0, code);

        // Short-timeout await: the fire-and-forget callback should have run
        // synchronously (TaskContinuationOptions.ExecuteSynchronously) before
        // the dispatch loop processed the next line.
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(tcs.Task, completed);

        // Notification emits no response; only shutdown does.
        var lines = Lines(output.ToString());
        Assert.Single(lines);
        var shut = Parse(lines[0]);
        Assert.Equal(1, shut.GetProperty("id").GetInt32());
    }
}
