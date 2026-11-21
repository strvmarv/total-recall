using System;
using System.Collections.Generic;
using System.Linq;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// <see cref="ITranscriptStore.ApplyIngest"/> (spec sections 7 step 6 and 8): the transactional checks, the
/// upsert by (session, unit, part) and the removal of vanished parts. Real file-backed store.
/// </summary>
public sealed class TranscriptApplyIngestTests : IDisposable
{
    private const string Host = "claude-code";
    private const string Id = "sess-1";

    private readonly TranscriptTestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ITranscriptStore Store => _db.Store;

    private static IngestChunk Chunk(int unit, int part, string content, string? ts = "2026-01-01T00:00:00.0000000Z") =>
        new(unit, unit, part, "dialogue", false, ts, 1, content);

    private static SessionIngest Ingest(
        IReadOnlyList<IngestChunk> chunks,
        SessionIngestState? expectedPrior = null,
        int firstRebuiltUnit = 0,
        string host = Host,
        string id = Id,
        string projectKey = "owner/repo",
        string? projectRoot = "/w/repo",
        string? cwd = "/w/repo/sub",
        string? builtUnderCutoff = null,
        int? exchangeCount = null,
        string? title = "A title",
        string? startedAt = "2026-01-01T00:00:00.0000000Z",
        string? endedAt = "2026-01-01T01:00:00.0000000Z",
        long offset = 10) =>
        new(host, id, projectKey, "remote", projectRoot, cwd, startedAt, endedAt, "interactive", title,
            "/logs/" + id + ".jsonl", 4096, 1700000000000, "fp-1", offset,
            exchangeCount ?? (chunks.Count == 0 ? 0 : chunks.Max(c => c.ExchangeEnd) + 1), 2, builtUnderCutoff,
            expectedPrior, firstRebuiltUnit, chunks);

    private static List<IngestChunk> Units(int count, string word = "alpha") =>
        Enumerable.Range(0, count).Select(u => Chunk(u, 0, $"{word}{u} unit text")).ToList();

    private long Hits(string term) =>
        _db.Scalar($"SELECT count(*) FROM tx_chunk_fts WHERE tx_chunk_fts MATCH '{term}'");

    [Fact]
    public void Fresh_WritesSessionThenChunks_CreatedTrue()
    {
        var ingest = Ingest(Units(3));

        var outcome = Store.ApplyIngest(ingest);

        Assert.Equal(new IngestOutcome(IngestOutcomeKind.Applied, ChunksNew: 3, Created: true), outcome);
        var state = Store.GetIngestState(Host, Id);
        Assert.Equal(
            new SessionIngestState(4096, 1700000000000, "fp-1", 10, 3, 2, "interactive", "owner/repo", "remote",
                "/w/repo", "/w/repo/sub", "A title", "2026-01-01T00:00:00.0000000Z", "2026-01-01T01:00:00.0000000Z",
                null, "/logs/sess-1.jsonl"),
            state);
        Assert.Equal(3, _db.Scalar("SELECT count(*) FROM tx_chunk"));
        Assert.Equal(1, Hits("alpha1"));
        Assert.Equal(1, _db.Scalar("SELECT count(*) FROM tx_chunk WHERE exchange_start = 2 AND exchange_end = 2 AND part = 0 " +
            "AND chunk_type = 'dialogue' AND has_error = 0 AND redaction_version = 1 AND embedded = 0 AND ts = '2026-01-01T00:00:00.0000000Z'"));
    }

    [Fact]
    public void SameIngestAgain_LeavesIdsAndEmbeddedUntouched()
    {
        var chunks = Units(3);
        Store.ApplyIngest(Ingest(chunks));
        _db.Exec("UPDATE tx_chunk SET embedded = 1");
        var idText = _db.Text("SELECT group_concat(id) FROM (SELECT id FROM tx_chunk ORDER BY id)");

        var outcome = Store.ApplyIngest(Ingest(chunks, expectedPrior: Store.GetIngestState(Host, Id)));

        Assert.Equal(new IngestOutcome(IngestOutcomeKind.Applied, ChunksUnchanged: 3), outcome);
        Assert.Equal(idText, _db.Text("SELECT group_concat(id) FROM (SELECT id FROM tx_chunk ORDER BY id)"));
        Assert.Equal(3, _db.Scalar("SELECT count(*) FROM tx_chunk WHERE embedded = 1"));
    }

    [Fact]
    public void ChangedChunk_UpdatedInPlace_EmbeddedResetToZero_FtsReindexed()
    {
        var chunks = Units(2);
        Store.ApplyIngest(Ingest(chunks));
        _db.Exec("UPDATE tx_chunk SET embedded = 1");
        var idOfUnit1 = _db.Scalar("SELECT id FROM tx_chunk WHERE exchange_start = 1");
        var changed = new List<IngestChunk> { chunks[0], Chunk(1, 0, "quokka1 replaced text") };

        var outcome = Store.ApplyIngest(Ingest(changed, expectedPrior: Store.GetIngestState(Host, Id)));

        Assert.Equal(new IngestOutcome(IngestOutcomeKind.Applied, ChunksChanged: 1, ChunksUnchanged: 1), outcome);
        Assert.Equal(idOfUnit1, _db.Scalar("SELECT id FROM tx_chunk WHERE exchange_start = 1"));
        Assert.Equal(0, _db.Scalar("SELECT embedded FROM tx_chunk WHERE exchange_start = 1"));
        Assert.Equal(1, _db.Scalar("SELECT embedded FROM tx_chunk WHERE exchange_start = 0"));
        Assert.Equal(1, Hits("quokka1"));
        Assert.Equal(0, Hits("alpha1"));
    }

    [Fact]
    public void RebuiltRange_RemovesVanishedParts_AndKeepsEarlierUnits()
    {
        var first = Units(3);
        first.Add(Chunk(3, 0, "tail3a part"));
        first.Add(Chunk(3, 1, "tail3b part"));
        first.Add(Chunk(3, 2, "tail3c part"));
        Store.ApplyIngest(Ingest(first));
        var earlierIds = _db.Text("SELECT group_concat(id) FROM (SELECT id FROM tx_chunk WHERE exchange_start < 3 ORDER BY id)");

        var rebuilt = new List<IngestChunk> { Chunk(3, 0, "tail3a part"), Chunk(3, 1, "tail3b part") };
        var outcome = Store.ApplyIngest(Ingest(rebuilt, expectedPrior: Store.GetIngestState(Host, Id), firstRebuiltUnit: 3));

        Assert.Equal(new IngestOutcome(IngestOutcomeKind.Applied, ChunksUnchanged: 2, ChunksRemoved: 1), outcome);
        Assert.Equal(5, _db.Scalar("SELECT count(*) FROM tx_chunk"));
        Assert.Equal(earlierIds, _db.Text("SELECT group_concat(id) FROM (SELECT id FROM tx_chunk WHERE exchange_start < 3 ORDER BY id)"));
        Assert.Equal(0, Hits("tail3c"));
    }

    [Fact]
    public void FirstRebuiltUnitZero_ReplacesEveryChunk()
    {
        Store.ApplyIngest(Ingest(Units(4)));

        var outcome = Store.ApplyIngest(Ingest(
            new List<IngestChunk> { Chunk(0, 0, "fresh0 text") },
            expectedPrior: Store.GetIngestState(Host, Id), firstRebuiltUnit: 0));

        Assert.Equal(new IngestOutcome(IngestOutcomeKind.Applied, ChunksChanged: 1, ChunksRemoved: 3), outcome);
        Assert.Equal(1, _db.Scalar("SELECT count(*) FROM tx_chunk"));
        Assert.Equal(1, Hits("fresh0"));
        Assert.Equal(0, Hits("alpha2"));
    }

    [Fact]
    public void EmptyIngest_DeletesChunks_AndResetsRow()
    {
        Store.ApplyIngest(Ingest(Units(3)));

        var outcome = Store.ApplyIngest(Ingest(
            new List<IngestChunk>(), expectedPrior: Store.GetIngestState(Host, Id),
            title: null, startedAt: null, endedAt: null, offset: 0, exchangeCount: 0));

        Assert.Equal(new IngestOutcome(IngestOutcomeKind.Applied, ChunksRemoved: 3), outcome);
        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_chunk"));
        var state = Store.GetIngestState(Host, Id);
        Assert.NotNull(state);
        Assert.Equal(0, state!.IngestedOffset);
        Assert.Equal(0, state.ExchangeCount);
        Assert.Null(state.Title);
        Assert.Null(state.StartedAt);
        Assert.Null(state.EndedAt);
        Assert.Equal("interactive", state.Kind);
    }

    [Fact]
    public void ForgottenSessionSinceRead_IsExcluded_WritesNothing()
    {
        Store.ForgetSession(Host, Id);

        var outcome = Store.ApplyIngest(Ingest(Units(2)));

        Assert.Equal(new IngestOutcome(IngestOutcomeKind.Excluded), outcome);
        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_session"));
        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_chunk"));
    }

    [Fact]
    public void ForgottenProjectByKey_IsExcluded()
    {
        Store.ForgetProject("owner/repo");

        var outcome = Store.ApplyIngest(Ingest(Units(2)));

        Assert.Equal(IngestOutcomeKind.Excluded, outcome.Kind);
        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_session"));
    }

    [Fact]
    public void CwdUnderForgottenRoot_IsExcluded()
    {
        _db.AddSession("pi", "older", projectKey: "gone/key", cwd: "/w/repo", projectRoot: "/w/repo");
        Store.ForgetProject("gone/key");

        var outcome = Store.ApplyIngest(Ingest(Units(2), projectKey: "other/key", cwd: "/w/repo/deep/dir"));

        Assert.Equal(IngestOutcomeKind.Excluded, outcome.Kind);
        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_session"));
    }

    [Fact]
    public void CutoffChangedSinceBuild_IsStale_WritesNothing()
    {
        Store.ForgetAll(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));

        var outcome = Store.ApplyIngest(Ingest(Units(2), builtUnderCutoff: null));

        Assert.Equal(new IngestOutcome(IngestOutcomeKind.Stale), outcome);
        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_session"));
        Assert.Equal(0, _db.Scalar("SELECT count(*) FROM tx_chunk"));
    }

    [Fact]
    public void CutoffEqualRaw_IsApplied()
    {
        Store.ForgetAll(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
        var raw = Store.IngestNotBefore()!.Raw;

        var outcome = Store.ApplyIngest(Ingest(Units(2), builtUnderCutoff: raw));

        Assert.Equal(IngestOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(raw, Store.GetIngestState(Host, Id)!.BuiltUnderCutoff);
    }

    [Fact]
    public void PriorStateChanged_IsConflict()
    {
        Store.ApplyIngest(Ingest(Units(2)));
        var read = Store.GetIngestState(Host, Id);
        // another pass commits between the read and this apply
        Assert.Equal(IngestOutcomeKind.Applied,
            Store.ApplyIngest(Ingest(Units(3), expectedPrior: read, offset: 20)).Kind);

        var outcome = Store.ApplyIngest(Ingest(Units(4, "beta"), expectedPrior: read, offset: 30));

        Assert.Equal(new IngestOutcome(IngestOutcomeKind.Conflict), outcome);
        Assert.Equal(3, _db.Scalar("SELECT count(*) FROM tx_chunk"));
        Assert.Equal(20, Store.GetIngestState(Host, Id)!.IngestedOffset);
        Assert.Equal(0, Hits("beta0"));
    }

    [Fact]
    public void FreshButRowAppeared_IsConflict()
    {
        Store.ApplyIngest(Ingest(Units(2)));

        var outcome = Store.ApplyIngest(Ingest(Units(3, "beta"), expectedPrior: null));

        Assert.Equal(new IngestOutcome(IngestOutcomeKind.Conflict), outcome);
        Assert.Equal(2, _db.Scalar("SELECT count(*) FROM tx_chunk"));
        Assert.Equal(0, Hits("beta0"));
    }

    [Fact]
    public void QuotesAndPercentInData_AreStoredVerbatim()
    {
        const string content = "it's \"quoted\" 100% done; DROP TABLE tx_session; -- %_ \\";
        const string title = "O'Brien \"title\" 50%";

        var outcome = Store.ApplyIngest(Ingest(
            new List<IngestChunk> { Chunk(0, 0, content) }, title: title, cwd: "/w/it's 100%/x", projectRoot: "/w/it's 100%"));

        Assert.Equal(IngestOutcomeKind.Applied, outcome.Kind);
        Assert.Equal(content, _db.Text("SELECT content FROM tx_chunk"));
        var state = Store.GetIngestState(Host, Id)!;
        Assert.Equal(title, state.Title);
        Assert.Equal("/w/it's 100%/x", state.Cwd);
        Assert.Equal("/w/it's 100%", state.ProjectRoot);
    }
}
