namespace TotalRecall.Core

// Shared types for the pure logic layer. No I/O, no framework dependencies.
//
// Mirrors (but does not re-use) the types in src-ts/types.ts. We deliberately
// do NOT share types with Infrastructure — Core is a closed language that only
// depends on the F# standard library and its own modules.

type Tier =
    | Hot
    | Warm
    | Cold

type ContentType =
    | Memory
    | Knowledge

type EntryType =
    | Correction
    | Preference
    | Decision
    | Surfaced
    | Imported
    | Compacted
    | Ingested

type SourceTool =
    | ClaudeCode
    | CopilotCli
    | Opencode
    | Cursor
    | Cline
    | Hermes
    | ManualSource

/// A memory or knowledge entry. Mirrors the TS Entry interface but uses F# types.
type Entry = {
    Id: string
    Content: string
    Summary: string option
    Source: string option
    SourceTool: SourceTool option
    Project: string option
    Tags: string list
    CreatedAt: int64
    UpdatedAt: int64
    LastAccessedAt: int64
    AccessCount: int
    DecayScore: float
    ParentId: string option
    CollectionId: string option
    /// JSON metadata stored as the raw string. Core does not interpret it;
    /// consumers do their own deserialization when needed.
    MetadataJson: string
}

/// A search result with score and rank, used by HybridSearch.
type SearchResult = {
    Entry: Entry
    Tier: Tier
    ContentType: ContentType
    Score: float
    Rank: int
}
