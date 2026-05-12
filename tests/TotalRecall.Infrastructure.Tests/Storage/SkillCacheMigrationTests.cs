using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Storage;

public class SkillCacheMigrationTests
{
    [Fact]
    public void Migration_creates_skill_cache_table_with_expected_columns()
    {
        using var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(skill_cache)";
        using var reader = cmd.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(1));

        Assert.Contains("id", columns);
        Assert.Contains("name", columns);
        Assert.Contains("description", columns);
        Assert.Contains("scope", columns);
        Assert.Contains("scope_id", columns);
        Assert.Contains("tags", columns);
        Assert.Contains("source", columns);
        Assert.Contains("version", columns);
        Assert.Contains("is_orphaned", columns);
        Assert.Contains("updated_at", columns);
    }

    [Fact]
    public void Migration12_AddsContentAndEmbeddingColumnsToSkillCache()
    {
        using var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        // Assert new columns exist on skill_cache
        using var colCmd = conn.CreateCommand();
        colCmd.CommandText = "PRAGMA table_info(skill_cache)";
        using var colReader = colCmd.ExecuteReader();
        var columns = new List<string>();
        while (colReader.Read()) columns.Add(colReader.GetString(1));

        Assert.Contains("content", columns);
        Assert.Contains("frontmatter_json", columns);
        Assert.Contains("content_hash", columns);
        Assert.Contains("content_embedding", columns);
        Assert.Contains("embedder_fingerprint", columns);

        // Assert the natural-key unique index exists
        using var idxCmd = conn.CreateCommand();
        idxCmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'ux_skill_cache_natural_key'";
        var indexName = idxCmd.ExecuteScalar() as string;
        Assert.Equal("ux_skill_cache_natural_key", indexName);
    }
}
