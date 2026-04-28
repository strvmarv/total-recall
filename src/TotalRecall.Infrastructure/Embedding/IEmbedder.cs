namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Text embedder interface. Accepts raw text (tokenization is the
/// implementation's responsibility) and returns a dense float vector.
/// Implementations are expected to lazily load any heavyweight resources
/// (models, vocab) on the first call.
/// </summary>
public interface IEmbedder
{
    /// <summary>
    /// Embed a single text string into a dense vector of length equal to the
    /// underlying model's hidden size. The returned array is L2-normalized so
    /// callers can treat dot-products as cosine similarities.
    /// </summary>
    float[] Embed(string text);

    /// <summary>
    /// Identity of the model this embedder produces vectors for. Used by
    /// <see cref="EmbedderFingerprint"/> to detect silent model swaps on
    /// existing databases. Implementations MUST return a stable value that
    /// does not require loading heavy resources — callers may invoke this
    /// at startup before any <see cref="Embed"/> call.
    /// </summary>
    EmbedderDescriptor Descriptor { get; }
}
