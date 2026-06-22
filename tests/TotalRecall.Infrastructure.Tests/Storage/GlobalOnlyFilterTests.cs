using System;
using System.Collections.Generic;
using Npgsql;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Storage;

/// <summary>
/// Fact attribute that skips the test when TOTAL_RECALL_TEST_PG_CONN is not
/// set. The check happens at xUnit discovery time (attribute instantiation),
/// which produces a genuine "Skipped" result rather than a test failure.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TOTAL_RECALL_TEST_PG_CONN") is null)
            Skip = "TOTAL_RECALL_TEST_PG_CONN not set";
    }
}

/// <summary>
/// Tests for the GlobalOnly filter in ListEntriesOpts. When GlobalOnly=true,
/// List returns only rows with project IS NULL.
/// </summary>
public class GlobalOnlyFilterTests
{
    private static (MsSqliteConnection conn, SqliteStore store) NewSqliteFixture()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new SqliteStore(conn));
    }

    [Fact]
    public void GlobalOnly_returns_only_null_project_rows()
    {
        var (conn, store) = NewSqliteFixture();
        using (conn)
        {
            store.Insert(Tier.Pinned, ContentType.Memory,
                new InsertEntryOpts("global", Id: "g"));
            store.Insert(Tier.Pinned, ContentType.Memory,
                new InsertEntryOpts("repo", Id: "r", Project: "o/r"));

            var globals = store.List(Tier.Pinned, ContentType.Memory,
                new ListEntriesOpts { GlobalOnly = true });

            Assert.Single(globals);
            Assert.Equal("g", globals[0].Id);
        }
    }

    [Fact]
    public void Project_with_include_global_returns_repo_and_globals()
    {
        var (conn, store) = NewSqliteFixture();
        using (conn)
        {
            store.Insert(Tier.Pinned, ContentType.Memory,
                new InsertEntryOpts("global", Id: "g"));
            store.Insert(Tier.Pinned, ContentType.Memory,
                new InsertEntryOpts("repo", Id: "r", Project: "o/r"));
            store.Insert(Tier.Pinned, ContentType.Memory,
                new InsertEntryOpts("other", Id: "x", Project: "o/x"));

            var list = store.List(Tier.Pinned, ContentType.Memory,
                new ListEntriesOpts { Project = "o/r", IncludeGlobal = true });

            Assert.Equal(2, list.Count);
            Assert.DoesNotContain(list, e => e.Id == "x");
        }
    }

    /// <summary>
    /// Postgres parity test: GlobalOnly filter works with PostgresStore.
    /// Skipped locally when TOTAL_RECALL_TEST_PG_CONN is not set.
    /// </summary>
    [PgFact]
    public void PostgresStore_GlobalOnly_returns_only_null_project_rows()
    {
        var connStr = Environment.GetEnvironmentVariable("TOTAL_RECALL_TEST_PG_CONN");
        if (connStr is null)
            return; // Already skipped by [PgFact], but be defensive

        var (schema, dataSource) = CreateSchema(connStr);
        try
        {
            var store = new PostgresStore(dataSource, "test-owner", 0);

            store.Insert(Tier.Pinned, ContentType.Memory,
                new InsertEntryOpts("global", Id: "g"));
            store.Insert(Tier.Pinned, ContentType.Memory,
                new InsertEntryOpts("repo", Id: "r", Project: "o/r"));

            var globals = store.List(Tier.Pinned, ContentType.Memory,
                new ListEntriesOpts { GlobalOnly = true });

            Assert.Single(globals);
            Assert.Equal("g", globals[0].Id);
        }
        finally
        {
            DropSchema(connStr, schema);
        }
    }

    /// <summary>
    /// Creates a unique schema name and ensures it exists in the database.
    /// </summary>
    private static (string Schema, NpgsqlDataSource DataSource) CreateSchema(string connStr)
    {
        var schema = $"tr_test_globalonly_{Guid.NewGuid():N}";

        using var adminDs = NpgsqlDataSource.Create(connStr);
        using var conn = adminDs.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{schema}\"";
        cmd.ExecuteNonQuery();

        var connBuilder = new NpgsqlConnectionStringBuilder(connStr)
        {
            SearchPath = $"{schema},public",
        };
        var dsBuilder = new NpgsqlDataSourceBuilder(connBuilder.ConnectionString);
        dsBuilder.UseVector();
        var ds = dsBuilder.Build();

        // Run migrations on the new schema
        PostgresMigrationRunner.RunMigrations(ds, 384);

        return (schema, ds);
    }

    /// <summary>
    /// Drops a schema that was created by CreateSchema.
    /// </summary>
    private static void DropSchema(string connStr, string schema)
    {
        try
        {
            using var adminDs = NpgsqlDataSource.Create(connStr);
            using var conn = adminDs.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
