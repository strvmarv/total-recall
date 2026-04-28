namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Identifies the embedding model that produced a vector. Persisted to the
/// <c>_meta</c> table on first store open so later startups can detect a
/// silent model swap (same dimensions, different semantic space) and refuse
/// to proceed rather than silently degrade retrieval quality.
///
/// See <see cref="EmbedderFingerprint"/> for the read/write/guard logic.
/// </summary>
/// <param name="Provider">Provider tag: <c>local</c>, <c>openai</c>, or <c>bedrock</c>.</param>
/// <param name="Model">Model identifier (e.g. <c>all-MiniLM-L6-v2</c>, <c>text-embedding-3-small</c>).</param>
/// <param name="Revision">
/// Revision/version of the model. For the local ONNX provider this is the
/// <c>revision</c> field from <c>models/registry.json</c>; for remote providers
/// it is the empty string unless the provider API exposes a stable identifier.
/// </param>
/// <param name="Dimensions">Embedding dimensionality.</param>
public sealed record EmbedderDescriptor(
    string Provider,
    string Model,
    string Revision,
    int Dimensions);
