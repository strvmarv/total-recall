using System;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Transcripts;

namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// What <see cref="EmbedderInspection.Inspect"/> found. <paramref name="Embedder"/> is null when the factory threw
/// (<paramref name="FactoryError"/> then holds the message and <paramref name="Readiness"/> is not ready with it).
/// Callers dispose <paramref name="Embedder"/> when done.
/// </summary>
public sealed record EmbedderInspectionResult(
    IEmbedder? Embedder,
    ArchiveFingerprint? Current,
    EmbedderReadiness Readiness,
    string? FactoryError);

/// <summary>Builds the configured embedder (construction is lazy and cheap) and reads its identity and readiness.</summary>
public static class EmbedderInspection
{
    public static EmbedderInspectionResult Inspect(
        Core.Config.EmbeddingConfig cfg,
        int? intraOpThreads = null,
        Func<Core.Config.EmbeddingConfig, int?, IEmbedder>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var make = factory ?? EmbedderFactory.CreateFromConfig;

        IEmbedder embedder;
        try
        {
            embedder = make(cfg, intraOpThreads);
        }
        catch (Exception ex)
        {
            ExceptionLogger.LogChain("[total-recall] embedder inspection", ex);
            return new EmbedderInspectionResult(null, null, new EmbedderReadiness(false, ex.Message), ex.Message);
        }

        try
        {
            var d = embedder.Descriptor;
            var current = new ArchiveFingerprint(d.Provider, d.Model, d.Revision, d.Dimensions);
            var readiness = (embedder as IEmbedderReadiness)?.CheckReady() ?? new EmbedderReadiness(true, null);
            return new EmbedderInspectionResult(embedder, current, readiness, null);
        }
        catch (Exception ex)
        {
            (embedder as IDisposable)?.Dispose();
            ExceptionLogger.LogChain("[total-recall] embedder inspection", ex);
            return new EmbedderInspectionResult(null, null, new EmbedderReadiness(false, ex.Message), ex.Message);
        }
    }
}
