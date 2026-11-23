using System;
using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.TestSupport.Transcripts;

/// <summary>
/// The one way a <see cref="BgeModelFactAttribute"/> test gets the real bge-small embedder. Every call first asks
/// <see cref="BgeModelFactAttribute.RequireModelPath"/>, so with the model missing and <c>CI</c> set the test fails
/// instead of passing unrun (a fact attribute cannot fail a test by itself). Then it inspects the configured embedder
/// the way production does (<see cref="EmbedderInspection.Inspect"/>, which finds the bundled <c>models/</c> by walking
/// up from the test assembly) and refuses anything but a ready local bge-small with 384 dimensions, so an acceptance
/// test can never pass on another model. Uses only public API.
/// </summary>
public static class BgeModel
{
    public const string ModelName = "bge-small-en-v1.5";

    /// <summary>
    /// The inspection of the real local embedder for <paramref name="cfg"/> (a config with no <c>[embedding]</c>
    /// provider, or <c>local</c>). The caller owns the returned embedder (dispose it when it is
    /// <see cref="IDisposable"/>). Throws when the model is missing or the embedder is not the ready local bge-small.
    /// </summary>
    public static EmbedderInspectionResult Inspect(Core.Config.EmbeddingConfig cfg, int? intraOpThreads = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        Require();

        var result = EmbedderInspection.Inspect(cfg, intraOpThreads);
        if (result.Embedder is null || !result.Readiness.Ready)
        {
            (result.Embedder as IDisposable)?.Dispose();
            throw new InvalidOperationException(
                "the real bge-small embedder is not ready: " + (result.Readiness.Reason ?? result.FactoryError ?? "no embedder"));
        }

        var d = result.Embedder.Descriptor;
        if (d.Provider != "local" || d.Model != ModelName || d.Dimensions != 384)
        {
            (result.Embedder as IDisposable)?.Dispose();
            throw new InvalidOperationException(
                $"expected the local {ModelName} with 384 dimensions, got {d.Provider} {d.Model} {d.Dimensions}");
        }
        return result;
    }

    /// <summary>
    /// The path of <c>model.onnx</c>, for a test whose embedder production code builds itself (a server composition):
    /// it must still fail when the model is missing and <c>CI</c> is set.
    /// </summary>
    public static string Require() => BgeModelFactAttribute.RequireModelPath();

    /// <summary>The real embedder alone (see <see cref="Inspect"/>); the caller disposes it.</summary>
    public static IEmbedder Embedder(Core.Config.EmbeddingConfig cfg) => Inspect(cfg).Embedder!;
}
