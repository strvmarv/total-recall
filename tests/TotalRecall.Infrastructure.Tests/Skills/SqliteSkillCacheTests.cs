using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Skills;

public class SqliteSkillCacheTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly SqliteSkillCache _cache;

    public SqliteSkillCacheTests()
    {
        _conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(_conn);
        _cache = new SqliteSkillCache(_conn);
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public async Task Upsert_inserts_new_row()
    {
        var id = Guid.NewGuid();
        await _cache.UpsertAsync(MakeDto(id, "alpha"), CancellationToken.None);
        var rows = await _cache.GetAllAsync(CancellationToken.None);
        Assert.Single(rows);
        Assert.Equal("alpha", rows[0].Name);
    }

    [Fact]
    public async Task Upsert_updates_existing_row()
    {
        var id = Guid.NewGuid();
        await _cache.UpsertAsync(MakeDto(id, "alpha"), CancellationToken.None);
        await _cache.UpsertAsync(MakeDto(id, "alpha-v2"), CancellationToken.None);
        var rows = await _cache.GetAllAsync(CancellationToken.None);
        Assert.Single(rows);
        Assert.Equal("alpha-v2", rows[0].Name);
    }

    [Fact]
    public async Task Remove_deletes_row()
    {
        var id = Guid.NewGuid();
        await _cache.UpsertAsync(MakeDto(id, "alpha"), CancellationToken.None);
        await _cache.RemoveAsync(id, CancellationToken.None);
        var rows = await _cache.GetAllAsync(CancellationToken.None);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task UpsertScannedAsync_RoundtripsContentAndFrontmatter()
    {
        var imported = new ImportedSkill(
            Name: "test-skill",
            Description: "desc",
            Content: "body",
            FrontmatterJson: "{\"x\":1}",
            Files: Array.Empty<ImportedSkillFile>(),
            SourcePath: "fixtures/test.md",
            SuggestedScope: "user",
            SuggestedScopeId: "u1",
            SuggestedTags: new[] { "a", "b" });

        await _cache.UpsertScannedAsync(imported, contentHash: "abc",
            embedding: null, embedderFingerprint: null, CancellationToken.None);

        var hit = await _cache.GetByNaturalKeyAsync("test-skill", "user", "u1", CancellationToken.None);
        Assert.NotNull(hit);
        Assert.Equal("body", hit!.Content);
        Assert.Equal("desc", hit.Description);
        Assert.Equal("{\"x\":1}", hit.FrontmatterJson);
        Assert.Equal("abc", hit.ContentHash);
        Assert.False(hit.IsOrphaned);
    }

    [Fact]
    public async Task MarkOrphansAsync_KeepsListedNaturalKeys_OrphansOthers()
    {
        await SeedAsync(_cache, "alpha");
        await SeedAsync(_cache, "beta");
        await SeedAsync(_cache, "gamma");

        await _cache.MarkOrphansAsync(
            new List<(string, string, string)>
            {
                ("alpha", "user", "u1"),
                ("beta",  "user", "u1"),
            },
            CancellationToken.None);

        var a = await _cache.GetByNaturalKeyAsync("alpha", "user", "u1", CancellationToken.None);
        var b = await _cache.GetByNaturalKeyAsync("beta",  "user", "u1", CancellationToken.None);
        var g = await _cache.GetByNaturalKeyAsync("gamma", "user", "u1", CancellationToken.None);
        Assert.False(a!.IsOrphaned);
        Assert.False(b!.IsOrphaned);
        Assert.True(g!.IsOrphaned);
    }

    [Fact]
    public async Task MarkOrphansAsync_EmptyKeep_OrphansAllRows()
    {
        await SeedAsync(_cache, "alpha");
        await SeedAsync(_cache, "beta");

        await _cache.MarkOrphansAsync(
            new List<(string, string, string)>(),
            CancellationToken.None);

        var a = await _cache.GetByNaturalKeyAsync("alpha", "user", "u1", CancellationToken.None);
        var b = await _cache.GetByNaturalKeyAsync("beta",  "user", "u1", CancellationToken.None);
        Assert.True(a!.IsOrphaned);
        Assert.True(b!.IsOrphaned);
    }

    private static Task SeedAsync(SqliteSkillCache cache, string name) =>
        cache.UpsertScannedAsync(
            new ImportedSkill(
                Name: name,
                Description: "desc",
                Content: "body",
                FrontmatterJson: "{}",
                Files: Array.Empty<ImportedSkillFile>(),
                SourcePath: $"fixtures/{name}.md",
                SuggestedScope: "user",
                SuggestedScopeId: "u1",
                SuggestedTags: Array.Empty<string>()),
            contentHash: "h",
            embedding: null,
            embedderFingerprint: null,
            CancellationToken.None);

    [Fact]
    public async Task RecordInvocationAsync_BumpsCountAndWritesEvent()
    {
        using var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var cache = new SqliteSkillCache(conn);

        var imported = new ImportedSkill(
            Name: "s", Description: "d", Content: "c",
            FrontmatterJson: "{}", Files: Array.Empty<ImportedSkillFile>(),
            SourcePath: "/v/s.md", SuggestedScope: "user", SuggestedScopeId: "u1",
            SuggestedTags: Array.Empty<string>());
        await cache.UpsertScannedAsync(imported, "h", null, null, CancellationToken.None);
        var seeded = await cache.GetByNaturalKeyAsync("s", "user", "u1", CancellationToken.None);

        await cache.RecordInvocationAsync(seeded!.Id, host: "claude-code",
            sessionId: "sess-1", occurredAt: DateTime.UtcNow, CancellationToken.None);
        await cache.RecordInvocationAsync(seeded.Id, host: "claude-code",
            sessionId: "sess-1", occurredAt: DateTime.UtcNow, CancellationToken.None);

        var after = await cache.GetByNaturalKeyAsync("s", "user", "u1", CancellationToken.None);
        Assert.Equal(2, after!.UsageCount);
        Assert.NotNull(after.LastUsedAt);
        Assert.True(after.DecayScore > 0);

        using var ev = conn.CreateCommand();
        ev.CommandText = "SELECT COUNT(*) FROM skill_usage_events WHERE skill_id=$id";
        ev.Parameters.AddWithValue("$id", seeded.Id.ToString());
        Assert.Equal(2L, (long)ev.ExecuteScalar()!);
    }

    private static PluginSyncSkillDto MakeDto(Guid id, string name) => new(
        Id: id, Name: name, Description: "d", Content: "body",
        Scope: "user", ScopeId: "u-1",
        Tags: new[] { "t" }, Source: "claude-code",
        IsOrphaned: false, Version: 1,
        CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);
}
