module TotalRecall.Core.Config

// F# records for the TotalRecall configuration.
//
// IMPORTANT: config files are TOML, and loading them needs file I/O. Both
// belong in Infrastructure, NOT in Core. Core just defines the type shape and
// offers a few pure helpers (deepMerge, setNestedKey) that operate on
// language-neutral dictionaries.

type HotTierConfig = {
    MaxEntries: int
    TokenBudget: int
    CarryForwardThreshold: float
    TaskWeight: float
    CompactionHintThreshold: int
    MaxContentChars: int
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

type PinnedTierConfig = {
    MaxContentChars: int
    FloorEnabled: bool
    FloorEveryNTurns: int
    FloorGrowthTokens: int
    ProjectScoping: bool
}

type TiersConfig = {
    Hot: HotTierConfig
    Warm: WarmTierConfig
    Cold: ColdTierConfig
    Pinned: PinnedTierConfig option   // optional: absent in existing config files
}

type CompactionConfig = {
    DecayHalfLifeHours: float
    WarmThreshold: float
    PromoteThreshold: float
    WarmSweepIntervalDays: int
    /// Phase 2 idea 1c — per-type decay half-lives (hours).
    /// Falls back to DecayHalfLifeHours when not set for a given type.
    DecayHalfLifeCorrection: float option
    DecayHalfLifePreference: float option
    DecayHalfLifeSurfaced: float option
    DecayHalfLifeDecision: float option
    AutoDemoteMinInjections: int
    PromoteMinAccess: int
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
    EmbeddingQueryPrefix: string option
    OnModelChange: string option
}

type StorageConfig = {
    ConnectionString: string option
    Mode: string option
}

type CortexConfig = {
    Url: string
    Pat: string
    SyncIntervalSeconds: int option
}

type GitSessionStart =
    | Blocking
    | Background
    | Off

/// The `[git_sync]` settings after normalisation. `Remote` is `None` when not set or not usable; `Branch` is
/// `Some "main"` when absent and `None` only when a value was given that cannot be used. `Warnings` lists what
/// was corrected, one line each.
type GitSyncConfig = {
    Remote: string option
    Branch: string option
    IntervalSeconds: int
    SessionStart: GitSessionStart
    SessionStartSoftSeconds: int
    SessionStartMaxSeconds: int
    SessionStartStallSeconds: int
    SessionEnd: bool
    SessionEndSeconds: int
    DeleteGuardPercent: int
    Warnings: string list
}

/// One TOML value as the loader saw it. `RawBad` carries a phrase naming an unsupported type ("a float", "an array").
type GitRawValue =
    | RawAbsent
    | RawBad of string
    | RawStr of string
    | RawInt of int64
    | RawBool of bool

/// The `[git_sync]` table as read from TOML, one raw value per key. Unknown keys are not carried.
type RawGitSync = {
    RemoteRaw: GitRawValue
    BranchRaw: GitRawValue
    IntervalRaw: GitRawValue
    SessionStartRaw: GitRawValue
    SoftRaw: GitRawValue
    MaxRaw: GitRawValue
    StallRaw: GitRawValue
    SessionEndRaw: GitRawValue
    SessionEndSecondsRaw: GitRawValue
    DeleteGuardRaw: GitRawValue
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

type ScopeConfig = {
    Default: string option
}

type SkillConfig = {
    ExtraDirs: string [] option
    /// Whether locally-scanned host-tool skills get uploaded to Cortex
    /// (cortex storage mode only). None/absent means disabled — auto-import
    /// must be explicitly opted into; it is not the safe-by-default behavior.
    AutoImport: bool option
}

/// Phase 3 idea 2c — tool-result cache settings.
type ToolCacheConfig = {
    MaxEntries: int
    DefaultTtlSeconds: int
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
    Scope: ScopeConfig option
    Skill: SkillConfig option
    ToolCache: ToolCacheConfig option
    GitSync: GitSyncConfig option
}

/// The pure side of the `[git_sync]` settings: defaults, range constants and `normalise`, which turns any raw
/// input into a usable configuration plus warnings. Total: it never throws.
module GitSyncSettings =

    [<Literal>]
    let DefaultBranch = "main"
    [<Literal>]
    let DefaultIntervalSeconds = 300
    [<Literal>]
    let MinIntervalSeconds = 30
    [<Literal>]
    let MaxIntervalSeconds = 86400
    [<Literal>]
    let MaxSeconds = 3600
    [<Literal>]
    let DefaultSoftSeconds = 10
    [<Literal>]
    let DefaultMaxSeconds = 20
    [<Literal>]
    let DefaultStallSeconds = 8
    [<Literal>]
    let DefaultSessionEndSeconds = 8
    [<Literal>]
    let DefaultDeleteGuardPercent = 20

    let defaults : GitSyncConfig =
        { Remote = None
          Branch = Some DefaultBranch
          IntervalSeconds = DefaultIntervalSeconds
          SessionStart = Blocking
          SessionStartSoftSeconds = DefaultSoftSeconds
          SessionStartMaxSeconds = DefaultMaxSeconds
          SessionStartStallSeconds = DefaultStallSeconds
          SessionEnd = true
          SessionEndSeconds = DefaultSessionEndSeconds
          DeleteGuardPercent = DefaultDeleteGuardPercent
          Warnings = [] }

    let private described (v: GitRawValue) : string =
        match v with
        | RawAbsent -> "nothing"
        | RawBad phrase -> phrase
        | RawStr _ -> "a string"
        | RawInt _ -> "an integer"
        | RawBool _ -> "a boolean"

    let private isAlnum (c: char) =
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')

    /// A repository address that cannot be an option to `git` and carries no whitespace or control
    /// characters. Scheme and transport checks belong to milestone 1a.
    let isUsableRemote (s: string) : bool =
        s.Length > 0
        && s.[0] <> '-'
        && not (s |> Seq.exists (fun c -> System.Char.IsWhiteSpace c || System.Char.IsControl c))

    /// `^[A-Za-z0-9][A-Za-z0-9._/-]{0,99}$` with no `..` and no `//`, not ending in `/` or `.`, and no
    /// `/`-separated component that begins with `.` or ends with `.lock`.
    let isUsableBranch (s: string) : bool =
        s.Length >= 1
        && s.Length <= 100
        && isAlnum s.[0]
        && (s |> Seq.forall (fun c -> isAlnum c || c = '.' || c = '_' || c = '/' || c = '-'))
        && not (s.Contains("..", System.StringComparison.Ordinal))
        && not (s.Contains("//", System.StringComparison.Ordinal))
        && not (s.EndsWith("/", System.StringComparison.Ordinal))
        && not (s.EndsWith(".", System.StringComparison.Ordinal))
        && (s.Split('/')
            |> Array.forall (fun part ->
                not (part.StartsWith(".", System.StringComparison.Ordinal))
                && not (part.EndsWith(".lock", System.StringComparison.Ordinal))))

    /// An integer key in `lo..hi`: the value, a description of the problem (without the "using" part) and
    /// whether the value was explicit and valid.
    let private intKey (lo: int) (hi: int) (dflt: int) (raw: GitRawValue) : int * string option * bool =
        match raw with
        | RawAbsent -> dflt, None, false
        | RawInt n when n >= int64 lo && n <= int64 hi -> int n, None, true
        | RawInt n -> dflt, Some (sprintf "%d is outside %d to %d" n lo hi), false
        | other -> dflt, Some (sprintf "expected an integer, got %s" (described other)), false

    let private warn (key: string) (problem: string option) (used: int) : string option =
        problem |> Option.map (fun p -> sprintf "git_sync.%s: %s; using %d" key p used)

    let normalise (raw: RawGitSync) : GitSyncConfig =
        // a null string (only a hand-built caller can make one) is treated as an empty string
        let orEmpty (v: GitRawValue) : GitRawValue =
            match v with
            | RawStr s when isNull s -> RawStr ""
            | other -> other
        let remote, remoteWarn =
            match orEmpty raw.RemoteRaw with
            | RawAbsent -> None, None
            | RawStr s ->
                let t = s.Trim()
                if t = "" then None, None
                elif isUsableRemote t then Some t, None
                else None, Some "git_sync.remote: not a usable repository address; treated as not set"
            | other ->
                None, Some (sprintf "git_sync.remote: expected a string, got %s; treated as not set" (described other))
        let branch, branchWarn =
            match orEmpty raw.BranchRaw with
            | RawAbsent -> Some DefaultBranch, None
            | RawStr s when isUsableBranch s -> Some s, None
            | RawStr _ -> None, Some "git_sync.branch: not a usable branch name; treated as not set"
            | other ->
                None, Some (sprintf "git_sync.branch: expected a string, got %s; treated as not set" (described other))
        let interval, intervalWarn =
            match raw.IntervalRaw with
            | RawAbsent -> DefaultIntervalSeconds, None
            | RawInt 0L -> 0, None
            | RawInt n when n >= 1L && n < int64 MinIntervalSeconds ->
                MinIntervalSeconds,
                Some (sprintf "git_sync.interval_seconds: %d is below the minimum of %d; using %d" n MinIntervalSeconds MinIntervalSeconds)
            | RawInt n when n >= int64 MinIntervalSeconds && n <= int64 MaxIntervalSeconds -> int n, None
            | RawInt n ->
                DefaultIntervalSeconds,
                Some (sprintf "git_sync.interval_seconds: %d is outside 0 or %d to %d; using %d" n MinIntervalSeconds MaxIntervalSeconds DefaultIntervalSeconds)
            | other ->
                DefaultIntervalSeconds,
                Some (sprintf "git_sync.interval_seconds: expected an integer, got %s; using %d" (described other) DefaultIntervalSeconds)
        let sessionStart, sessionStartWarn =
            match orEmpty raw.SessionStartRaw with
            | RawAbsent -> Blocking, None
            | RawStr "blocking" -> Blocking, None
            | RawStr "background" -> Background, None
            | RawStr "off" -> Off, None
            | RawStr _ ->
                Blocking, Some "git_sync.session_start: not one of \"blocking\", \"background\", \"off\"; using \"blocking\""
            | other ->
                Blocking, Some (sprintf "git_sync.session_start: expected a string, got %s; using \"blocking\"" (described other))
        let soft, softProblem, softValid = intKey 1 MaxSeconds DefaultSoftSeconds raw.SoftRaw
        let maxSeconds, maxProblem, _ = intKey 1 MaxSeconds DefaultMaxSeconds raw.MaxRaw
        let stall, stallProblem, _ = intKey 1 MaxSeconds DefaultStallSeconds raw.StallRaw
        let sessionEnd, sessionEndWarn =
            match raw.SessionEndRaw with
            | RawAbsent -> true, None
            | RawBool b -> b, None
            | other -> true, Some (sprintf "git_sync.session_end: expected a boolean, got %s; using true" (described other))
        let endSeconds, endProblem, _ = intKey 1 MaxSeconds DefaultSessionEndSeconds raw.SessionEndSecondsRaw
        let guard, guardProblem, _ = intKey 0 100 DefaultDeleteGuardPercent raw.DeleteGuardRaw
        // The soft limit never exceeds the hard one. The warning is given only for a valid explicit soft; a bad
        // soft that is then lowered names the value finally used.
        let finalSoft = if soft > maxSeconds then maxSeconds else soft
        let softWarn =
            if softValid && soft > maxSeconds then
                Some (sprintf "git_sync.session_start_soft_seconds: %d is above session_start_max_seconds; using %d" soft maxSeconds)
            else warn "session_start_soft_seconds" softProblem finalSoft
        let warnings =
            [ remoteWarn
              branchWarn
              intervalWarn
              sessionStartWarn
              softWarn
              warn "session_start_max_seconds" maxProblem maxSeconds
              warn "session_start_stall_seconds" stallProblem stall
              sessionEndWarn
              warn "session_end_seconds" endProblem endSeconds
              warn "delete_guard_percent" guardProblem guard ]
            |> List.choose id
        { Remote = remote
          Branch = branch
          IntervalSeconds = interval
          SessionStart = sessionStart
          SessionStartSoftSeconds = finalSoft
          SessionStartMaxSeconds = maxSeconds
          SessionStartStallSeconds = stall
          SessionEnd = sessionEnd
          SessionEndSeconds = endSeconds
          DeleteGuardPercent = guard
          Warnings = warnings }

// --- pure helpers (isSafeKey, deepMerge, setNestedKey) ---

/// Reject keys that could lead to prototype-pollution-like issues when used
/// with untyped dictionary representations. Direct port of TS isSafeKey.
let isSafeKey (key: string) : bool =
    key <> "__proto__" && key <> "constructor" && key <> "prototype"

/// Deep-merge a source map over a target map. Nested maps are merged
/// recursively; primitive values from source override target. Skips unsafe
/// keys (see isSafeKey). Pure: returns a new map without mutating inputs.
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
