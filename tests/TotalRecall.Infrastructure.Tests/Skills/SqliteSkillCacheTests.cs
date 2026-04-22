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

    private static PluginSyncSkillDto MakeDto(Guid id, string name) => new(
        Id: id, Name: name, Description: "d", Content: "body",
        Scope: "user", ScopeId: "u-1",
        Tags: new[] { "t" }, Source: "claude-code",
        IsOrphaned: false, Version: 1,
        CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow);
}
