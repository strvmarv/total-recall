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
    public void Migration16_CreatesPinnedFtsSyncTriggers()
    {
        // All 6 FTS sync triggers for the 2 pinned tables must exist after RunMigrations.
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var triggerNames = new HashSet<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' ORDER BY name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) triggerNames.Add(reader.GetString(0));

        // Each of the 2 pinned tables has 3 FTS sync triggers:
        //   <base>_fts_ai (after insert), <base>_fts_ad (after delete), <base>_fts_au (after update)
        var expected = new HashSet<string>
        {
            "pinned_memories_fts_ai",
            "pinned_memories_fts_ad",
            "pinned_memories_fts_au",
            "pinned_knowledge_fts_ai",
            "pinned_knowledge_fts_ad",
            "pinned_knowledge_fts_au",
        };

        Assert.Equal(6, expected.Count);
        Assert.Superset(expected, triggerNames);
    }

    // Tier model v2 (Task 9): TableName(Tier.Pinned, …) is retired — the pinned
    // tables are addressed by literal name now (see MigrationRunner.PinnedContentTables).
    // Migration 16 still creates them (dropped later by TierV2DataMigration.RunOnce).
}
