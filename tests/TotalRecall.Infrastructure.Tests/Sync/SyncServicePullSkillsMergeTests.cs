using System;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Sync;

public class SyncServicePullSkillsMergeTests
{
    [Fact]
    public async Task UpsertAsync_MaxWinsForUsageCount_NeverDecreasesLocal()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var cache = new SqliteSkillCache(conn);

        var imported = new ImportedSkill(
            Name: "s", Description: null, Content: "body",
            FrontmatterJson: "{}", Files: Array.Empty<ImportedSkillFile>(),
            SourcePath: "/v/s.md", SuggestedScope: "user", SuggestedScopeId: "u1",
            SuggestedTags: Array.Empty<string>());
        await cache.UpsertScannedAsync(imported, "h", null, null, CancellationToken.None);
        var sk = await cache.GetByNaturalKeyAsync("s", "user", "u1", CancellationToken.None);
        for (int i = 0; i < 5; i++)
            await cache.RecordInvocationAsync(sk!.Id, null, null, DateTime.UtcNow, CancellationToken.None);

        var remoteId = Guid.NewGuid(); // cortex assigns its own id
        var remote = new PluginSyncSkillDto(
            Id: remoteId,
            Name: "s",
            Description: null,
            Content: "body",
            FrontmatterJson: "{}",
            ContentHash: "h",
            Scope: "user",
            ScopeId: "u1",
            Tags: Array.Empty<string>(),
            Source: null,
            IsOrphaned: false,
            Version: 2,
            UsageCount: 3, // lower than local 5
            LastUsedAt: DateTime.UtcNow.AddHours(-1),
            CreatedAt: DateTime.UtcNow.AddDays(-1),
            UpdatedAt: DateTime.UtcNow.AddMinutes(-5));

        await cache.UpsertAsync(remote, CancellationToken.None);

        var after = await cache.GetByNaturalKeyAsync("s", "user", "u1", CancellationToken.None);
        Assert.NotNull(after);
        // max-wins: local 5 vs cortex 3 → 5
        Assert.Equal(5, after!.UsageCount);
    }

    [Fact]
    public async Task UpsertAsync_AdoptsCortexId_AndRewritesUsageEvents()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var cache = new SqliteSkillCache(conn);

        var imported = new ImportedSkill(
            Name: "s", Description: null, Content: "body",
            FrontmatterJson: "{}", Files: Array.Empty<ImportedSkillFile>(),
            SourcePath: "/v/s.md", SuggestedScope: "user", SuggestedScopeId: "u1",
            SuggestedTags: Array.Empty<string>());
        await cache.UpsertScannedAsync(imported, "h", null, null, CancellationToken.None);
        var sk = await cache.GetByNaturalKeyAsync("s", "user", "u1", CancellationToken.None);
        var localId = sk!.Id;

        // Record an event before pull — should be re-keyed to cortex id.
        await cache.RecordInvocationAsync(localId, null, "sess1", DateTime.UtcNow, CancellationToken.None);

        var cortexId = Guid.NewGuid();
        var remote = new PluginSyncSkillDto(
            Id: cortexId, Name: "s", Description: null, Content: "body",
            FrontmatterJson: "{}", ContentHash: "h",
            Scope: "user", ScopeId: "u1", Tags: Array.Empty<string>(), Source: null,
            IsOrphaned: false, Version: 2, UsageCount: 1,
            LastUsedAt: DateTime.UtcNow,
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);

        await cache.UpsertAsync(remote, CancellationToken.None);

        var after = await cache.GetByIdAsync(cortexId, CancellationToken.None);
        Assert.NotNull(after);
        Assert.Equal("s", after!.Name);

        // Local row with old id should be gone.
        var oldRow = await cache.GetByIdAsync(localId, CancellationToken.None);
        Assert.Null(oldRow);

        // skill_usage_events.skill_id should now point at cortex id.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM skill_usage_events WHERE skill_id = $id";
        cmd.Parameters.AddWithValue("$id", cortexId.ToString());
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }
}
