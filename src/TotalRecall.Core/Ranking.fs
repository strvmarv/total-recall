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
