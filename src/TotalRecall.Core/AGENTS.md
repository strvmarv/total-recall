# TotalRecall.Core — Agent Guide

The pure functional core of total-recall. Written in F#. **No I/O of any kind.**
No database access, no file system reads, no HTTP calls, no mutable state.
Every function is a pure transformation: input → output.

C# callers in `TotalRecall.Infrastructure` call into this layer via direct F# interop
(the projects reference each other via `ProjectReference`; F# modules compile to static classes).

---

## Module Reference

| File | Module | What it does |
|------|--------|-------------|
| `Types.fs` | `TotalRecall.Core` | Domain types: `Tier`, `ContentType`, `Entry`, `SearchResult`, config records |
| `Config.fs` | `TotalRecall.Core.Config` | TOML config record types (`TotalRecallConfig`, `TierConfig`, `EmbeddingConfig`, …) |
| `Tokenizer.fs` | `TotalRecall.Core.Tokenizer` | BERT BasicTokenization + WordPiece; counts tokens for budget enforcement |
| `Decay.fs` | `TotalRecall.Core.Decay` | Exponential decay scoring; `computeScore`, `shouldDemote`, `shouldPromote` |
| `Ranking.fs` | `TotalRecall.Core.Ranking` | Hybrid search result merging and re-ranking (vector + FTS scores) |
| `Compaction.fs` | `TotalRecall.Core.Compaction` | Hot-tier compaction logic; which entries survive, which are evicted |
| `Chunker.fs` | `TotalRecall.Core.Chunker` | Semantic chunking of file content with overlap |
| `Parsers.fs` | `TotalRecall.Core.Parsers` | Language-specific content parsers (Markdown, code) feeding into Chunker |

---

## Key Invariants

1. **No I/O** — if you find yourself opening a file or making a network call from Core, stop. Move it to Infrastructure.
2. **No mutable state** — all types are F# records or discriminated unions. Mutation lives in Infrastructure.
3. **No `async`** — Core functions are synchronous pure computations. Async wrappers live in Infrastructure callers.
4. **AOT-safe** — no reflection, no `typeof<_>` tricks that break NativeAOT. F# discriminated unions and records are fine.

---

## Domain Types (`Types.fs`)

```fsharp
// Tiers — used as dispatch keys everywhere
type Tier = Hot | Warm | Cold

// Content types — determines which table pair (hot_memories vs hot_knowledge)
type ContentType = Memory | Knowledge

// Core entry record — mirrors the SQLite content table columns
type Entry = {
    Id: string
    Content: string
    Summary: string option
    // ... (see Types.fs for full shape)
    Tier: Tier
    ContentType: ContentType
    DecayScore: float
    // ...
}
```

C# callers access these via `TotalRecall.Core.Tier.Hot`, `TotalRecall.Core.ContentType.Memory`, etc.
For discriminated union case checks from C#: `tier.IsHot`, `tier.IsWarm`, `tier.IsCold`.

---

## Decay (`Decay.fs`)

```fsharp
// Half-life is configurable (default: 168h = 1 week)
val computeScore : halfLifeHours:float -> lastAccessedAt:DateTimeOffset -> accessCount:int -> float

// Threshold checks (thresholds from config)
val shouldDemote  : warmThreshold:float    -> score:float -> bool
val shouldPromote : promoteThreshold:float -> score:float -> bool
```

Called by `SessionLifecycle` during warm sweep and compaction.

---

## Ranking (`Ranking.fs`)

```fsharp
// Merge and re-rank vector + FTS results into a unified list
val mergeResults :
    vectorResults : (string * float) list ->   // (entryId, cosineScore)
    ftsResults    : (string * float) list ->   // (entryId, bm25Score)
    topK          : int ->
    (string * float) list                      // merged (entryId, combinedScore)
```

`HybridSearch` in Infrastructure calls this after fetching both result sets from SQLite/Postgres.

---

## Tokenizer (`Tokenizer.fs`)

```fsharp
// Count tokens (BERT WordPiece approximation — exact enough for budget enforcement)
val countTokens : text:string -> int

// Truncate text to fit within a token budget
val truncateToTokens : maxTokens:int -> text:string -> string
```

Used by `SessionLifecycle` for hot-tier token budget enforcement (default: 4000 tokens).

---

## Chunker + Parsers (`Chunker.fs`, `Parsers.fs`)

```fsharp
// Chunk a file's content into semantically coherent segments
val chunk :
    content      : string ->
    filePath     : string ->   // used to pick the right parser
    maxTokens    : int    ->   // from config: tiers.cold.chunk_max_tokens
    overlapTokens: int    ->   // from config: tiers.cold.chunk_overlap_tokens
    string list              // list of chunk texts
```

Parser dispatch is based on file extension from `filePath`. Add new language parsers in
`Parsers.fs` by extending the match expression, then wire through `Chunker.chunk`.

---

## Compaction (`Compaction.fs`)

```fsharp
// Decide which hot-tier entries survive compaction and which are evicted to warm
val planCompaction :
    entries          : Entry list ->
    maxEntries       : int ->      // from config: tiers.hot.max_entries
    tokenBudget      : int ->      // from config: tiers.hot.token_budget
    carryThreshold   : float ->    // from config: tiers.hot.carry_forward_threshold
    CompactionPlan               // { Keep: Entry list; Evict: Entry list }
```

`SessionLifecycle.RunCompactionAsync()` calls this, then issues the actual demote writes
via `IStore`.

---

## Calling F# from C#

F# modules compile to static classes. F# `option<T>` compiles to `FSharpOption<T>`.

```csharp
// Option check
if (FSharpOption<string>.get_IsSome(cfg.Storage.Value.Mode))
{
    var mode = cfg.Storage.Value.Mode.Value;
}

// Discriminated union checks
if (tier.IsHot) { ... }
if (contentType.IsMemory) { ... }

// Static module function call
var score = TotalRecall.Core.Decay.computeScore(halfLifeHours, lastAccessedAt, accessCount);
var chunks = TotalRecall.Core.Chunker.chunk(content, filePath, maxTokens, overlapTokens);
```

**FSharp.Core dependency**: Infrastructure and Server reference `FSharp.Core` NuGet package to
access `FSharpOption<T>` and other F# runtime types from C#.

---

## Adding to Core

Before adding new logic here, confirm it is truly pure (no I/O, no mutation). If it needs
a database read or file access, it belongs in Infrastructure.

Steps:
1. Add the pure function(s) to the appropriate `.fs` file (or create a new one).
2. Add the new `.fs` file to the `<Compile>` list in `TotalRecall.Core.fsproj` **in dependency order** (F# requires top-down declaration order).
3. Write Expecto tests in `tests/TotalRecall.Core.Tests/`.
4. Call from Infrastructure as needed.
