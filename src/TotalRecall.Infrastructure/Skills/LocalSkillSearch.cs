using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.Infrastructure.Skills;

public sealed class LocalSkillSearch(ISkillCache cache, IEmbedder embedder) : ILocalSkillSearch
{
    private const double SemanticWeight = 0.65;
    private const double KeywordWeight  = 0.25;
    private const double DecayWeight    = 0.10;

    // Half-life of 30 days (720 hours) for the time component of decay.
    private const double DecayHalfLifeHours = 720.0;

    public async Task<IReadOnlyList<SkillSearchHitDto>> SearchAsync(
        string query, IReadOnlyList<string>? tags, int limit, CancellationToken ct)
    {
        var all = await cache.ListAllForSearchAsync(ct).ConfigureAwait(false);
        if (all.Count == 0) return Array.Empty<SkillSearchHitDto>();

        var filtered = tags is { Count: > 0 }
            ? all.Where(s => tags.All(t => s.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))).ToList()
            : (IReadOnlyList<CachedSkill>)all;
        if (filtered.Count == 0) return Array.Empty<SkillSearchHitDto>();

        float[]? qv = null;
        try { qv = embedder.Embed(query); }
        catch { /* fall back to keyword-only */ }

        var tokens = Tokenize(query);
        var scored = filtered.Select(s =>
        {
            var sem = (qv is not null && s.ContentEmbedding is not null)
                ? Cosine(qv, s.ContentEmbedding) : 0.0;
            var kw  = KeywordScore(s, tokens);
            var score = SemanticWeight * sem
                      + KeywordWeight * kw
                      + DecayWeight * NormalizeDecay(s.UsageCount, s.LastUsedAt);
            return new SkillSearchHitDto(
                Id: s.Id, Name: s.Name, Description: s.Description,
                Scope: s.Scope, ScopeId: s.ScopeId, Tags: s.Tags,
                Score: score, Excerpt: Excerpt(s.Content, tokens));
        })
        .OrderByDescending(h => h.Score)
        .Take(limit)
        .ToList();

        return scored;
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static string[] Tokenize(string q) =>
        q.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', ',', '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1).Distinct().ToArray();

    private static double KeywordScore(CachedSkill s, string[] tokens)
    {
        if (tokens.Length == 0) return 0;
        var hay = $"{s.Name} {s.Description} {string.Join(" ", s.Tags)} {s.Content}".ToLowerInvariant();
        int hits = tokens.Count(t => hay.Contains(t, StringComparison.Ordinal));
        return (double)hits / tokens.Length;
    }

    private static string Excerpt(string content, string[] tokens)
    {
        var body = content ?? "";
        foreach (var t in tokens)
        {
            var idx = body.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                int start = Math.Max(0, idx - 40);
                int len = Math.Min(body.Length - start, 160);
                return body.Substring(start, len).Trim();
            }
        }
        return body.Length > 160 ? body[..160] + "..." : body;
    }

    private static double NormalizeDecay(int usageCount, DateTime? lastUsedAt)
    {
        if (usageCount <= 0) return 0;
        var ageHours = lastUsedAt is null
            ? 0
            : (DateTime.UtcNow - lastUsedAt.Value).TotalHours;
        var decayed = usageCount * Math.Exp(-Math.Log(2) * ageHours / DecayHalfLifeHours);
        // Squash via log so a single heavily-used skill doesn't dominate over
        // many moderately-used ones. log(1+20) is the saturation point; above
        // that the contribution is ~1.0.
        return Math.Min(1.0, Math.Log(1 + decayed) / Math.Log(1 + 20.0));
    }
}
