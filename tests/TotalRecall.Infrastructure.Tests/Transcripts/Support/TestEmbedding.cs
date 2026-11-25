using System;
using Microsoft.FSharp.Core;
using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.TestSupport.Transcripts;

/// <summary>
/// Embedding helpers shared by the transcript sync and search tests: the <c>[embedding]</c> config of the local
/// bge-small model (or of a named provider), and an embedder whose descriptor cannot be read. Uses only public API.
/// </summary>
public static class TestEmbedding
{
    /// <summary>The <c>[embedding]</c> config for bge-small-en-v1.5 (384 dimensions), with <paramref name="provider"/> when given.</summary>
    public static global::TotalRecall.Core.Config.EmbeddingConfig Config(string? provider = null) => new(
        "bge-small-en-v1.5",
        384,
        provider is null ? FSharpOption<string>.None : FSharpOption<string>.Some(provider),
        FSharpOption<string>.None,
        FSharpOption<string>.None,
        FSharpOption<string>.None,
        FSharpOption<string>.None,
        FSharpOption<string>.None,
        FSharpOption<string>.None,
        FSharpOption<string>.None);
}

/// <summary>An <see cref="IEmbedder"/> whose descriptor throws and that must never be asked to embed.</summary>
public sealed class ThrowingDescriptorEmbedder : IEmbedder
{
    public EmbedderDescriptor Descriptor => throw new InvalidOperationException("no descriptor");
    public float[] Embed(string text) => throw new InvalidOperationException("must not be called");
    public float[] EmbedQuery(string text) => throw new InvalidOperationException("must not be called");
}
