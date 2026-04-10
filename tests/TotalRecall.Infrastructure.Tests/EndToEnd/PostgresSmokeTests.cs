// tests/TotalRecall.Infrastructure.Tests/EndToEnd/PostgresSmokeTests.cs
//
// End-to-end smoke tests that exercise the full Postgres + embedder path
// through the MCP handler layer.
//
// These tests are gated behind the TOTAL_RECALL_TEST_PG_CONN environment
// variable. If it is not set, every test skips gracefully via a custom
// [PgFact] attribute that sets FactAttribute.Skip at runtime during discovery.
//
// A unique schema (tr_smoke_<guid>) is created per test for isolation and
// dropped on cleanup. The FakeEmbedder (384-dim, deterministic) is used in
// place of a real remote model, so no API key is required.

using System;
using System.Text.Json;
using System.Threading;
using Npgsql;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Tests.TestSupport;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.EndToEnd;

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

[Trait("Category", "EndToEnd")]
public sealed class PostgresSmokeTests
{
    private static string? ConnStr =>
        Environment.GetEnvironmentVariable("TOTAL_RECALL_TEST_PG_CONN");

    /// <summary>
    /// Creates a data source that sets the Postgres search_path to the given
    /// schema, so all DDL and DML targets that schema without explicit
    /// qualification.
    /// </summary>
    private static NpgsqlDataSource CreateDataSource(string connStr, string schema)
    {
        var builder = new NpgsqlConnectionStringBuilder(connStr)
        {
            SearchPath = schema,
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    /// <summary>
    /// Creates a unique schema name and ensures it exists in the database.
    /// Returns the schema name and the data source scoped to it.
    /// The caller is responsible for dropping the schema when done.
    /// </summary>
    private static (string Schema, NpgsqlDataSource DataSource) CreateSchema(string connStr)
    {
        var schema = $"tr_smoke_{Guid.NewGuid():N}";

        // Use the root connection (no search_path override) to CREATE the schema.
        using var adminDs = NpgsqlDataSource.Create(connStr);
        using var conn = adminDs.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS \"{schema}\"";
        cmd.ExecuteNonQuery();

        var ds = CreateDataSource(connStr, schema);
        return (schema, ds);
    }

    /// <summary>
    /// Drops a schema that was created by <see cref="CreateSchema"/>.
    /// Cascades so any residual objects inside are removed too.
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
            // best-effort — don't fail the test on cleanup errors
        }
    }

    // -------------------------------------------------------------------------
    // Test 1 — store + search round-trip
    // -------------------------------------------------------------------------

    [PgFact]
    public async System.Threading.Tasks.Task StoreAndSearch_RoundTrip()
    {
        var connStr = ConnStr!; // non-null: [PgFact] skips when env var is absent

        var (schema, dataSource) = CreateSchema(connStr);
        await using (dataSource)
        {
            try
            {
                // 1. Run migrations into the temp schema.
                PostgresMigrationRunner.RunMigrations(dataSource, 384);

                // 2. Wire up the infrastructure stack.
                const string ownerId = "smoke-test";
                var store = new PostgresStore(dataSource, ownerId);
                var vectorSearch = new PgvectorSearch(dataSource, ownerId);
                var ftsSearch = new PostgresFtsSearch(dataSource, ownerId);
                var embedder = new FakeEmbedder();
                var hybridSearch = new HybridSearch(vectorSearch, ftsSearch, store);

                // 3. Build the MCP handlers.
                var storeHandler = new MemoryStoreHandler(store, embedder, vectorSearch);
                var searchHandler = new MemorySearchHandler(embedder, hybridSearch);

                // 4. Store a memory entry via the MCP handler.
                var storeArgs = JsonDocument.Parse(
                    """{"content": "test memory about authentication", "tier": "hot"}""")
                    .RootElement;

                var storeResult = await storeHandler.ExecuteAsync(storeArgs, CancellationToken.None);
                Assert.False(storeResult.IsError, "Store handler returned an error");
                Assert.NotEmpty(storeResult.Content);

                // 5. Search for the stored entry.
                var searchArgs = JsonDocument.Parse("""{"query": "auth"}""").RootElement;
                var searchResult = await searchHandler.ExecuteAsync(searchArgs, CancellationToken.None);
                Assert.False(searchResult.IsError, "Search handler returned an error");
                Assert.NotEmpty(searchResult.Content);

                // 6. Parse the JSON response and assert at least 1 result.
                var jsonText = searchResult.Content[0].Text;
                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;
                Assert.Equal(JsonValueKind.Array, root.ValueKind);
                Assert.True(root.GetArrayLength() >= 1,
                    $"Expected at least 1 search result, got {root.GetArrayLength()}");
            }
            finally
            {
                DropSchema(connStr, schema);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Test 2 — store with visibility and verify round-trip
    // -------------------------------------------------------------------------

    [PgFact]
    public async System.Threading.Tasks.Task StoreWithVisibility_Roundtrip()
    {
        var connStr = ConnStr!; // non-null: [PgFact] skips when env var is absent

        var (schema, dataSource) = CreateSchema(connStr);
        await using (dataSource)
        {
            try
            {
                // 1. Run migrations into the temp schema.
                PostgresMigrationRunner.RunMigrations(dataSource, 384);

                // 2. Wire up the infrastructure stack.
                const string ownerId = "smoke-test-visibility";
                var store = new PostgresStore(dataSource, ownerId);
                var vectorSearch = new PgvectorSearch(dataSource, ownerId);
                var embedder = new FakeEmbedder();

                // 3. Build the store handler.
                var storeHandler = new MemoryStoreHandler(store, embedder, vectorSearch);

                // 4. Store an entry with visibility: "team".
                var storeArgs = JsonDocument.Parse(
                    """{"content": "shared team knowledge", "tier": "hot", "visibility": "team"}""")
                    .RootElement;

                var storeResult = await storeHandler.ExecuteAsync(storeArgs, CancellationToken.None);
                Assert.False(storeResult.IsError, "Store handler returned an error");
                Assert.NotEmpty(storeResult.Content);

                // Extract the stored id from the response payload: {"id":"<id>"}.
                var payloadText = storeResult.Content[0].Text;
                using var payloadDoc = JsonDocument.Parse(payloadText);
                var id = payloadDoc.RootElement.GetProperty("id").GetString();
                Assert.False(string.IsNullOrEmpty(id), "Stored entry id must not be empty");

                // 5. Retrieve the entry directly from the store to verify it exists.
                var entry = store.Get(TotalRecall.Core.Tier.Hot, TotalRecall.Core.ContentType.Memory, id!);
                Assert.NotNull(entry);
                Assert.Equal(id, entry!.Id);
                Assert.Equal("shared team knowledge", entry.Content);
            }
            finally
            {
                DropSchema(connStr, schema);
            }
        }
    }
}
