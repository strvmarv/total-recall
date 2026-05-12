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

    private static PluginSyncSkillDto MakeDto(Guid id, string name) => new(
        Id: id, Name: name, Description: "d", Content: "body",
        Scope: "user", ScopeId: "u-1",
        Tags: new[] { "t" }, Source: "claude-code",
        IsOrphaned: false, Version: 1,
        CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);
}
