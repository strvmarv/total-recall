using System;
using System.Collections.Generic;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>What the ranking needs to know about a candidate chunk.</summary>
public sealed record RankInfo(long SessionId, string ChunkType, bool HasError, string? Ts);

/// <summary>One ranked chunk: its relative score (the best is 1.0) and the candidate lists it came from.</summary>
public sealed record RankedHit(long ChunkId, double Score, IReadOnlyList<string> Via);

/// <summary>
/// The archive search's own ranking (search the archive, spec section 5, "Merge and ranking"): reciprocal rank fusion
/// over the meaning, phrase and words lists, three multipliers (all initial and untuned), a stable order and at most two
/// chunks per session. Pure; scores are used relatively, so no absolute threshold exists.
/// </summary>
public static class RankFusion
{
    /// <summary>The reciprocal-rank-fusion constant.</summary>
    public const int RrfK = 60;

    /// <summary>Multiplier of a compaction summary.</summary>
    public const double SummaryBoost = 1.25;

    /// <summary>Multiplier of a chunk that holds an error, for a query that looks like an error string.</summary>
    public const double ErrorBoost = 1.20;

    /// <summary>The recency boost is <c>1 + RecencyWeight * 2^(-age_days / RecencyHalfLifeDays)</c>.</summary>
    public const double RecencyWeight = 0.10;

    public const double RecencyHalfLifeDays = 180;

    /// <summary>The most chunks of one session among the results, unless the cap is off.</summary>
    public const int MaxPerSession = 2;

    /// <param name="vector">Chunk ids by similarity, best first.</param>
    /// <param name="phrase">Chunk ids of the phrase hits by bm25, best first.</param>
    /// <param name="words">Chunk ids of the word hits by bm25, best first.</param>
    /// <param name="info">Session, type, error flag and time of every id in the lists; an id with no entry is dropped.</param>
    /// <param name="applySessionCap">False for a session-scope search.</param>
    public static IReadOnlyList<RankedHit> Rank(
        IReadOnlyList<long> vector,
        IReadOnlyList<long> phrase,
        IReadOnlyList<long> words,
        IReadOnlyDictionary<long, RankInfo> info,
        DateTimeOffset now,
        bool errorQuery,
        int topK,
        bool applySessionCap)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(phrase);
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(info);
        if (topK <= 0)
            return Array.Empty<RankedHit>();

        var candidates = new Dictionary<long, Candidate>();
        Accumulate(candidates, info, vector, "meaning");
        Accumulate(candidates, info, phrase, "phrase");
        Accumulate(candidates, info, words, "words");
        if (candidates.Count == 0)
            return Array.Empty<RankedHit>();

        var scored = new List<Scored>(candidates.Count);
        foreach (var (id, c) in candidates)
        {
            var i = c.Info;
            var score = c.Fused;
            if (string.Equals(i.ChunkType, "compaction-summary", StringComparison.Ordinal))
                score *= SummaryBoost;
            if (errorQuery && i.HasError)
                score *= ErrorBoost;

            DateTimeOffset? at = null;
            if (TranscriptTime.TryParse(i.Ts, out var parsed))
            {
                at = parsed;
                var ageDays = Math.Max(0.0, (now - parsed).TotalDays);
                score *= 1 + RecencyWeight * Math.Pow(2, -ageDays / RecencyHalfLifeDays);
            }
            scored.Add(new Scored(id, score, at, i.SessionId, c.Via));
        }

        scored.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            // newer first; an unknown time sorts after a known one
            if (a.At != b.At)
            {
                if (a.At is null) return 1;
                if (b.At is null) return -1;
                return b.At.Value.CompareTo(a.At.Value);
            }
            return a.Id.CompareTo(b.Id);
        });

        var best = scored[0].Score;
        var perSession = new Dictionary<long, int>();
        var result = new List<RankedHit>(Math.Min(topK, scored.Count));
        foreach (var s in scored)
        {
            if (result.Count >= topK)
                break;
            if (applySessionCap)
            {
                perSession.TryGetValue(s.SessionId, out var taken);
                if (taken >= MaxPerSession)
                    continue;
                perSession[s.SessionId] = taken + 1;
            }
            result.Add(new RankedHit(s.Id, best > 0 ? s.Score / best : 1.0, s.Via));
        }
        return result;
    }

    private static void Accumulate(
        Dictionary<long, Candidate> candidates, IReadOnlyDictionary<long, RankInfo> info, IReadOnlyList<long> list, string name)
    {
        var seenInList = new HashSet<long>();
        for (var position = 0; position < list.Count; position++)
        {
            var id = list[position];
            if (!seenInList.Add(id) || !info.TryGetValue(id, out var chunk))
                continue;
            if (!candidates.TryGetValue(id, out var c))
            {
                c = new Candidate(chunk);
                candidates[id] = c;
            }
            c.Fused += 1.0 / (RrfK + position + 1);
            c.Via.Add(name);
        }
    }

    private sealed class Candidate
    {
        public Candidate(RankInfo info) => Info = info;
        public RankInfo Info { get; }
        public double Fused { get; set; }
        public List<string> Via { get; } = new();
    }

    private sealed record Scored(long Id, double Score, DateTimeOffset? At, long SessionId, IReadOnlyList<string> Via);
}
