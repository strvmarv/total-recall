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
}
