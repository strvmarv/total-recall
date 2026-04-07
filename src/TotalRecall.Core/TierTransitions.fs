module TotalRecall.Core.TierTransitions

open TotalRecall.Core

// Pure tier-state transitions. Validates whether a given move is allowed.

type TransitionResult =
    | Allowed
    | Rejected of reason: string

/// Validate a promotion (moving an entry to a higher tier).
let validatePromotion (from: Tier) (toTier: Tier) : TransitionResult =
    failwith "TotalRecall.Core.TierTransitions.validatePromotion not yet implemented (Plan 2 Task 2.7)"

/// Validate a demotion (moving an entry to a lower tier).
let validateDemotion (from: Tier) (toTier: Tier) : TransitionResult =
    failwith "TotalRecall.Core.TierTransitions.validateDemotion not yet implemented (Plan 2 Task 2.7)"
