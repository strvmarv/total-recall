// Lightweight IVectorSearch fake for Plan 4 handler tests. Records
// InsertEmbedding calls; search methods throw so their accidental use shows
// up loudly.

using System;
using System.Collections.Generic;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Search;

namespace TotalRecall.Server.Tests.TestSupport;

public sealed class FakeVectorSearch : IVectorSearch
{
    public sealed record InsertCall(Tier Tier, ContentType Type, string EntryId, float[] Embedding);

    public List<InsertCall> InsertCalls { get; } = new();

    public void InsertEmbedding(Tier tier, ContentType type, string entryId, ReadOnlyMemory<float> embedding)
    {
        InsertCalls.Add(new InsertCall(tier, type, entryId, embedding.ToArray()));
    }

    public void DeleteEmbedding(Tier tier, ContentType type, string entryId) =>
        throw new NotImplementedException();

    public IReadOnlyList<VectorSearchResult> SearchByVector(
        Tier tier,
        ContentType type,
        ReadOnlyMemory<float> queryVec,
        VectorSearchOpts opts) =>
        throw new NotImplementedException();

    public IReadOnlyList<VectorSearchResult> SearchMultipleTiers(
        IReadOnlyList<(Tier Tier, ContentType Type)> targets,
        ReadOnlyMemory<float> queryVec,
        VectorSearchOpts opts) =>
        throw new NotImplementedException();
}
