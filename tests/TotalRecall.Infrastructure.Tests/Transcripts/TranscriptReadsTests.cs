using System;
using System.Linq;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.TestSupport.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// <see cref="ITranscriptReads"/> (read spec section 1): sessions, chunks, stored exchange numbers and the session list,
/// all inside one deferred read transaction that writes nothing. Real file-backed store, seeded through
/// <see cref="ReadArchiveBuilder"/>.
/// </summary>
public sealed class TranscriptReadsTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TranscriptTestDb _db = new();
    private readonly ReadArchiveBuilder _archive;

    public TranscriptReadsTests() => _archive = new ReadArchiveBuilder(_db.Store);

    public void Dispose() => _db.Dispose();

    private static SessionFilter All(bool includeAutomated = true) => new(null, null, null, includeAutomated);

    private long IdOf(string hostSessionId) =>
        _db.Scalar($"SELECT id FROM tx_session WHERE host_session_id = '{hostSessionId}'");

    private static string[] Refs(SessionList list) => list.Rows.Select(r => r.Ref).ToArray();

    [Fact]
    public void AnyChunk_EmptyAndPopulated()
    {
        using (var reads = _db.Store.BeginReads())
            Assert.False(reads.AnyChunk());

        _archive.Session("claude-code", "s1").Exchange(0, "hello").Commit();

        using var again = _db.Store.BeginReads();
        Assert.True(again.AnyChunk());
    }

    [Fact]
    public void SessionByRef_And_ById_ReturnFields_IncludingChunkCount()
    {
        _archive.Session("claude-code", "sess-abcdef-123", project: "acme/widgets", kind: "automated", title: "Fix the build",
                startedAt: T0, endedAt: T0.AddHours(1))
            .Exchange(0, "a").Parts(1, "b0", "b1", "b2").Exchange(3, "d")
            .Commit();
        var id = IdOf("sess-abcdef-123");
        var expected = new SessionRow(id, "claude-code:sess-abcdef-123", "acme/widgets", "automated", "Fix the build",
            ReadArchiveBuilder.Iso(T0), ReadArchiveBuilder.Iso(T0.AddHours(1)), 4, 5);

        using var reads = _db.Store.BeginReads();
        Assert.Equal(expected, reads.SessionByRef("claude-code", "sess-abcdef-123"));
        Assert.Equal(expected, reads.SessionById(id));
    }

    [Fact]
    public void SessionByRef_Unknown_Null()
    {
        _archive.Session("claude-code", "s1").Exchange(0, "a").Commit();

        using var reads = _db.Store.BeginReads();
        Assert.Null(reads.SessionByRef("claude-code", "nope"));
        Assert.Null(reads.SessionByRef("pi", "s1"));
        Assert.Null(reads.SessionById(IdOf("s1") + 1000));
    }

    [Fact]
    public void ChunkById_ReturnsRowWithSession_UnknownNull()
    {
        _archive.Session("pi", "s1", project: "acme/widgets", title: "T")
            .Exchange(2, "the body", ts: T0, chunkType: "compaction-summary", hasError: true)
            .Commit();
        var chunkId = _db.Scalar("SELECT id FROM tx_chunk");

        using var reads = _db.Store.BeginReads();
        var row = reads.ChunkById(chunkId);
        Assert.NotNull(row);
        Assert.Equal(chunkId, row.Id);
        Assert.Equal(IdOf("s1"), row.SessionId);
        Assert.Equal("pi:s1", row.SessionRef);
        Assert.Equal("acme/widgets", row.Project);
        Assert.Equal("interactive", row.Kind);
        Assert.Equal("T", row.Title);
        Assert.Equal(ReadArchiveBuilder.Iso(T0), row.Ts);
        Assert.Equal("compaction-summary", row.ChunkType);
        Assert.True(row.HasError);
        Assert.Equal(2, row.ExchangeStart);
        Assert.Equal(2, row.ExchangeEnd);
        Assert.Equal("pi session s1 | project acme/widgets | 2026-03-01 | interactive | compaction-summary | exchanges 2-2\n\nthe body",
            row.Content);
        Assert.Null(reads.ChunkById(chunkId + 1000));
    }

    [Fact]
    public void ChunksOfExchange_OrderedByPart_AllParts()
    {
        _archive.Session("claude-code", "s1")
            .Exchange(0, "zero")
            .Parts(1, "first", "User (continued): q\n\nsecond", "User (continued): q\n\nthird")
            .Exchange(2, "two")
            .Commit();
        var session = IdOf("s1");

        using var reads = _db.Store.BeginReads();
        var chunks = reads.ChunksOfExchange(session, 1);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(1, c.ExchangeStart));
        Assert.EndsWith("\n\nfirst", chunks[0].Content);
        Assert.EndsWith("\n\nsecond", chunks[1].Content);
        Assert.EndsWith("\n\nthird", chunks[2].Content);
        Assert.Empty(reads.ChunksOfExchange(session, 5));
        Assert.Empty(reads.ChunksOfExchange(session + 1000, 1));
    }

    [Fact]
    public void ExchangesInRange_SkipsGaps_Ascending()
    {
        _archive.Session("claude-code", "s1")
            .Exchange(0, "a").Exchange(1, "b").Exchange(3, "d").Parts(4, "e0", "e1")
            .Commit();
        _archive.Session("claude-code", "other").Exchange(2, "x").Commit();
        var session = IdOf("s1");

        using var reads = _db.Store.BeginReads();
        Assert.Equal(new[] { 0, 1, 3, 4 }, reads.ExchangesInRange(session, 0, 10));
        Assert.Equal(new[] { 1, 3 }, reads.ExchangesInRange(session, 1, 3));
        Assert.Empty(reads.ExchangesInRange(session, 2, 2));
        Assert.Equal(new[] { 4 }, reads.ExchangesInRange(session, 4, 4));
        Assert.Empty(reads.ExchangesInRange(session, 5, 9));
    }

    [Fact]
    public void ExchangesBefore_After_NearestFirst_StopAtEnds_Zero()
    {
        _archive.Session("claude-code", "s1")
            .Exchange(0, "a").Exchange(1, "b").Exchange(3, "d").Parts(4, "e0", "e1").Exchange(7, "h")
            .Commit();
        var session = IdOf("s1");

        using var reads = _db.Store.BeginReads();
        Assert.Equal(new[] { 3, 1 }, reads.ExchangesBefore(session, 4, 2));
        Assert.Equal(new[] { 1, 0 }, reads.ExchangesBefore(session, 3, 5));
        Assert.Empty(reads.ExchangesBefore(session, 0, 3));
        Assert.Empty(reads.ExchangesBefore(session, 4, 0));
        Assert.Empty(reads.ExchangesBefore(session, 4, -1));

        Assert.Equal(new[] { 3, 4 }, reads.ExchangesAfter(session, 1, 2));
        Assert.Equal(new[] { 4, 7 }, reads.ExchangesAfter(session, 3, 9));
        Assert.Empty(reads.ExchangesAfter(session, 7, 3));
        Assert.Empty(reads.ExchangesAfter(session, 1, 0));
        Assert.Empty(reads.ExchangesAfter(session, 1, -1));
    }

    [Fact]
    public void ListSessions_NewestFirst_NoTimesLast_TiesById()
    {
        _archive.Session("claude-code", "notime-a").Exchange(0, "x").Commit();
        _archive.Session("claude-code", "old", startedAt: T0, endedAt: T0.AddHours(1)).Exchange(0, "x").Commit();
        _archive.Session("claude-code", "started-only", startedAt: T0.AddHours(3)).Exchange(0, "x").Commit();
        _archive.Session("claude-code", "tie-a", startedAt: T0, endedAt: T0.AddHours(2)).Exchange(0, "x").Commit();
        _archive.Session("claude-code", "tie-b", startedAt: T0.AddMinutes(5), endedAt: T0.AddHours(2)).Exchange(0, "x").Commit();
        _archive.Session("claude-code", "notime-b").Exchange(0, "x").Commit();

        using var reads = _db.Store.BeginReads();
        var list = reads.ListSessions(All(), 20);

        Assert.Equal(
            new[]
            {
                "claude-code:started-only", "claude-code:tie-b", "claude-code:tie-a", "claude-code:old",
                "claude-code:notime-b", "claude-code:notime-a",
            },
            Refs(list));
        Assert.Equal(6, list.Total);
        Assert.Equal(0, list.AutomatedHidden);
        Assert.All(list.Rows, r => Assert.Equal(1, r.ChunkCount));
    }

    [Fact]
    public void ListSessions_ProjectFilter()
    {
        _archive.Session("claude-code", "a", project: "acme/one", startedAt: T0).Exchange(0, "x").Commit();
        _archive.Session("claude-code", "b", project: "acme/two", startedAt: T0.AddHours(1)).Exchange(0, "x").Commit();
        _archive.Session("claude-code", "c", project: "acme/one", startedAt: T0.AddHours(2)).Exchange(0, "x").Commit();
        _archive.Session("claude-code", "d", project: "ACME/one", startedAt: T0.AddHours(3)).Exchange(0, "x").Commit();

        using var reads = _db.Store.BeginReads();
        var list = reads.ListSessions(new SessionFilter("acme/one", null, null, true), 20);
        Assert.Equal(new[] { "claude-code:c", "claude-code:a" }, Refs(list));
        Assert.Equal(2, list.Total);
        Assert.Empty(reads.ListSessions(new SessionFilter("nope/none", null, null, true), 20).Rows);
    }

    [Fact]
    public void ListSessions_Since_Inclusive_Before_Strict_FullPrecision()
    {
        var earlier = T0.AddTicks(1234567);
        var later = earlier.AddTicks(1); // 100 ns apart
        _archive.Session("claude-code", "earlier", startedAt: T0, endedAt: earlier).Exchange(0, "x").Commit();
        _archive.Session("claude-code", "later", startedAt: T0, endedAt: later).Exchange(0, "x").Commit();

        using var reads = _db.Store.BeginReads();
        Assert.Equal(new[] { "claude-code:earlier" }, Refs(reads.ListSessions(new SessionFilter(null, null, later, true), 20)));
        Assert.Equal(new[] { "claude-code:later" }, Refs(reads.ListSessions(new SessionFilter(null, later, null, true), 20)));
        Assert.Equal(new[] { "claude-code:later", "claude-code:earlier" },
            Refs(reads.ListSessions(new SessionFilter(null, earlier, null, true), 20)));
        Assert.Empty(reads.ListSessions(new SessionFilter(null, null, earlier, true), 20).Rows);
        Assert.Equal(new[] { "claude-code:earlier" },
            Refs(reads.ListSessions(new SessionFilter(null, earlier, later, true), 20)));

        // a bound given in another offset is compared in UTC
        var laterElsewhere = later.ToOffset(TimeSpan.FromHours(-5));
        Assert.Equal(new[] { "claude-code:earlier" },
            Refs(reads.ListSessions(new SessionFilter(null, null, laterElsewhere, true), 20)));
    }

    [Fact]
    public void ListSessions_NoTimeSession_MatchesNeitherBound()
    {
        _archive.Session("claude-code", "notime").Exchange(0, "x").Commit();
        _archive.Session("claude-code", "timed", endedAt: T0).Exchange(0, "x").Commit();

        using var reads = _db.Store.BeginReads();
        Assert.Equal(new[] { "claude-code:timed", "claude-code:notime" }, Refs(reads.ListSessions(All(), 20)));
        var since = reads.ListSessions(new SessionFilter(null, T0.AddYears(-10), null, true), 20);
        Assert.Equal(new[] { "claude-code:timed" }, Refs(since));
        Assert.Equal(1, since.Total);
        var before = reads.ListSessions(new SessionFilter(null, null, T0.AddYears(10), true), 20);
        Assert.Equal(new[] { "claude-code:timed" }, Refs(before));
        Assert.Equal(1, before.Total);
    }

    [Fact]
    public void ListSessions_AutomatedHidden_Counted_OnlyWhenNotIncluded_AllFiltersApplied()
    {
        _archive.Session("claude-code", "i1", project: "acme/one", startedAt: T0.AddHours(1)).Exchange(0, "x").Commit();
        _archive.Session("claude-code", "a1", project: "acme/one", kind: "automated", startedAt: T0.AddHours(2)).Exchange(0, "x").Commit();
        _archive.Session("claude-code", "a2", project: "acme/one", kind: "automated", startedAt: T0.AddHours(3)).Exchange(0, "x").Commit();
        _archive.Session("claude-code", "a-old", project: "acme/one", kind: "automated", startedAt: T0.AddHours(-5)).Exchange(0, "x").Commit();
        _archive.Session("claude-code", "a-other", project: "acme/two", kind: "automated", startedAt: T0.AddHours(4)).Exchange(0, "x").Commit();
        _archive.Session("claude-code", "a-reset", project: "acme/one", kind: "automated", startedAt: T0.AddHours(5)).Reset().Commit();

        using var reads = _db.Store.BeginReads();
        var hidden = reads.ListSessions(new SessionFilter("acme/one", T0, null, false), 20);
        Assert.Equal(new[] { "claude-code:i1" }, Refs(hidden));
        Assert.Equal(1, hidden.Total);
        Assert.Equal(2, hidden.AutomatedHidden); // a1, a2: not a-old (since), a-other (project), a-reset (no chunk)

        var included = reads.ListSessions(new SessionFilter("acme/one", T0, null, true), 20);
        Assert.Equal(new[] { "claude-code:a2", "claude-code:a1", "claude-code:i1" }, Refs(included));
        Assert.Equal(3, included.Total);
        Assert.Equal(0, included.AutomatedHidden);

        var everything = reads.ListSessions(All(includeAutomated: false), 20);
        Assert.Equal(1, everything.Total);
        Assert.Equal(4, everything.AutomatedHidden);
    }

    [Fact]
    public void ListSessions_ResetRowWithNoChunks_NotListedNorCounted()
    {
        _archive.Session("claude-code", "kept", startedAt: T0).Exchange(0, "x").Commit();
        Assert.Equal(IngestOutcomeKind.Applied, _archive.Session("claude-code", "reset", startedAt: T0.AddHours(1)).Reset().Commit().Kind);
        Assert.Equal(0, _db.Scalar("SELECT exchange_count FROM tx_session WHERE host_session_id = 'reset'"));

        using var reads = _db.Store.BeginReads();
        var list = reads.ListSessions(All(), 20);
        Assert.Equal(new[] { "claude-code:kept" }, Refs(list));
        Assert.Equal(1, list.Total);
        // a named read still finds it, with no chunks
        Assert.Equal(0, reads.SessionByRef("claude-code", "reset")!.ChunkCount);
    }

    [Fact]
    public void ListSessions_LimitAndTotal()
    {
        for (var i = 0; i < 5; i++)
            _archive.Session("claude-code", "s" + i, startedAt: T0.AddHours(i)).Exchange(0, "x").Commit();

        using var reads = _db.Store.BeginReads();
        var two = reads.ListSessions(All(), 2);
        Assert.Equal(new[] { "claude-code:s4", "claude-code:s3" }, Refs(two));
        Assert.Equal(5, two.Total);
        var zero = reads.ListSessions(All(), 0);
        Assert.Empty(zero.Rows);
        Assert.Equal(5, zero.Total);
        Assert.Equal(5, reads.ListSessions(All(), 100).Rows.Count);
    }

    [Fact]
    public void Reads_AreOneTransaction_ACommitBetweenQueriesIsNotSeen()
    {
        _archive.Session("claude-code", "s1", startedAt: T0).Exchange(0, "a").Exchange(1, "b").Commit();
        var session = IdOf("s1");

        using (var reads = _db.Store.BeginReads())
        {
            Assert.NotNull(reads.SessionByRef("claude-code", "s1")); // the snapshot starts here

            var second = TranscriptStore.Open(new TranscriptLocation(_db.Path, IsMainDatabase: false), create: false);
            Assert.Equal(TranscriptOpenStatus.Opened, second.Status);
            using (var other = second.Store!)
                Assert.Equal(1, other.ForgetSession("claude-code", "s1").Sessions);

            Assert.NotNull(reads.SessionById(session));
            Assert.Equal(2, reads.ChunksOfExchange(session, 0).Count + reads.ChunksOfExchange(session, 1).Count);
            Assert.Equal(new[] { 0, 1 }, reads.ExchangesInRange(session, 0, 5));
            Assert.Single(reads.ListSessions(All(), 20).Rows);
            Assert.True(reads.AnyChunk());
        }

        using var fresh = _db.Store.BeginReads();
        Assert.Null(fresh.SessionByRef("claude-code", "s1"));
        Assert.Null(fresh.SessionById(session));
        Assert.Empty(fresh.ListSessions(All(), 20).Rows);
        Assert.False(fresh.AnyChunk());
    }

    [Fact]
    public void Reads_WriteNothing()
    {
        _archive.Session("claude-code", "s1", startedAt: T0).Exchange(0, "a").Parts(2, "c0", "c1").Commit();
        _archive.Session("claude-code", "s2", kind: "automated", endedAt: T0).Exchange(0, "a").Commit();
        var session = IdOf("s1");
        var chunk = _db.Scalar("SELECT min(id) FROM tx_chunk");

        string Snapshot() =>
            $"{_db.Scalar("SELECT count(*) FROM tx_session")}|{_db.Scalar("SELECT count(*) FROM tx_chunk")}|" +
            $"{_db.Text("SELECT group_concat(key || '=' || value, ';') FROM (SELECT key, value FROM tx_state ORDER BY key)")}|" +
            $"{_db.Text("SELECT group_concat(id || ':' || embedded || ':' || content, ';') FROM (SELECT * FROM tx_chunk ORDER BY id)")}";

        var before = Snapshot();
        using (var reads = _db.Store.BeginReads())
        {
            reads.AnyChunk();
            reads.SessionByRef("claude-code", "s1");
            reads.SessionById(session);
            reads.ChunkById(chunk);
            reads.ChunksOfExchange(session, 2);
            reads.ExchangesInRange(session, 0, 9);
            reads.ExchangesBefore(session, 2, 3);
            reads.ExchangesAfter(session, 0, 3);
            reads.ListSessions(All(includeAutomated: false), 20);
            reads.ListSessions(new SessionFilter("owner/repo", T0.AddDays(-1), T0.AddDays(1), true), 1);
        }

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Dispose_EndsTheTransaction_Idempotent()
    {
        _archive.Session("claude-code", "s1").Exchange(0, "a").Commit();
        var reads = _db.Store.BeginReads();
        Assert.True(reads.AnyChunk());
        reads.Dispose();
        reads.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reads.AnyChunk());
        Assert.Equal(1, _db.Store.Status().Chunks);
    }
}
