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
        Func<Core.Config.EmbeddingConfig, int?, IEmbedder>? factory = null) =>
        Inspect(cfg, intraOpThreads, factory ?? EmbedderFactory.CreateFromConfig, log: true);

    /// <summary>
    /// The same reading with no side effect: nothing is created on disk (see
    /// <see cref="EmbedderFactory.CreateFromConfigWithoutSideEffects"/>) and a failure is not logged; its message is
    /// in the result's readiness (for <c>transcripts status</c>, which reports and never creates anything). The
    /// embedder is built for inspection only and should not be used to embed.
    /// </summary>
    public static EmbedderInspectionResult InspectWithoutSideEffects(Core.Config.EmbeddingConfig cfg) =>
        Inspect(cfg, null, (c, _) => EmbedderFactory.CreateFromConfigWithoutSideEffects(c), log: false);

    private static EmbedderInspectionResult Inspect(
        Core.Config.EmbeddingConfig cfg,
        int? intraOpThreads,
        Func<Core.Config.EmbeddingConfig, int?, IEmbedder> make,
        bool log)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        IEmbedder embedder;
        try
        {
            embedder = make(cfg, intraOpThreads);
        }
        catch (Exception ex)
        {
            if (log)
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
            if (log)
                ExceptionLogger.LogChain("[total-recall] embedder inspection", ex);
            return new EmbedderInspectionResult(null, null, new EmbedderReadiness(false, ex.Message), ex.Message);
        }
    }
}
