module TotalRecall.Core.Compaction

open TotalRecall.Core

// Compaction decision vocabulary. Pure data types; the deciding happens
// externally (in the host tool's subagent per the spec). Core just defines
// what a valid decision looks like.

type CompactionDecision =
    /// Keep the entry in hot tier.
    | CarryForward
    /// Promote to warm tier, optionally replacing content with a summary.
    | Promote of summary: string option
    /// Discard the entry with a reason.
    | Discard of reason: string

/// Input to a compaction cycle: the entries currently in hot tier.
type CompactionInput = {
    HotEntries: Entry list
    NowMs: int64
}
