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

    [Fact]
    public void Migration13_AddsUsageColumnsAndEventsTable()
    {
        using var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        // skill_cache columns
        using var c1 = conn.CreateCommand();
        c1.CommandText = "PRAGMA table_info(skill_cache)";
        var cols = new HashSet<string>(StringComparer.Ordinal);
        using (var r = c1.ExecuteReader())
            while (r.Read()) cols.Add(r.GetString(1));
        Assert.Contains("usage_count", cols);
        Assert.Contains("last_used_at", cols);
        Assert.Contains("decay_score", cols);

        // skill_usage_events table
        using var c2 = conn.CreateCommand();
        c2.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='skill_usage_events'";
        Assert.NotNull(c2.ExecuteScalar());

        // Indexes
        using var c3 = conn.CreateCommand();
        c3.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='skill_usage_events'";
        var idxNames = new List<string>();
        using (var r = c3.ExecuteReader()) while (r.Read()) idxNames.Add(r.GetString(0));
        Assert.Contains("ix_skill_usage_events_skill_id", idxNames);
        Assert.Contains("ix_skill_usage_events_unsynced", idxNames);
    }
}
