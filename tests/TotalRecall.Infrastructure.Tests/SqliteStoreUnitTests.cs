using System.Collections.Generic;
using System.Linq;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Deserialization-focused tests for <see cref="SqliteStore"/>. These insert
/// hand-crafted rows directly via SQL and round-trip them through <c>Get</c>
/// to verify every field is mapped correctly. The spec brief explicitly
/// allows the blur of unit/integration line here by using an in-memory DB.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqliteStoreUnitTests
{
    private static MsSqliteConnection NewMigratedConnection()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return conn;
    }

    private static void RawInsert(
        MsSqliteConnection conn,
        string table,
        string id,
        string content,
        string? summary,
        string? source,
        string? sourceTool,
        string? project,
        string tags,
        long createdAt,
        long updatedAt,
        long lastAccessedAt,
        int accessCount,
        double decayScore,
        string? parentId,
        string? collectionId,
        string metadata)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
INSERT INTO {table}
  (id, content, summary, source, source_tool, project, tags,
   created_at, updated_at, last_accessed_at, access_count,
   decay_score, parent_id, collection_id, metadata)
VALUES
  ($id, $content, $summary, $source, $source_tool, $project, $tags,
   $created_at, $updated_at, $last_accessed_at, $access_count,
   $decay_score, $parent_id, $collection_id, $metadata)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$summary", (object?)summary ?? System.DBNull.Value);
        cmd.Parameters.AddWithValue("$source", (object?)source ?? System.DBNull.Value);
        cmd.Parameters.AddWithValue("$source_tool", (object?)sourceTool ?? System.DBNull.Value);
        cmd.Parameters.AddWithValue("$project", (object?)project ?? System.DBNull.Value);
        cmd.Parameters.AddWithValue("$tags", tags);
        cmd.Parameters.AddWithValue("$created_at", createdAt);
        cmd.Parameters.AddWithValue("$updated_at", updatedAt);
        cmd.Parameters.AddWithValue("$last_accessed_at", lastAccessedAt);
        cmd.Parameters.AddWithValue("$access_count", accessCount);
        cmd.Parameters.AddWithValue("$decay_score", decayScore);
        cmd.Parameters.AddWithValue("$parent_id", (object?)parentId ?? System.DBNull.Value);
        cmd.Parameters.AddWithValue("$collection_id", (object?)collectionId ?? System.DBNull.Value);
        cmd.Parameters.AddWithValue("$metadata", metadata);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void RowToEntry_PopulatesAllFields()
    {
        using var conn = NewMigratedConnection();
        RawInsert(
            conn, "hot_memories",
            id: "entry-1",
            content: "hello world",
            summary: "a greeting",
            source: "manual-entry",
            sourceTool: "claude-code",
            project: "demo",
            tags: "[\"alpha\",\"beta\",\"gamma\"]",
            createdAt: 1000,
            updatedAt: 2000,
            lastAccessedAt: 3000,
            accessCount: 7,
            decayScore: 0.75,
            parentId: "parent-id",
            collectionId: "coll-id",
            metadata: "{\"k\":\"v\"}");

        using var store = new SqliteStore(conn);
        var entry = store.Get(Tier.Hot, ContentType.Memory, "entry-1");

        Assert.NotNull(entry);
        Assert.Equal("entry-1", entry!.Id);
        Assert.Equal("hello world", entry.Content);
        Assert.Equal("a greeting", entry.Summary!.Value);
        Assert.Equal("manual-entry", entry.Source!.Value);
        Assert.True(FSharpOption<SourceTool>.get_IsSome(entry.SourceTool));
        Assert.True(entry.SourceTool!.Value.IsClaudeCode);
        Assert.Equal("demo", entry.Project!.Value);
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, entry.Tags.ToArray());
        Assert.Equal(1000L, entry.CreatedAt);
        Assert.Equal(2000L, entry.UpdatedAt);
        Assert.Equal(3000L, entry.LastAccessedAt);
        Assert.Equal(7, entry.AccessCount);
        Assert.Equal(0.75, entry.DecayScore);
        Assert.Equal("parent-id", entry.ParentId!.Value);
        Assert.Equal("coll-id", entry.CollectionId!.Value);
        Assert.Equal("{\"k\":\"v\"}", entry.MetadataJson);
    }

    [Fact]
    public void RowToEntry_HandlesNullableColumns()
    {
        using var conn = NewMigratedConnection();
        RawInsert(
            conn, "hot_memories",
            id: "entry-2",
            content: "bare",
            summary: null,
            source: null,
            sourceTool: null,
            project: null,
            tags: "[]",
            createdAt: 1, updatedAt: 1, lastAccessedAt: 1,
            accessCount: 0, decayScore: 1.0,
            parentId: null, collectionId: null,
            metadata: "{}");

        using var store = new SqliteStore(conn);
        var entry = store.Get(Tier.Hot, ContentType.Memory, "entry-2");

        Assert.NotNull(entry);
        Assert.True(FSharpOption<string>.get_IsNone(entry!.Summary));
        Assert.True(FSharpOption<string>.get_IsNone(entry.Source));
        Assert.True(FSharpOption<SourceTool>.get_IsNone(entry.SourceTool));
        Assert.True(FSharpOption<string>.get_IsNone(entry.Project));
        Assert.True(FSharpOption<string>.get_IsNone(entry.ParentId));
        Assert.True(FSharpOption<string>.get_IsNone(entry.CollectionId));
    }

    [Fact]
    public void RowToEntry_EmptyTagsAndMetadata()
    {
        using var conn = NewMigratedConnection();
        RawInsert(
            conn, "hot_memories",
            id: "entry-3",
            content: "c",
            summary: null, source: null, sourceTool: null, project: null,
            tags: "[]",
            createdAt: 1, updatedAt: 1, lastAccessedAt: 1,
            accessCount: 0, decayScore: 1.0,
            parentId: null, collectionId: null,
            metadata: "{}");

        using var store = new SqliteStore(conn);
        var entry = store.Get(Tier.Hot, ContentType.Memory, "entry-3");

        Assert.NotNull(entry);
        Assert.Empty(entry!.Tags);
        Assert.Equal("{}", entry.MetadataJson);
    }

    [Fact]
    public void Get_MissingId_ReturnsNull()
    {
        using var conn = NewMigratedConnection();
        using var store = new SqliteStore(conn);
        var entry = store.Get(Tier.Hot, ContentType.Memory, "does-not-exist");
        Assert.Null(entry);
    }
}
