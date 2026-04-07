module TotalRecall.Core.Ranking

// Hybrid search ranking math. Combines vector similarity and FTS scores.

/// Combine a vector similarity score (0..1, higher is better) and an FTS rank score
/// into a single sortable hybrid score. ftsWeight controls the mix.
let hybridScore
    (vectorScore: float)
    (ftsScore: float)
    (ftsWeight: float)
    : float =
    failwith "TotalRecall.Core.Ranking.hybridScore not yet implemented (Plan 2 Task 2.8)"
