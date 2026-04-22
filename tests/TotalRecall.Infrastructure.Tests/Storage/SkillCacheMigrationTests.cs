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
}
