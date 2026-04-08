// Recording IHybridSearch fake for Plan 4 handler tests. Captures the
// arguments the MemorySearchHandler passes to Search(...) so tests can
// assert the filter/topK/minScore pass-through, and returns a configurable
// SearchResult[] so the JSON response shape can be verified end-to-end.

using System;
using System.Collections.Generic;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Search;

namespace TotalRecall.Server.Tests.TestSupport;

public sealed class RecordingFakeHybridSearch : IHybridSearch
{
    public sealed record SearchCall(
        IReadOnlyList<(Tier Tier, ContentType Type)> Tiers,
        string Query,
        ReadOnlyMemory<float> QueryEmbedding,
        HybridSearchOpts Opts);

    public List<SearchCall> Calls { get; } = new();
    public IReadOnlyList<SearchResult> NextResult { get; set; } =
        Array.Empty<SearchResult>();

    public IReadOnlyList<SearchResult> Search(
        IReadOnlyList<(Tier Tier, ContentType Type)> tiers,
        string query,
        ReadOnlyMemory<float> queryEmbedding,
        HybridSearchOpts opts)
    {
        Calls.Add(new SearchCall(tiers, query, queryEmbedding, opts));
        return NextResult;
    }
}
