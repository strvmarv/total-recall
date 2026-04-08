using System.Collections.Generic;

namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Immutable description of a model entry in the registry.
/// Ported from the lookup/validation subset of <c>src-ts/embedding/registry.ts</c>.
/// </summary>
public sealed record ModelSpec(
    string Name,
    int Dimensions,
    string Sha256,
    long SizeBytes,
    string Revision,
    IReadOnlyDictionary<string, string> Files);
