using System;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests;

[Trait("Category", "Integration")]
public sealed class ImportLogTests
{
    private static (MsSqliteConnection conn, ImportLog log) NewLog()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new ImportLog(conn));
    }

    [Fact]
    public void ContentHash_KnownInput_ProducesExpectedSha256()
    {
        // sha256("hello") = 2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824
        Assert.Equal(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            ImportLog.ContentHash("hello"));
    }

    [Fact]
    public void IsAlreadyImported_NotPresent_ReturnsFalse()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            Assert.False(log.IsAlreadyImported("nonexistent-hash"));
        }
    }

    [Fact]
    public void LogImport_NewEntry_InsertsRow()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            var hash = ImportLog.ContentHash("payload");
            log.LogImport("claude-code", "/tmp/foo.jsonl", hash, "entry-1", Tier.Hot, ContentType.Memory);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT id, timestamp, source_tool, source_path, content_hash,
       target_entry_id, target_tier, target_type
  FROM import_log";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.False(string.IsNullOrEmpty(reader.GetString(0)));
            Assert.True(reader.GetInt64(1) > 0);
            Assert.Equal("claude-code", reader.GetString(2));
            Assert.Equal("/tmp/foo.jsonl", reader.GetString(3));
            Assert.Equal(hash, reader.GetString(4));
            Assert.Equal("entry-1", reader.GetString(5));
            Assert.Equal("hot", reader.GetString(6));
            Assert.Equal("memory", reader.GetString(7));
            Assert.False(reader.Read());
        }
    }

    [Fact]
    public void LogImport_DuplicateInsert_NoOp()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            var hash = ImportLog.ContentHash("payload");
            log.LogImport("claude-code", "/tmp/foo.jsonl", hash, "entry-1", Tier.Hot, ContentType.Memory);
            log.LogImport("claude-code", "/tmp/foo.jsonl", hash, "entry-2", Tier.Warm, ContentType.Knowledge);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM import_log";
            var count = (long)cmd.ExecuteScalar()!;
            Assert.Equal(1L, count);
        }
    }

    [Fact]
    public void IsAlreadyImported_AfterLog_ReturnsTrue()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            var hash = ImportLog.ContentHash("payload");
            Assert.False(log.IsAlreadyImported(hash));
            log.LogImport("claude-code", "/tmp/foo.jsonl", hash, "e", Tier.Hot, ContentType.Memory);
            Assert.True(log.IsAlreadyImported(hash));
        }
    }

    [Fact]
    public void LogImport_TierAndTypeStrings_Hot_Memory_RoundTrip()
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            log.LogImport("t", "/p", "h", "e", Tier.Hot, ContentType.Memory);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT target_tier, target_type FROM import_log";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("hot", reader.GetString(0));
            Assert.Equal("memory", reader.GetString(1));
        }
    }

    [Theory]
    [InlineData("hot", "memory")]
    [InlineData("warm", "memory")]
    [InlineData("cold", "memory")]
    [InlineData("hot", "knowledge")]
    [InlineData("warm", "knowledge")]
    [InlineData("cold", "knowledge")]
    public void LogImport_AllTierTypeCombinations_StoresExpectedStrings(string tierStr, string typeStr)
    {
        var (conn, log) = NewLog();
        using (conn)
        {
            var tier = tierStr switch
            {
                "hot" => Tier.Hot,
                "warm" => Tier.Warm,
                "cold" => Tier.Cold,
                _ => throw new ArgumentException(nameof(tierStr)),
            };
            var type = typeStr switch
            {
                "memory" => ContentType.Memory,
                "knowledge" => ContentType.Knowledge,
                _ => throw new ArgumentException(nameof(typeStr)),
            };
            // distinct path so each combo gets its own stable id
            log.LogImport("tool", $"/path/{tierStr}-{typeStr}", "h", "e", tier, type);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT target_tier, target_type FROM import_log";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(tierStr, reader.GetString(0));
            Assert.Equal(typeStr, reader.GetString(1));
        }
    }
}
