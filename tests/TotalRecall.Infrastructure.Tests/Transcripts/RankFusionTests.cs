using System;
using System.Collections.Generic;
using System.Linq;
using TotalRecall.Infrastructure.Transcripts;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

public sealed class RankFusionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private const string Old = "1980-01-01T00:00:00Z"; // far past: 2^(-age/180) underflows the boost to exactly 1
    private static readonly long[] None = Array.Empty<long>();

    private static RankInfo Info(long session = 1, string type = "dialogue", bool error = false, string? ts = Old) =>
        new(session, type, error, ts);

    private static Dictionary<long, RankInfo> Infos(params (long Id, RankInfo Info)[] items) =>
        items.ToDictionary(i => i.Id, i => i.Info);

    private static IReadOnlyList<RankedHit> Rank(
        long[] v, long[] p, long[] w, Dictionary<long, RankInfo> info,
        bool errorQuery = false, int topK = 10, bool cap = true, DateTimeOffset? now = null) =>
        RankFusion.Rank(v, p, w, info, now ?? Now, errorQuery, topK, cap);

    [Fact]
    public void Rank_EmptyLists_Empty() =>
        Assert.Empty(Rank(None, None, None, new Dictionary<long, RankInfo>()));

    [Fact]
    public void Rank_ChunkInTwoLists_BeatsChunkInOne()
    {
        var info = Infos((1, Info(1)), (2, Info(2)), (3, Info(3)));
        // 1 is first in V only; 2 is second in V and second in W; 3 is first in W only.
        var hits = Rank(new long[] { 1, 2 }, None, new long[] { 3, 2 }, info);
        Assert.Equal(2, hits[0].ChunkId);
        Assert.Equal(new[] { "meaning", "words" }, hits[0].Via);
    }

    [Fact]
    public void Rank_PhraseAndWords_BothCount()
    {
        var info = Infos((1, Info(1)), (2, Info(2)));
        var hits = Rank(None, new long[] { 1, 2 }, new long[] { 2, 1 }, info);
        // equal fused scores (1/61 + 1/62 each) -> tie-break lower id; both lists counted
        Assert.Equal(1.0, hits[0].Score);
        Assert.Equal(1.0, hits[1].Score);
        Assert.Equal(new[] { "phrase", "words" }, hits[0].Via);

        var single = Rank(None, new long[] { 1 }, None, Infos((1, Info(1))));
        Assert.Equal(new[] { "phrase" }, single[0].Via);

        var triple = Rank(new long[] { 5 }, new long[] { 5 }, new long[] { 5 }, Infos((5, Info(1))));
        Assert.Equal(new[] { "meaning", "phrase", "words" }, triple[0].Via);
    }

    [Fact]
    public void Rank_CompactionSummary_Boosted1_25()
    {
        var info = Infos((1, Info(1)), (2, Info(2, type: "compaction-summary")));
        var hits = Rank(new long[] { 1, 2 }, None, None, info);
        // 2: (1/62) * 1.25 = 0.020161 beats 1: 1/61 = 0.016393
        Assert.Equal(2, hits[0].ChunkId);
        Assert.Equal(1.0, hits[0].Score);
        Assert.Equal((1.0 / 61) / ((1.0 / 62) * 1.25), hits[1].Score, 9);
    }

    [Fact]
    public void Rank_ErrorChunk_BoostedOnlyForErrorQuery()
    {
        var info = Infos((1, Info(1)), (2, Info(2, error: true)));
        var plain = Rank(new long[] { 1, 2 }, None, None, info, errorQuery: false);
        Assert.Equal(1, plain[0].ChunkId);

        var err = Rank(new long[] { 1, 2 }, None, None, info, errorQuery: true);
        Assert.Equal(2, err[0].ChunkId); // (1/62)*1.2 > 1/61
        Assert.Equal((1.0 / 61) / ((1.0 / 62) * 1.2), err[1].Score, 9);
    }

    [Fact]
    public void Rank_RecencyBoost_NewerWinsNearTie_NoBoostForUnknownTs()
    {
        var recent = Now.AddDays(-1).ToString("o");
        var older = Now.AddDays(-400).ToString("o");
        // 1 ranks first but is old; 2 ranks second and is recent. 1: (1/61) * 1.0214 = 0.01674; 2: (1/62) * 1.0996 =
        // 0.01774, so the recent chunk overtakes the one ranked above it.
        var info = Infos((1, Info(1, ts: older)), (2, Info(2, ts: recent)));
        var hits = Rank(new long[] { 1, 2 }, None, None, info);
        Assert.Equal(new long[] { 2, 1 }, hits.Select(h => h.ChunkId).ToArray());
        Assert.Equal(1.0, hits[0].Score);
        Assert.Equal(0.9441, hits[1].Score, 4);

        // Same rank (two lists put each first): the newer chunk wins the near tie.
        var info2 = Infos((1, Info(1, ts: older)), (2, Info(2, ts: recent)));
        var tie = Rank(new long[] { 1 }, new long[] { 2 }, None, info2);
        Assert.Equal(2, tie[0].ChunkId);

        // Unknown time: no boost, no penalty (equal to a far-past chunk); in the tie-break it counts as the oldest.
        var info3 = Infos((1, Info(1, ts: null)), (2, Info(2, ts: Old)));
        var unknown = Rank(new long[] { 2 }, new long[] { 1 }, None, info3);
        Assert.Equal(new long[] { 2, 1 }, unknown.Select(h => h.ChunkId).ToArray());
        Assert.Equal(1.0, unknown[1].Score, 12);

        var unparseable = Rank(new long[] { 1 }, None, None, Infos((1, Info(1, ts: "not a time"))));
        Assert.Equal(1.0, unparseable[0].Score);
    }

    [Fact]
    public void Rank_FutureTs_DoesNotBoostBeyondTen()
    {
        var info = Infos((1, Info(1, ts: Now.AddDays(30).ToString("o"))), (2, Info(2, ts: Now.ToString("o"))));
        var hits = Rank(new long[] { 1 }, new long[] { 2 }, None, info);
        Assert.Equal(1, hits[0].ChunkId); // equal boost (age clamped to 0), tie broken by newer ts
        Assert.Equal(1.0, hits[1].Score, 12);
    }

    [Fact]
    public void Rank_SessionCap_TwoPerSession_SkipsToNextSession()
    {
        var info = Infos((1, Info(1)), (2, Info(1)), (3, Info(1)), (4, Info(2)), (5, Info(1)));
        var hits = Rank(new long[] { 1, 2, 3, 4, 5 }, None, None, info, topK: 4);
        Assert.Equal(new long[] { 1, 2, 4 }, hits.Select(h => h.ChunkId).ToArray());
    }

    [Fact]
    public void Rank_SessionScope_NoCap()
    {
        var info = Infos((1, Info(1)), (2, Info(1)), (3, Info(1)), (4, Info(1)));
        var hits = Rank(new long[] { 1, 2, 3, 4 }, None, None, info, topK: 3, cap: false);
        Assert.Equal(new long[] { 1, 2, 3 }, hits.Select(h => h.ChunkId).ToArray());
    }

    [Fact]
    public void Rank_TopScoreIsOne()
    {
        var info = Infos((1, Info(1, type: "compaction-summary")), (2, Info(2)), (3, Info(3, error: true)));
        var hits = Rank(new long[] { 2, 3, 1 }, new long[] { 1 }, None, info, errorQuery: true);
        Assert.Equal(1.0, hits[0].Score);
        Assert.All(hits, h => Assert.InRange(h.Score, 0.0, 1.0));
        Assert.Equal(hits.OrderByDescending(h => h.Score).Select(h => h.ChunkId), hits.Select(h => h.ChunkId));
    }

    [Fact]
    public void Rank_TopScoreIsOne_WhenTopKCutsTheRest()
    {
        var info = Infos((1, Info(1)), (2, Info(2)));
        var hits = Rank(new long[] { 1, 2 }, None, None, info, topK: 1);
        Assert.Single(hits);
        Assert.Equal(1.0, hits[0].Score);
    }

    [Fact]
    public void Rank_ScoresStayRelativeToTheBest_WhenTheSessionCapSkipsAChunk()
    {
        // two chunks of session 1 fill its cap ahead of a third; session 2's chunk comes in after the skipped one
        var info = Infos((1, Info(1)), (2, Info(1)), (3, Info(1)), (4, Info(2)));
        var hits = Rank(new long[] { 1, 2, 3, 4 }, None, None, info, topK: 3);
        Assert.Equal(new long[] { 1, 2, 4 }, hits.Select(h => h.ChunkId).ToArray());
        Assert.Equal(1.0, hits[0].Score);
        Assert.Equal((1.0 / 64) / (1.0 / 61), hits[2].Score, 9); // its own rank (fourth), not its place in the results
    }

    [Fact]
    public void Rank_TieBreak_NewerTsThenLowerId()
    {
        // far-past timestamps carry no boost (2^-(age/180) underflows), so the scores are exactly equal
        var info = Infos((1, Info(1, ts: "1980-01-01T00:00:00Z")), (2, Info(2, ts: "1985-01-01T00:00:00Z")),
            (3, Info(3, ts: "1985-01-01T00:00:00Z")));
        var hits = Rank(new long[] { 3 }, new long[] { 1 }, new long[] { 2 }, info);
        Assert.Equal(new long[] { 2, 3, 1 }, hits.Select(h => h.ChunkId).ToArray());
    }

    [Fact]
    public void Rank_TopKZero_Empty() =>
        Assert.Empty(Rank(new long[] { 1 }, None, None, Infos((1, Info(1))), topK: 0));

    [Fact]
    public void Rank_RepeatedIdInAList_CountsItsFirstPosition()
    {
        var hits = Rank(new long[] { 1, 1, 2 }, None, None, Infos((1, Info(1)), (2, Info(2))));
        Assert.Equal(new long[] { 1, 2 }, hits.Select(h => h.ChunkId).ToArray());
        Assert.Equal((1.0 / 63) / (1.0 / 61), hits[1].Score, 9); // the rank is the position in the list: 2 is third
    }
}
