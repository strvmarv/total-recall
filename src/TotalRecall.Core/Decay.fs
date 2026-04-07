module TotalRecall.Core.Decay

open TotalRecall.Core

// Decay score calculation. Pure function over entry metadata.
//
// Ported from src-ts/memory/decay.ts. The formula:
//
//   timeFactor = exp(-hoursSinceAccess / decayConstantHours)
//   freqFactor = 1 + log2(1 + accessCount)
//   typeWeight = (per EntryType, see TypeWeights below)
//   score      = timeFactor * freqFactor * typeWeight
//
// NOTE: The TS source calls the time-constant parameter "decay_half_life_hours"
// but the formula is exp(-x/τ), not 0.5^(x/halfLife). At hours = τ, the
// timeFactor is 1/e ≈ 0.368, not 0.5. The parameter name is misleading;
// we keep it consistent with the TS source for now (Plan 3 Infrastructure
// reads from the same config field).
//
// The result is NOT clamped to [0,1]: typeWeight can be 1.5 and freqFactor
// grows with access_count, so the score can exceed 1.0 and grow unboundedly
// in theory. Clamping is the consumer's responsibility.

let private MS_PER_HOUR = 60.0 * 60.0 * 1000.0

let typeWeight (entryType: EntryType) : float =
    match entryType with
    | Correction -> 1.5
    | Preference -> 1.3
    | Decision -> 1.0
    | Surfaced -> 0.8
    | Imported -> 1.1
    | Compacted -> 1.0
    | Ingested -> 0.9

/// Calculate a decay score for an entry given its access metadata, type,
/// the current time, and the decay-constant (named "half life" in the
/// config, but actually the time constant τ for the exponential).
let calculateDecayScore
    (lastAccessedAtMs: int64)
    (accessCount: int)
    (entryType: EntryType)
    (nowMs: int64)
    (decayConstantHours: float)
    : float =
    let hoursSinceAccess = float (nowMs - lastAccessedAtMs) / MS_PER_HOUR
    let timeFactor = exp (-hoursSinceAccess / decayConstantHours)
    let freqFactor = 1.0 + (log (1.0 + float accessCount) / log 2.0)
    let typeWeight = typeWeight entryType
    timeFactor * freqFactor * typeWeight
