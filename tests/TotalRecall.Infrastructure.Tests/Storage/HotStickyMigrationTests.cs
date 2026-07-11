using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Storage;

[Trait("Category", "Integration")]
public sealed class HotStickyMigrationTests
{
    [Fact]
    public void Migration17_AddsStickyColumnToHotTables()
    {
        using var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        foreach (var tbl in new[] { "hot_memories", "hot_knowledge" })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info($t) WHERE name = 'sticky'";
            cmd.Parameters.AddWithValue("$t", tbl);
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public void Migration17_IsIdempotent()
    {
        using var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        MigrationRunner.RunMigrations(conn); // must not throw
    }
}
