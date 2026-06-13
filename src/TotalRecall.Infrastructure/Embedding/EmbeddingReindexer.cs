using System;
using System.IO;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Re-embeds every stored content row in place with a (new) embedder and
/// replaces its vec0 row. Backend-agnostic over <see cref="IStore"/> +
/// <see cref="IVectorSearch"/>. Documents use the symmetric <see cref="IEmbedder.Embed"/>
/// path (no query prefix).
/// </summary>
public sealed class EmbeddingReindexer
{
    private static readonly (Tier Tier, ContentType Type)[] Pairs =
    {
        (Tier.Pinned, ContentType.Memory),
        (Tier.Hot, ContentType.Memory),
        (Tier.Warm, ContentType.Memory),
        (Tier.Cold, ContentType.Memory),
        (Tier.Pinned, ContentType.Knowledge),
        (Tier.Hot, ContentType.Knowledge),
        (Tier.Warm, ContentType.Knowledge),
        (Tier.Cold, ContentType.Knowledge),
    };

    private readonly IStore _store;
    private readonly IVectorSearch _vectors;
    private readonly IEmbedder _embedder;

    public EmbeddingReindexer(IStore store, IVectorSearch vectors, IEmbedder embedder)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vectors = vectors ?? throw new ArgumentNullException(nameof(vectors));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

    /// <summary>Re-embed every row; returns the number of vectors rewritten.</summary>
    public int Reindex(TextWriter? progress)
    {
        int total = 0;
        foreach (var (tier, type) in Pairs)
        {
            var rows = _store.List(tier, type);
            int inPair = 0;
            foreach (var entry in rows)
            {
                var vec = _embedder.Embed(entry.Content);
                var rowid = _store.GetInternalKey(tier, type, entry.Id);
                if (rowid is not null)
                    _vectors.DeleteEmbedding(tier, type, rowid.Value);
                _vectors.InsertEmbedding(tier, type, entry.Id, vec);
                inPair++;
                total++;
            }
            if (inPair > 0)
                progress?.WriteLine($"  {TotalRecall.Infrastructure.Memory.TierNames.TierName(tier)}/{TotalRecall.Infrastructure.Memory.TierNames.ContentTypeName(type)}: {inPair} re-embedded");
        }
        return total;
    }
}
