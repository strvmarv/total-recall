module TotalRecall.Core.Ranking

// Hybrid search ranking math. Combines vector similarity and FTS scores.
//
// Ported from src-ts/memory/search.ts line 83:
//   const fusedScore = scores.vectorScore + ftsWeight * scores.ftsScore;
//
// This is an additive boost — the FTS score is added to the vector score,
// scaled by ftsWeight. Note this is NOT a convex combination: the fused
// score is unbounded above by max(vector, fts), it can grow beyond either.

/// Combine a vector similarity score and an FTS rank score into a single
/// sortable hybrid score. Pure function over the three numeric inputs.
let hybridScore
    (vectorScore: float)
    (ftsScore: float)
    (ftsWeight: float)
    : float =
    vectorScore + ftsWeight * ftsScore

/// Cosine similarity between two non-null float arrays of equal length.
/// Returns a value in [0, 1] — negative cosine scores are clamped to 0
/// because negative similarity is meaningless for context selection.
let private cosineSimilarity (a: float[]) (b: float[]) : float =
    let mutable dot = 0.0
    let mutable normA = 0.0
    let mutable normB = 0.0
    for i = 0 to a.Length - 1 do
        dot <- dot + a.[i] * b.[i]
        normA <- normA + a.[i] * a.[i]
        normB <- normB + b.[i] * b.[i]
    if normA = 0.0 || normB = 0.0 then 0.0
    else max 0.0 (dot / (sqrt normA * sqrt normB))

/// Phase 2 idea 2b — task-aware blended score for context selection.
/// Combines decay score and task-embedding cosine similarity via a
/// configurable taskWeight parameter.
///
///   blended = (1 - taskWeight) * (decayScore / maxDecayScore) + taskWeight * cosine(entry, task)
///
/// When taskWeight = 0, this is pure decay. When taskWeight = 1, pure task
/// similarity. The decay component is normalized against maxDecayScore so
/// both terms are on roughly [0,1] and the blend is well-behaved.
let taskAwareScore
    (entryEmbedding: float[])
    (taskEmbedding: float[])
    (decayScore: float)
    (maxDecayScore: float)
    (taskWeight: float)
    : float =
    let cosine = cosineSimilarity entryEmbedding taskEmbedding
    let decayNorm = if maxDecayScore > 0.0 then decayScore / maxDecayScore else 0.0
    (1.0 - taskWeight) * decayNorm + taskWeight * cosine
