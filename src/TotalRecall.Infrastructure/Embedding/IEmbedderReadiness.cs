namespace TotalRecall.Infrastructure.Embedding;

/// <summary>Whether an embedder can run, with a reason naming what is missing when it cannot.</summary>
public readonly record struct EmbedderReadiness(bool Ready, string? Reason);

/// <summary>
/// Implemented by embedders that can say, without loading anything, whether they can run
/// (the local model: files present and of the right size). Embedders that do not implement
/// it are treated as ready by their callers.
/// </summary>
public interface IEmbedderReadiness
{
    EmbedderReadiness CheckReady();
}
