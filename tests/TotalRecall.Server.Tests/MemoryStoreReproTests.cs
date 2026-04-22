// Repro test for the session-end memory_store failure observed 2026-04-19:
//   MCP error -32603: Internal error: KeyNotFoundException:
//   An index satisfying the predicate was not found in the collection.
//
// Reproduces the exact shape of the failing call (tier=warm, entryType=decision,
// tags array, scope/source non-null) against a real SqliteStore wrapped in a
// RoutingStore (the cortex-mode production topology). If the failure is in the
// post-insert EnqueueUpsert path (_local.Get -> SyncPayload.Upsert ->
// SyncQueue.Enqueue), this test will trip it in-process and print a clean
// stack trace.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Sync;
using TotalRecall.Server.Handlers;
using Xunit;
using Xunit.Abstractions;

namespace TotalRecall.Server.Tests;

public sealed class MemoryStoreReproTests
{
    private readonly ITestOutputHelper _output;

    public MemoryStoreReproTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task MemoryStore_AgainstRoutingStore_ReplicatesFailingCall()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var sqliteStore = new SqliteStore(conn);
        var vec = new VectorSearch(conn);
        var syncQueue = new SyncQueue(conn);
        var remote = new NoopRemoteBackend();
        var routing = new RoutingStore(sqliteStore, remote, syncQueue);
        var embedder = new InlineFakeEmbedder();

        var handler = new MemoryStoreHandler(routing, embedder, vec);

        // Exact shape of the failing call from 2026-04-19 session_end.
        var argsJson = """
            {
              "content": "Plan 2 skills plugin MCP + session_start integration shipped 2026-04-19. Both repos on main. 5 skill_* MCP handlers (cortex mode only), session_start folds skill counts into importSummary with 5s soft timeout.",
              "tier": "warm",
              "entryType": "decision",
              "tags": ["plan2","skills","session-index","2026-04-19","consolidated"],
              "source": "session:plan2:2026-04-19"
            }
            """;
        var args = JsonDocument.Parse(argsJson).RootElement.Clone();

        try
        {
            var result = await handler.ExecuteAsync(args, CancellationToken.None);
            _output.WriteLine("Succeeded. Result: " + (result.Content.Length > 0 ? result.Content[0].Text : "<empty>"));
        }
        catch (Exception ex)
        {
            _output.WriteLine($"THREW: {ex.GetType().FullName}: {ex.Message}");
            _output.WriteLine("Stack:");
            _output.WriteLine(ex.ToString());
            throw;
        }
    }

    // --- inline fakes ------------------------------------------------

    private sealed class InlineFakeEmbedder : IEmbedder
    {
        public float[] Embed(string text)
        {
            var v = new float[384];
            var len = text?.Length ?? 0;
            for (var i = 0; i < 384; i++)
            {
                v[i] = (float)Math.Sin(len * (i + 1) / 384.0);
            }
            return v;
        }
    }

    private sealed class NoopRemoteBackend : IRemoteBackend
    {
        public Task<SyncSearchResult[]> SearchKnowledgeAsync(string query, int topK, IReadOnlyList<string>? scopes, CancellationToken ct)
            => Task.FromResult(Array.Empty<SyncSearchResult>());
        public Task<SyncSearchResult[]> SearchMemoriesAsync(string query, string scope, int topK, CancellationToken ct)
            => Task.FromResult(Array.Empty<SyncSearchResult>());
        public Task<SyncStatusResult> GetStatusAsync(CancellationToken ct)
            => Task.FromResult(new SyncStatusResult(0, 0, 0, 0));
        public Task UpsertMemoriesAsync(SyncEntry[] entries, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteMemoryAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<SyncPullResult> GetUserMemoriesModifiedSinceAsync(DateTimeOffset since, CancellationToken ct)
            => Task.FromResult(new SyncPullResult(Array.Empty<SyncEntry>(), null));
        public Task PushUsageEventsAsync(SyncUsageEvent[] events, CancellationToken ct) => Task.CompletedTask;
        public Task PushRetrievalEventsAsync(SyncRetrievalEvent[] events, CancellationToken ct) => Task.CompletedTask;
        public Task PushCompactionEntriesAsync(SyncCompactionEntry[] entries, CancellationToken ct) => Task.CompletedTask;
        public Task<PluginSyncSkillDto[]> GetSkillsModifiedSinceAsync(DateTime? since, CancellationToken ct)
            => Task.FromResult(Array.Empty<PluginSyncSkillDto>());
    }
}
