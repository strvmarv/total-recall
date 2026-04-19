using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Full CRUD round-trip tests for <see cref="SqliteStore"/> against a real
/// <c>:memory:</c> SQLite database with the full migration stack applied.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqliteStoreIntegrationTests
{
    private static (MsSqliteConnection conn, SqliteStore store) NewStore()
    {
        var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return (conn, new SqliteStore(conn));
    }

    /// <summary>
    /// Count vec rows whose rowid has no matching content row, plus
    /// content rows whose rowid has no matching vec row, across every
    /// (tier, type) pair. Returns the total number of orphans. Useful
    /// as a post-test invariant assertion.
    /// </summary>
    private static long CountOrphans(MsSqliteConnection conn)
    {
        long total = 0;
        foreach (var (tier, type) in MigrationRunner_AllPairs())
        {
            var contentTable = $"{tier}_{type}";
            var vecTable = $"{tier}_{type}_vec";

            using var vecCmd = conn.CreateCommand();
            vecCmd.CommandText =
                $"SELECT COUNT(*) FROM {vecTable} " +
                $"WHERE rowid NOT IN (SELECT rowid FROM {contentTable})";
            total += (long)vecCmd.ExecuteScalar()!;

            using var contentCmd = conn.CreateCommand();
            contentCmd.CommandText =
                $"SELECT COUNT(*) FROM {contentTable} " +
                $"WHERE rowid NOT IN (SELECT rowid FROM {vecTable})";
            total += (long)contentCmd.ExecuteScalar()!;
        }
        return total;
    }

    private static IEnumerable<(string Tier, string Type)> MigrationRunner_AllPairs() =>
        new[]
        {
            ("hot", "memories"),  ("warm", "memories"),  ("cold", "memories"),
            ("hot", "knowledge"), ("warm", "knowledge"), ("cold", "knowledge"),
        };

    [Fact]
    public void StoreDeleteStore_NoOrphansRemain()
    {
        // End-to-end regression for the 0.6.7 dogfood failure sequence:
        //   1. memory_store → E1 at some rowid R
        //   2. memory_delete E1
        //   3. memory_store → E2, which collided with the orphan vec row
        //      SQLite had left behind at R, causing a UNIQUE constraint
        //      crash on hot_memories_vec PK.
        //
        // With the fixes applied (reordered delete, rowid-aware vec
        // delete, and transactional insert), the exact same sequence
        // must succeed and leave zero orphans in any of the six
        // content/vec table pairs.
        var (conn, store) = NewStore();
        using (conn)
        {
            var search = new Search.VectorSearch(conn);
            var e1 = new float[384];
            e1[0] = 1f;
            var e2 = new float[384];
            e2[1] = 1f;

            // Store E1.
            var id1 = store.InsertWithEmbedding(
                Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("first"),
                e1);

            // Delete E1 — vec row first (via resolved rowid), then content row.
            var rowid1 = store.GetInternalKey(Tier.Hot, ContentType.Memory, id1);
            Assert.NotNull(rowid1);
            search.DeleteEmbedding(Tier.Hot, ContentType.Memory, rowid1!.Value);
            store.Delete(Tier.Hot, ContentType.Memory, id1);

            // Store E2 — this is the call that used to crash on the old
            // code path.
            var id2 = store.InsertWithEmbedding(
                Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("second"),
                e2);

            // Both invariants must hold:
            //   - E2 is retrievable via vector search
            //   - No orphan rows exist anywhere
            var results = search.SearchByVector(
                Tier.Hot, ContentType.Memory,
                e2,
                new Search.VectorSearchOpts(TopK: 1));
            Assert.Single(results);
            Assert.Equal(id2, results[0].Id);

            Assert.Equal(0L, CountOrphans(conn));
        }
    }

    [Fact]
    public void InsertWithEmbedding_HappyPath_InsertsBothRows()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var embedding = new float[384];
            embedding[0] = 1f;

            var id = store.InsertWithEmbedding(
                Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("hello"),
                embedding);

            var entry = store.Get(Tier.Hot, ContentType.Memory, id);
            Assert.NotNull(entry);
            Assert.Equal("hello", entry!.Content);

            // The matching vec row must exist at the same rowid. We check
            // by running a KNN search and confirming we get this entry back.
            var search = new Search.VectorSearch(conn);
            var results = search.SearchByVector(
                Tier.Hot, ContentType.Memory,
                embedding,
                new Search.VectorSearchOpts(TopK: 1));
            Assert.Single(results);
            Assert.Equal(id, results[0].Id);
        }
    }

    [Fact]
    public void InsertWithEmbedding_OnVecInsertFailure_RollsBackContentRow()
    {
        // Belt-and-suspenders for the 0.6.7 orphan-content-row bug. If the
        // vec insert crashes (e.g. rowid collision with a pre-existing
        // orphan from an earlier buggy release), the transactionally-
        // coupled content insert MUST also roll back so we do not leave a
        // content row behind without an embedding.
        var (conn, store) = NewStore();
        using (conn)
        {
            // Plant an orphan vec row at the rowid the next content insert
            // will get. SQLite allocates rowid = MAX(existing)+1, and the
            // table is empty, so the next rowid is 1.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO hot_memories_vec (rowid, embedding) VALUES (1, zeroblob(1536))";
                cmd.ExecuteNonQuery();
            }

            var embedding = new float[384];
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
                store.InsertWithEmbedding(
                    Tier.Hot, ContentType.Memory,
                    new InsertEntryOpts("should roll back"),
                    embedding));

            // Content row must have been rolled back.
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM hot_memories";
            Assert.Equal(0L, (long)countCmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public void Insert_Get_RoundTrip()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id = store.Insert(
                Tier.Hot, ContentType.Memory,
                new InsertEntryOpts(
                    Content: "remember this",
                    Summary: "brief",
                    Source: "user",
                    SourceTool: SourceTool.ClaudeCode,
                    Project: "proj",
                    Tags: new[] { "t1", "t2" },
                    MetadataJson: "{\"a\":1}"));

            var entry = store.Get(Tier.Hot, ContentType.Memory, id);
            Assert.NotNull(entry);
            Assert.Equal("remember this", entry!.Content);
            Assert.Equal("brief", entry.Summary!.Value);
            Assert.Equal("user", entry.Source!.Value);
            Assert.True(entry.SourceTool!.Value.IsClaudeCode);
            Assert.Equal("proj", entry.Project!.Value);
            Assert.Equal(new[] { "t1", "t2" }, entry.Tags.ToArray());
            Assert.Equal("{\"a\":1}", entry.MetadataJson);
            Assert.Equal(0, entry.AccessCount);
            Assert.Equal(1.0, entry.DecayScore);
            Assert.Equal(entry.CreatedAt, entry.UpdatedAt);
        }
    }

    [Fact]
    public void Update_TouchUpdatesAccessCountAndTimestamp()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id = store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("hi"));
            var before = store.Get(Tier.Hot, ContentType.Memory, id)!;
            Thread.Sleep(5);

            store.Update(Tier.Hot, ContentType.Memory, id,
                new UpdateEntryOpts { Touch = true });

            var after = store.Get(Tier.Hot, ContentType.Memory, id)!;
            Assert.Equal(before.AccessCount + 1, after.AccessCount);
            Assert.True(after.LastAccessedAt >= before.LastAccessedAt);
            Assert.True(after.UpdatedAt >= before.UpdatedAt);
        }
    }

    [Fact]
    public void Update_PartialFields_OnlyChangesSpecified()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id = store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts(
                    Content: "orig",
                    Summary: "orig-summary",
                    Project: "p"));

            store.Update(Tier.Hot, ContentType.Memory, id,
                new UpdateEntryOpts { Content = "new" });

            var after = store.Get(Tier.Hot, ContentType.Memory, id)!;
            Assert.Equal("new", after.Content);
            Assert.Equal("orig-summary", after.Summary!.Value);
            Assert.Equal("p", after.Project!.Value);
        }
    }

    [Fact]
    public void Delete_RemovesRow()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id = store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("bye"));
            store.Delete(Tier.Hot, ContentType.Memory, id);
            Assert.Null(store.Get(Tier.Hot, ContentType.Memory, id));
        }
    }

    [Fact]
    public void List_OrderByDefault_ReturnsNewestFirst()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var a = store.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("a"));
            Thread.Sleep(2);
            var b = store.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("b"));
            Thread.Sleep(2);
            var c = store.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("c"));

            var list = store.List(Tier.Hot, ContentType.Memory);
            Assert.Equal(3, list.Count);
            // Newest first
            Assert.Equal(c, list[0].Id);
            Assert.Equal(b, list[1].Id);
            Assert.Equal(a, list[2].Id);
        }
    }

    [Fact]
    public void List_ProjectFilter_OnlyReturnsMatchingProject()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("p1", Project: "alpha"));
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("p2", Project: "beta"));
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("p3"));

            var list = store.List(Tier.Hot, ContentType.Memory,
                new ListEntriesOpts { Project = "alpha" });
            Assert.Single(list);
            Assert.Equal("p1", list[0].Content);
        }
    }

    [Fact]
    public void List_IncludeGlobal_ReturnsProjectAndNullProjectRows()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("p1", Project: "alpha"));
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("p2", Project: "beta"));
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("global"));

            var list = store.List(Tier.Hot, ContentType.Memory,
                new ListEntriesOpts { Project = "alpha", IncludeGlobal = true });
            Assert.Equal(2, list.Count);
            var contents = list.Select(e => e.Content).ToHashSet();
            Assert.Contains("p1", contents);
            Assert.Contains("global", contents);
        }
    }

    [Fact]
    public void List_LimitClause_CapsResults()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            for (var i = 0; i < 5; i++)
                store.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts($"e{i}"));

            var list = store.List(Tier.Hot, ContentType.Memory,
                new ListEntriesOpts { Limit = 2 });
            Assert.Equal(2, list.Count);
        }
    }

    [Fact]
    public void Count_ReturnsRowCount()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            Assert.Equal(0, store.Count(Tier.Hot, ContentType.Memory));
            store.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("a"));
            store.Insert(Tier.Hot, ContentType.Memory, new InsertEntryOpts("b"));
            Assert.Equal(2, store.Count(Tier.Hot, ContentType.Memory));
        }
    }

    [Fact]
    public void ListByMetadata_FiltersOnJsonExtract()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("hit", MetadataJson: "{\"k\":\"v\"}"));
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("miss", MetadataJson: "{\"k\":\"other\"}"));

            var list = store.ListByMetadata(
                Tier.Hot, ContentType.Memory,
                new Dictionary<string, string> { ["k"] = "v" });
            Assert.Single(list);
            Assert.Equal("hit", list[0].Content);
        }
    }

    [Fact]
    public void ListByMetadata_RejectsInvalidKey()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            Assert.Throws<ArgumentException>(() =>
                store.ListByMetadata(
                    Tier.Hot, ContentType.Memory,
                    new Dictionary<string, string> { ["1bad"] = "v" }));
        }
    }

    [Fact]
    public void ListByMetadata_RejectsEmptyFilter()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            Assert.Throws<ArgumentException>(() =>
                store.ListByMetadata(
                    Tier.Hot, ContentType.Memory,
                    new Dictionary<string, string>()));
        }
    }

    [Fact]
    public void Move_TransfersRowAcrossTiers()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id = store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("travelling"));
            var original = store.Get(Tier.Hot, ContentType.Memory, id)!;
            Thread.Sleep(5);

            store.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, id);

            Assert.Null(store.Get(Tier.Hot, ContentType.Memory, id));
            var moved = store.Get(Tier.Warm, ContentType.Memory, id);
            Assert.NotNull(moved);
            Assert.Equal("travelling", moved!.Content);
            Assert.Equal(original.CreatedAt, moved.CreatedAt);
            Assert.True(moved.UpdatedAt >= original.UpdatedAt);
        }
    }

    [Fact]
    public void Move_NonexistentRow_Throws()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            Assert.Throws<InvalidOperationException>(() =>
                store.Move(Tier.Hot, ContentType.Memory,
                           Tier.Warm, ContentType.Memory,
                           "nope"));
        }
    }

    [Fact]
    public void OrderBy_InvalidColumn_Throws()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            Assert.Throws<ArgumentException>(() =>
                store.List(Tier.Hot, ContentType.Memory,
                    new ListEntriesOpts { OrderBy = "id ASC" }));
        }
    }

    [Fact]
    public void OrderBy_InvalidDirection_Throws()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            Assert.Throws<ArgumentException>(() =>
                store.List(Tier.Hot, ContentType.Memory,
                    new ListEntriesOpts { OrderBy = "created_at FOO" }));
        }
    }

    [Fact]
    public void OrderBy_TooManyParts_Throws()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            Assert.Throws<ArgumentException>(() =>
                store.List(Tier.Hot, ContentType.Memory,
                    new ListEntriesOpts { OrderBy = "created_at ASC extra" }));
        }
    }

    [Fact]
    public void Migration8_AddsScope_ColumnExistsOnAllContentTables()
    {
        var (conn, _) = NewStore();
        using (conn)
        {
            foreach (var (tier, type) in MigrationRunner_AllPairs())
            {
                var table = $"{tier}_{type}";
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA table_info({table})";
                using var reader = cmd.ExecuteReader();
                var columns = new List<string>();
                while (reader.Read())
                    columns.Add(reader.GetString(1));
                Assert.Contains("scope", columns);
            }
        }
    }

    [Fact]
    public void Insert_WithScope_RoundTripsCorrectly()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id = store.Insert(
                Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("scoped content", Scope: "user:paul"));

            var entries = store.List(Tier.Hot, ContentType.Memory);
            var entry = entries.Single(e => e.Id == id);
            Assert.Equal("user:paul", entry.Scope);
        }
    }

    [Fact]
    public void Insert_WithoutScope_DefaultsToEmptyString()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id = store.Insert(
                Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("no scope"));

            var entries = store.List(Tier.Hot, ContentType.Memory);
            var entry = entries.Single(e => e.Id == id);
            Assert.Equal("", entry.Scope);
        }
    }

    [Fact]
    public void Migration9_AddsEntryType_ColumnExistsOnAllContentTables()
    {
        var (conn, _) = NewStore();
        using (conn)
        {
            foreach (var (tier, type) in MigrationRunner_AllPairs())
            {
                var table = $"{tier}_{type}";
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA table_info({table})";
                using var reader = cmd.ExecuteReader();
                var columns = new List<string>();
                while (reader.Read())
                    columns.Add(reader.GetString(1));
                Assert.Contains("entry_type", columns);
            }
        }
    }

    [Fact]
    public void Insert_WithEntryType_RoundTripsCorrectly()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id = store.Insert(
                Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("typed content", EntryType: EntryType.Preference));

            var entry = store.Get(Tier.Hot, ContentType.Memory, id);
            Assert.NotNull(entry);
            Assert.True(entry!.EntryType.IsPreference);
        }
    }

    [Fact]
    public void Insert_WithoutEntryType_DefaultsToPreference()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id = store.Insert(
                Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("untyped content"));

            var entry = store.Get(Tier.Hot, ContentType.Memory, id);
            Assert.NotNull(entry);
            Assert.True(entry!.EntryType.IsPreference);
        }
    }

    [Fact]
    public void Insert_WithNonDefaultEntryType_RoundTripsCorrectly()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            var id = store.Insert(
                Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("corrective content", EntryType: EntryType.Correction));

            var entry = store.Get(Tier.Hot, ContentType.Memory, id);
            Assert.NotNull(entry);
            Assert.True(entry!.EntryType.IsCorrection);
        }
    }

    [Fact]
    public void List_WithScopeFilter_ReturnsOnlyMatchingEntries()
    {
        var (conn, store) = NewStore();
        using (conn)
        {
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("alice entry", Scope: "user:alice"));
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("bob entry", Scope: "user:bob"));
            store.Insert(Tier.Hot, ContentType.Memory,
                new InsertEntryOpts("shared entry", Scope: "global:shared"));

            var alice = store.List(Tier.Hot, ContentType.Memory,
                new ListEntriesOpts { Scopes = new[] { "user:alice" } });
            Assert.Single(alice);
            Assert.Equal("alice entry", alice[0].Content);

            var multi = store.List(Tier.Hot, ContentType.Memory,
                new ListEntriesOpts { Scopes = new[] { "user:alice", "global:shared" } });
            Assert.Equal(2, multi.Count);
        }
    }
}
