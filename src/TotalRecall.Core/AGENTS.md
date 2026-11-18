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
| `Config.fs` | `TotalRecall.Core.Config` | TOML config record types (`TotalRecallConfig`, `TierConfig`, `EmbeddingConfig`, …); the optional `[git_sync]` settings (`GitSyncConfig`, `RawGitSync`) and the pure `GitSyncSettings.normalise` that turns raw values into a usable config plus warnings; `isUsableBranch` refuses the name `HEAD` (`git check-ref-format --branch` rejects it, so the rule must too) |
| `Tokenizer.fs` | `TotalRecall.Core.Tokenizer` | BERT BasicTokenization + WordPiece; counts tokens for budget enforcement |
| `Decay.fs` | `TotalRecall.Core.Decay` | Exponential decay scoring; `computeScore`, `shouldDemote`, `shouldPromote` |
| `Ranking.fs` | `TotalRecall.Core.Ranking` | Hybrid search result merging and re-ranking (vector + FTS scores) |
| `Compaction.fs` | `TotalRecall.Core.Compaction` | Hot-tier compaction logic; which entries survive, which are evicted |
| `Chunker.fs` | `TotalRecall.Core.Chunker` | Semantic chunking of file content with overlap |
| `Parsers.fs` | `TotalRecall.Core.Parsers` | Language-specific content parsers (Markdown, code) feeding into Chunker |
| `SyncStamp.fs` | `TotalRecall.Core.SyncStamp` | Git sync: wire timestamps (`yyyy-MM-ddTHH:mm:ss.fffZ`), `MachineId`, `Stamp` (`<timestamp>.<6-digit counter>.<8 hex>`) and the hybrid logical clock (`Clock.issue` / `observe` / `guardStartup`, 24 h drift guard) |
| `SyncRecord.fs` | `TotalRecall.Core.SyncRecord` | Git sync: the record model (`SyncRecord`, `Placement`, `Origin`) and the lower-case wire names of `Tier`, `EntryType`, `SourceTool` (not the SQLite strings) |
| `SyncValidation.fs` | `TotalRecall.Core.SyncValidation` | Git sync: id and path rules, `validate` for a record read from the repository (a trust boundary), `Reject` / `Note` / `Block`, `checkWritable`, `sanitizeText` |
| `SyncGit.fs` | `TotalRecall.Core.SyncGit` | Git sync: the closed `GitCommand` set (no case can force, delete or mirror), address, branch and revision rules (`classifyRemote`, `build`), the `-c` configuration overrides, the `GIT_*` environment rule, `composeSshCommand`, `classifyFailure`, credential `redact` / `cleanStderr`, `cloneKey` / `dbKey`, `parseLsRemote`, `parseLsRemoteRefs` (the `LsRemoteRefs` command lists every ref of a remote), and the two push commands `StagePaths` (`add -f -- <paths>`, valid only for a non-empty list of exact record paths) and `StagedSet` (`diff --cached --name-status -z --no-renames`). The git client is `Infrastructure/GitSync/GitClient`; the hidden `sync` commands use it through `SyncSetupService` |
| `SyncTree.fs` | `TotalRecall.Core.SyncTree` | Git sync: parsers for `git ls-tree -r -z -l` and `git diff-tree -r -z --no-renames --raw`, and the per-entry verdicts (`classifyRecordEntry`, `classifyDiffEntry`: symlink, submodule, bad path, bad shard, bad id, too large). Nothing calls it yet |
| `SyncManifest.fs` | `TotalRecall.Core.SyncManifest` | Git sync: the repository's `manifest.json` rules: `classifyFormat` (`Supported` for `total-recall/v1`, `Newer n` for a higher version of at most 9 digits, else `Unrecognised`), `isValidRepoId` (32 lowercase hex digits), `render` (the exact single-line bytes), `MaxManifestBytes` (4 KiB) and `MaxLookBytes` (64 KiB). Parsing the JSON is `GitSync/ManifestFile.cs`. |
| `SyncIdentity.fs` | `TotalRecall.Core.SyncIdentity` | Git sync: the identity rule. `evaluate` returns `Joined` or `NotJoined reason` (`NotConfigured`, `NeverJoined`, `RemoteChanged`, `DataDirectoryMoved`, `RepositoryReplaced`; the first failing check decides, an unavailable host fingerprint or unknown manifest id skips its check), `reasonText`, the `_meta` key names (`MetaKeys`, including the clock keys `Hlc` = `git_sync_hlc` and `ClockWarning` = `git_sync_clock_warning` the scan writes) and `hostFingerprint` (SHA-256 of the OS machine id and the canonical database path, never the hostname). |
| `SyncSetup.fs` | `TotalRecall.Core.SyncSetup` | Git sync setup rules for the hidden `sync init`/`join`/`leave`/`status` commands: the `Refusal` set and its wire names (`refusalName`), `effectiveMode`/`modeVerdict` (storage mode: only `local` or `git` may set up), the address rules (`addressUserInfo` finds a credential, `addressRefusal` refuses a password, token, `?` or `#`; `displayRemote` masks userinfo), `isSameRepository` (repository id and branch decide), `keepMachineId` (kept only when the host fingerprint matches), `newRepoId`/`newMachineId` (32 and 8 hex digits from random bytes), `classifyRefs` (an empty remote, one holding only the branch, or something else) and `renderStatus` over `StatusFacts` (`Fact`: known or unknown or missing; `HostCheck`). |
| `SyncPublic.fs` | `TotalRecall.Core.SyncPublic` | Git sync: the public-repository check's pure side: `parseGitHubSlug` (host after the last `@`, refuses any authority with `\`, `?`, `#`, `%` or whitespace), `ofStatus` (200 `Public`, 404 `Private`, else `Unknown`), the cache value (`formatCache`, `parseCache`, `isFresh`: 24 h, at most 5 minutes in the future) and the two notices. |
| `SyncSettingsGuard.fs` | `TotalRecall.Core.SyncSettingsGuard` | The denylist of `config set` / `config_set`: `isProtectedKey` is true for `git_sync` (any depth), `storage`, `storage.mode`, `storage.connection_string` and what is beneath them, and for a key with an empty segment; segments are trimmed and compared case-insensitively. |
| `SyncMerge.fs` | `TotalRecall.Core.SyncMerge` | Git sync: `decide : MergeInput -> MergeDecision`, the pure per-entry merge (whole-entry table, no-base and field-wise group rules, own-push recognition, per-entry deletion rows); the input, decision, review-request and note types; `clearsPending`. Nothing calls it yet (the cycle is milestone 1b) |
| `SyncPush.fs` | `TotalRecall.Core.SyncPush` | Git sync, the push (milestone 1b-i-3): the pure rules of what is pushed and written. `isCandidate` (a row with no base, a pending group or a tags change), `exclusion` (the reasons an entry is left out, first match wins: issue, rejected-record, not-reconciled, record-missing, record-unreadable, unreadable-base, path-occupied, blocked-elsewhere, unreadable-entry) with `exclusionName`, `pathOccupied` (a listed path in the way of a record, compared without case, independent of the host's file system), the block marks (`writerReasons`, `isWriterReason`, `blockName`, `markAction`), `planChanged` / `GroupPlan` (a group is written only with a pending stamp greater than the record's; `Stale` names the others), `sourceChange`, `newRecord`, `afterPush` (a pending stamp is cleared only when it equals the pushed one), `Published` / `publishedAfterPush`, `commitMessage` (always ends `-0`), `machineLabel` and `parseStagedSet` / `stagedSetMatches` (the staged files must be exactly the written files). Pure and total: no I/O, no clock. The only caller will be the cycle (1b-i-4) through `GitSyncPusher` |
| `SyncApply.fs` | `TotalRecall.Core.SyncApply` | Git sync, the pull (milestone 1b-i-2): the pure rules of the apply. `postponement : MergeDecision -> PostponeReason option` (`Deletion`, `Review`, `Repush`, in that order: the decisions this milestone does not carry out, which change nothing and hold `L`), `reasonName`, the `PushingStamps` codec for the `pushing_stamps` column text (`decode` reads `NULL`, empty, `{}`, `[]` and any unreadable text as none; `encode`), `rejectionKey` (`sanitizeText` of the path: the id of a `rejected-record` issue), `demotesToWarm` (the hot-tier content limit: a limit of 0 or less is no limit; an insert ignores the record's sticky flag), `visitSet` (the entries to decide, ordinal order, from the sets of decision 5 of the pull spec) and `planRow : MergeDecision -> RowBefore -> ... -> Result<RowAfter option, string>` (the row an entry has after a decision: state, pending stamps, `pushing_stamps`, the comparison columns from the stored entry; `Ok None` writes no row; the stamp for `RestampPending` comes from a function the caller passes). Pure and total: no I/O, no clock. |
| `SyncScan.fs` | `TotalRecall.Core.SyncScan` | Git sync, the scan (milestone 1b-i-1): `contentHash` (SHA-256 of a versioned prefix and the six content fields, each `0x00` when absent or `0x01` + 4-byte big-endian length + UTF-8) and `tagsHash` (count and length-prefixed normalised tags), the two hashes a `git_sync_entry` row stores in `scanned_content_hash` and `scanned_tags_hash` (the apply must set them from the stored entry with these functions); `decide : RowView option -> LocalEntry option -> bool -> ScanAction` (`NoChange`, `NewRow`, `Reactivate`, `UpdateRow of RowChanges` with a `GroupAction` per group: `Unchanged`, `StoreValue`, `IssueStamp floor`, `DropPendingStamp`; `MoveToLeft`; `ReportDeletion`), pure and total; `floorStamp` (the greater of two optional stamps) and `ClockWarning` (`Kind`: `ClockReset`, `ClockUnreadable`, `FarFuture`, `Exhausted`; `addKinds` builds the `clock: ` + sorted kind names text and returns `None` when nothing is added). |

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
type Tier = Hot | Warm | Cold | Pinned

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
