using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Sync;

public class SyncServiceSkillUsageFlushTests
{
    [Fact]
    public async Task FlushAsync_PushesUnsyncedSkillUsageEvents_AndMarksThemSynced()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var cache = new SqliteSkillCache(conn);

        // Seed a skill so RecordInvocationAsync's foreign-key column has a target.
        var imported = new ImportedSkill(
            Name: "s", Description: null, Content: "b",
            FrontmatterJson: "{}", Files: Array.Empty<ImportedSkillFile>(),
            SourcePath: "/v/s.md", SuggestedScope: "user", SuggestedScopeId: "u1",
            SuggestedTags: Array.Empty<string>());
        await cache.UpsertScannedAsync(imported, "h", null, null, CancellationToken.None);
        var sk = await cache.GetByNaturalKeyAsync("s", "user", "u1", CancellationToken.None);

        await cache.RecordInvocationAsync(sk!.Id, "claude-code", "sess1", DateTime.UtcNow, CancellationToken.None);
        await cache.RecordInvocationAsync(sk.Id, "claude-code", "sess1", DateTime.UtcNow, CancellationToken.None);

        var spy = new SpyRemote();
        var queue = new SyncQueue(conn);
        var store = new SqliteStore(conn);
        var svc = new SyncService(store, spy, queue, conn, cache);

        await svc.FlushAsync(CancellationToken.None);

        Assert.Equal(2, spy.PushedSkillUsage.Count);

        using var q = conn.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM skill_usage_events WHERE synced_at IS NULL";
        Assert.Equal(0L, (long)q.ExecuteScalar()!);
    }

    private sealed class SpyRemote : IRemoteBackend
    {
        public List<PluginSyncSkillUsageEvent> PushedSkillUsage { get; } = new();
        public Task PushSkillUsageAsync(PluginSyncSkillUsageEvent[] events, CancellationToken ct)
        {
            PushedSkillUsage.AddRange(events);
            return Task.CompletedTask;
        }

        // Stub the rest with no-ops / throws — only the flush path under test is exercised.
        public Task<SyncSearchResult[]> SearchKnowledgeAsync(string query, int topK, IReadOnlyList<string>? scopes, CancellationToken ct) => Task.FromResult(Array.Empty<SyncSearchResult>());
        public Task<SyncSearchResult[]> SearchMemoriesAsync(string query, string scope, int topK, CancellationToken ct) => Task.FromResult(Array.Empty<SyncSearchResult>());
        public Task<SyncStatusResult> GetStatusAsync(CancellationToken ct) => Task.FromResult(new SyncStatusResult(0, 0, 0, 0));
        public Task UpsertMemoriesAsync(SyncEntry[] entries, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteMemoryAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<SyncPullResult> GetUserMemoriesModifiedSinceAsync(DateTimeOffset since, CancellationToken ct) => Task.FromResult(new SyncPullResult(Array.Empty<SyncEntry>(), null));
        public Task PushUsageEventsAsync(SyncUsageEvent[] events, CancellationToken ct) => Task.CompletedTask;
        public Task PushRetrievalEventsAsync(SyncRetrievalEvent[] events, CancellationToken ct) => Task.CompletedTask;
        public Task PushCompactionEntriesAsync(SyncCompactionEntry[] entries, CancellationToken ct) => Task.CompletedTask;
        public Task<PluginSyncSkillDto[]> GetSkillsModifiedSinceAsync(DateTime? since, CancellationToken ct) => Task.FromResult(Array.Empty<PluginSyncSkillDto>());
    }
}
