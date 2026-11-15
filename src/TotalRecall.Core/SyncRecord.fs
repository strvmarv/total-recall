module TotalRecall.Core.SyncRecord

// The record model for git sync (milestone 0b-1): one memory as it lives in the sync
// repository, with the wire names of its enums. Pure; the JSON codec is in Infrastructure.

open System
open TotalRecall.Core.SyncStamp

/// Where the entry sits and how that was last decided. Each half has its own stamp.
type Placement =
    { Tier: Tier
      TierStamp: Stamp
      Sticky: bool
      StickyStamp: Stamp }

/// Set only on entries an importer created; carried unchanged.
type Origin = { Tool: string; Hash: string }

/// `Tags` is sorted (UTF-16 code-unit order) and unique. `updated_at` is not a field: it is
/// always the time of `ContentStamp`.
type SyncRecord =
    { Id: string
      Content: string
      EntryType: EntryType
      Tags: string list
      Source: SourceTool option
      CreatedAtMs: int64
      Project: string option
      Scope: string option
      Placement: Placement
      ContentStamp: Stamp
      Origin: Origin option
      WrittenBy: string option }

let entryTypeName (t: EntryType) : string =
    match t with
    | Correction -> "correction"
    | Preference -> "preference"
    | Decision -> "decision"
    | Surfaced -> "surfaced"
    | Imported -> "imported"
    | Compacted -> "compacted"
    | Ingested -> "ingested"

let tryParseEntryType (s: string) : EntryType option =
    match s with
    | "correction" -> Some Correction
    | "preference" -> Some Preference
    | "decision" -> Some Decision
    | "surfaced" -> Some Surfaced
    | "imported" -> Some Imported
    | "compacted" -> Some Compacted
    | "ingested" -> Some Ingested
    | _ -> None

let sourceToolName (t: SourceTool) : string =
    match t with
    | ClaudeCode -> "claude-code"
    | CopilotCli -> "copilot-cli"
    | Opencode -> "opencode"
    | Cursor -> "cursor"
    | Cline -> "cline"
    | Hermes -> "hermes"
    | ManualSource -> "manual"

let tryParseSourceTool (s: string) : SourceTool option =
    match s with
    | "claude-code" -> Some ClaudeCode
    | "copilot-cli" -> Some CopilotCli
    | "opencode" -> Some Opencode
    | "cursor" -> Some Cursor
    | "cline" -> Some Cline
    | "hermes" -> Some Hermes
    | "manual" -> Some ManualSource
    | _ -> None

let tierName (t: Tier) : string =
    match t with
    | Hot -> "hot"
    | Warm -> "warm"
    | Cold -> "cold"

let tryParseTier (s: string) : Tier option =
    match s with
    | "hot" -> Some Hot
    | "warm" -> Some Warm
    | "cold" -> Some Cold
    | _ -> None

/// Hot is the warmest (0); a higher rank is colder.
let tierRank (t: Tier) : int =
    match t with
    | Hot -> 0
    | Warm -> 1
    | Cold -> 2

/// Tags are a set: sorted by UTF-16 code units (not UTF-8 byte order) and unique.
let normaliseTags (tags: string list) : string list =
    tags |> List.distinct |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
