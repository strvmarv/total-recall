module TotalRecall.Core.Config

// F# records mirroring src-ts/types.ts TotalRecallConfig.
//
// IMPORTANT: TS uses TOML for config files (smol-toml), and the loading is
// done with file I/O in src-ts/config.ts. Both belong in Infrastructure
// (Plan 3), NOT in Core. Core just defines the type shape and offers a
// few pure helpers (deepMerge, setNestedKey) that operate on
// language-neutral dictionaries.

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
    Provider: string option
    Endpoint: string option
    BedrockRegion: string option
    BedrockModel: string option
    ModelName: string option
    ApiKey: string option
}

type StorageConfig = {
    ConnectionString: string option
    Mode: string option
}

type CortexConfig = {
    Url: string
    Pat: string
}

type UserConfig = {
    UserId: string option
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
    Storage: StorageConfig option
    User: UserConfig option
    Cortex: CortexConfig option
}

// --- pure helpers (mirrors src-ts/config.ts isSafeKey + deepMerge + setNestedKey) ---

/// Reject keys that could lead to prototype-pollution-like issues when used
/// with untyped dictionary representations. Direct port of TS isSafeKey.
let isSafeKey (key: string) : bool =
    key <> "__proto__" && key <> "constructor" && key <> "prototype"

/// Deep-merge a source map over a target map. Nested maps are merged
/// recursively; primitive values from source override target. Skips unsafe
/// keys (see isSafeKey). Pure: returns a new map without mutating inputs.
///
/// Equivalent to the TS deepMerge function in src-ts/config.ts.
let rec deepMerge
    (target: Map<string, obj>)
    (source: Map<string, obj>)
    : Map<string, obj> =
    let mutable result = target
    for KeyValue (key, sourceValue) in source do
        if isSafeKey key then
            match Map.tryFind key target, sourceValue with
            | Some (:? Map<string, obj> as targetMap), (:? Map<string, obj> as sourceMap) ->
                result <- Map.add key (deepMerge targetMap sourceMap :> obj) result
            | _ ->
                result <- Map.add key sourceValue result
    result

/// Set a value at a dotted key path inside a map, creating intermediate
/// maps as needed. Returns a new map without mutating the input. Throws
/// ArgumentException if any segment of the dotted key is unsafe.
///
/// Equivalent to the TS setNestedKey function in src-ts/config.ts.
let setNestedKey
    (obj: Map<string, obj>)
    (dotKey: string)
    (value: obj)
    : Map<string, obj> =
    let parts = dotKey.Split('.')
    if parts |> Array.exists (isSafeKey >> not) then
        raise (System.ArgumentException(sprintf "Invalid config key segment in %s" dotKey))

    let rec setAt (current: Map<string, obj>) (idx: int) : Map<string, obj> =
        let part = parts.[idx]
        if idx = parts.Length - 1 then
            Map.add part value current
        else
            let nested =
                match Map.tryFind part current with
                | Some (:? Map<string, obj> as m) -> m
                | _ -> Map.empty
            Map.add part (setAt nested (idx + 1) :> obj) current
    setAt obj 0
