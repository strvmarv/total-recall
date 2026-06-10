using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Schema tests for the pinned tier — Migration 16 (pinned_memories +
/// pinned_knowledge). Mirrors the SqliteConnection + vec0 bootstrap used by
/// <see cref="SchemaTests"/> and <see cref="SqliteConnectionTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PinnedTierSchemaTests
{
    [Fact]
    public void Migration16_CreatesPinnedTables()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        foreach (var tbl in new[] { "pinned_memories", "pinned_knowledge" })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE name IN ($t, $t || '_vec', $t || '_fts')";
            cmd.Parameters.AddWithValue("$t", tbl);
            Assert.Equal(3L, (long)cmd.ExecuteScalar()!);
        }

        // Pinned tables must have the full current column set (scope,
        // entry_type, times_injected included from birth).
        using var cols = conn.CreateCommand();
        cols.CommandText = "SELECT COUNT(*) FROM pragma_table_info('pinned_memories') " +
            "WHERE name IN ('scope','entry_type','times_injected')";
        Assert.Equal(3L, (long)cols.ExecuteScalar()!);
    }

    [Fact]
    public void Migration16_IsIdempotent()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        MigrationRunner.RunMigrations(conn); // must not throw
    }

    [Fact]
    public void TableName_Pinned_ReturnsPinnedTables()
    {
        Assert.Equal("pinned_memories", MigrationRunner.TableName(Tier.Pinned, ContentType.Memory));
        Assert.Equal("pinned_knowledge", MigrationRunner.TableName(Tier.Pinned, ContentType.Knowledge));
    }
}
