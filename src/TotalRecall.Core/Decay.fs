module TotalRecall.Core.Decay

// Decay score calculation. Pure function over metadata.

/// Calculate a decay score given the entry's last-accessed timestamp, current time,
/// and the decay half-life in hours. Result is in [0.0, 1.0] where 1.0 is fresh.
let calculateDecayScore
    (lastAccessedAtMs: int64)
    (nowMs: int64)
    (halfLifeHours: float)
    : float =
    failwith "TotalRecall.Core.Decay.calculateDecayScore not yet implemented (Plan 2 Task 2.6)"
