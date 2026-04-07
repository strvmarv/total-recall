module TotalRecall.Core.Config

// F# records mirroring the TS TotalRecallConfig. Pure parsing and validation.

type HotTierConfig = {
    MaxEntries: int
    TokenBudget: int
    CarryForwardThreshold: float
}

type WarmTierConfig = {
    MaxEntries: int
    RetrievalTopK: int
    SimilarityThreshold: float
    ColdDecayDays: int
}

type ColdTierConfig = {
    ChunkMaxTokens: int
    ChunkOverlapTokens: int
    LazySummaryThreshold: int
}

type TiersConfig = {
    Hot: HotTierConfig
    Warm: WarmTierConfig
    Cold: ColdTierConfig
}

type CompactionConfig = {
    DecayHalfLifeHours: float
    WarmThreshold: float
    PromoteThreshold: float
    WarmSweepIntervalDays: int
}

type EmbeddingConfig = {
    Model: string
    Dimensions: int
}

type RegressionConfig = {
    MissRateDelta: float option
    LatencyRatio: float option
    MinEvents: int option
}

type SearchConfig = {
    FtsWeight: float option
}

type TotalRecallConfig = {
    Tiers: TiersConfig
    Compaction: CompactionConfig
    Embedding: EmbeddingConfig
    Regression: RegressionConfig option
    Search: SearchConfig option
}

type ValidationError =
    | MissingField of path: string
    | InvalidValue of path: string * reason: string

/// Parse a JSON string into a TotalRecallConfig, returning Result.
let parseConfigJson (json: string) : Result<TotalRecallConfig, ValidationError list> =
    failwith "TotalRecall.Core.Config.parseConfigJson not yet implemented (Plan 2 Task 2.12)"
