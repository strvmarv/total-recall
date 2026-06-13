// Lightweight IEmbedder fake for Plan 4 handler tests. Records the text it
// was asked to embed and returns a fixed 384-dim vector so the
// MemoryStoreHandler can exercise its embed+insert pipeline without loading
// the real ONNX model.

using System.Collections.Generic;
using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.Server.Tests.TestSupport;

public sealed class RecordingFakeEmbedder : IEmbedder
{
    public List<string> Calls { get; } = new();

        public EmbedderDescriptor Descriptor { get; } = new("test", "fake", "", 384);

    /// <summary>
    /// Set when <see cref="EmbedQuery"/> (the asymmetric query path) is used
    /// rather than <see cref="Embed"/> (the document path). Lets handler tests
    /// assert that queries flow through the query-specific method.
    /// </summary>
    public bool EmbedQueryCalled { get; private set; }

    public float[] Embed(string text)
    {
        Calls.Add(text);
        var v = new float[384];
        for (var i = 0; i < 384; i++) v[i] = 0.1f;
        return v;
    }

    // Declaring EmbedQuery on the class overrides the IEmbedder default
    // interface method, so this version is dispatched for query embedding.
    public float[] EmbedQuery(string text)
    {
        EmbedQueryCalled = true;
        return Embed(text); // reuse recording + vector logic
    }
}
