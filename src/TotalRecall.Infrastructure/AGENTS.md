# TotalRecall.Infrastructure — Agent Guide

The infrastructure layer owns all side-effects: storage, embedding, importers, ingestion,
telemetry, search, and Cortex sync. Pure logic lives in `TotalRecall.Core` (F#).

**AOT requirement**: `IsAotCompatible=true` and `InvariantGlobalization=true` are set in the csproj.
No reflection, no dynamic code generation. All JSON via source-generated `JsonContext`.

---

## Subsystem Map

| Directory | Interfaces / Key Types | Purpose |
|-----------|----------------------|---------|
| `Storage/` | `IStore`, `SqliteStore`, `PostgresStore`, `RoutingStore` | CRUD for memory/knowledge entries across tiers |
| `Embedding/` | `IEmbedder`, `OnnxEmbedder`, `RemoteEmbedder`, `EmbedderFactory` | Embedding generation (local ONNX or remote API) |
| `Search/` | `IVectorSearch`, `IHybridSearch`, `IFtsSearch`, `VectorSearch`, `HybridSearch` | Vector + FTS hybrid retrieval |
| `Importers/` | `IImporter`, `ClaudeCodeImporter`, `CopilotCliImporter`, … | One-way import from host tool memory dirs |
| `Ingestion/` | `IFileIngester`, `FileIngester`, `HierarchicalIndex`, `IngestValidator` | File/dir ingestion into cold KB tier |
| `Telemetry/` | `CompactionLog`, `UsageEventLog`, `UsageDailyRollup`, `UsageWatermarkStore` | Retrieval events, compaction history, usage telemetry |
| `Usage/` | `IUsageImporter`, `UsageIndexer`, `UsageQueryService` | Token usage tracking and query |
| `Sync/` | `CortexClient`, `SyncQueue`, `SyncService`, `PeriodicSync`, `RoutingStore` | Bidirectional Cortex sync |
| `GitSync/` | `RecordReader`, `ParsedRecord`, `RecordWriter`, `RecordPatcher` | Git sync record codec (not wired to any sync yet): strict reader (1 MiB cap, duplicate-key and encoding pre-pass), hand-written canonical writer (byte-stable, unknown content kept), patcher. Wire names are lower-case, not the `EntryTypeMapping` DB strings |
| `GitSync/` (git client) | `GitProcess`, `GitClient` (+ `GitClient.Write.cs`), `GitSyncPaths`, `GitOutcome`, `SyncLock` | Git sync transport (used by the hidden `sync` commands; the cycle is milestone 1b): see "Git sync transport" below |
| `GitSync/` (setup) | `SyncSetupService` (partial: `.cs`, `.LeaveStatus.cs`, `.Init.cs`, `.Record.cs`, `.Join.cs`, `.Unblock.cs`), `GitSyncStateStore`, `ReadOnlyDatabase`, `ReadOnlyMeta`, `LegacyDatabaseProbe`, `CloneSideline`, `UserConfigFile` | The hidden `sync init`/`join`/`leave`/`status`/`unblock` commands' service (`Cli/Commands/Sync/`): see "Git sync setup commands" below |
| `GitSync/` (cycle) | `GitSyncCycle` (partial: `.cs`, `.Attempt.cs`, `.Clone.cs`, `.Push.cs`), `CycleSettings`, `CycleSummary`, `CycleOutcome`, `IdentityCheck` (in `GitSyncFetcher.cs`) | The cycle: one `RunAsync(ct)` that takes the sync lock and runs the identity check, the stale-lock removal, the scan, the clone check, the fetch, the apply, the push gate and the push, then records the result (see "Git sync cycle" below). Nothing calls it until 1b-iii has merged |
| `GitSync/` (push) | `GitSyncPusher` (partial: `.cs`, `.Prepare.cs`, `.Plan.cs`, `.Write.cs`, `.Finalize.cs`) | The push: write this machine's changed memories into the clone (and, from 1b-ii, remove the records of proven local deletions, write back a record a text edit saved and republish reverted groups; from 1b-iii part 1 it re-checks each entry for a secret first), commit once, push without force, finalize the rows (see "Git sync push" below). The only caller is the cycle (1b-i-4), which nothing calls until 1b-iii has merged |
| `GitSync/` (pull) | `GitSyncFetcher`, `PullTree`, `GitSyncApplier` (partial: `.cs`, `.Entry.cs`, `.Issues.cs`, `.Pull.cs`), `LocalEntryReader`, `BaseRenderer`, `GitSyncIssues`, `GitSyncReviews` | The pull: fetch the repository and apply other machines' memories, deletions included (see "Git sync pull" below). Only the cycle (1b-i-4) calls it, and nothing calls the cycle until 1b-iii has merged |
| `GitSync/` (scan) | `GitSyncRow` + `GitSyncRows` (typed `git_sync_entry` access: `ReadAll`, `Read`, `Write` upsert, `Delete`, each with an optional `SqliteTransaction` for the pull), `GitSyncScanner` (partial: `.cs`, `.Plan.cs`, `.Write.cs`, `.Secrets.cs`) | The scan: stamps local memory changes into `git_sync_entry` and the secret block of 1b-iii part 1 (see "Git sync scan" below). Only the cycle (1b-i-4) calls it, and nothing calls the cycle until 1b-iii has merged |
| `GitSync/` (guards) | `ManifestFile`, `HostIdentity`, `MachineIdReader`, `GitSyncIdentityStore`, `PublicRepoProbe`, `RepoGuard` (+ `RepoGuard.Public.cs`) | Git sync guards (the cycle exists but nothing calls it yet): manifest codec, host fingerprint, the stored identity, the anonymous GitHub probe and `RepoGuard` (`EvaluateIdentityAsync`, `ReadManifestAsync`, `CheckPublicAsync`). See "Git sync guards" below. |
| `Config/` | `ConfigLoader`, `TomlConfig` | TOML config loading + `~/.total-recall/config.toml` |
| `Diagnostics/` | `ExceptionLogger` | AOT-safe exception chain logging |
| `Migration/` | `MigrationRunner`, `PostgresMigrationRunner` | Sequential schema migration framework |
| `Json/` | `JsonContext` (Infrastructure-level) | Source-generated JSON for Infrastructure DTOs |
| `Memory/` | Memory-specific helpers | Memory operation utilities |
| `Eval/` | Eval query services, `RegressionDetector` / `RegressionOptions` | Retrieval quality metrics; session-start regression detection (see "Regression detection" in the root `AGENTS.md`) |

---

## Storage Layer

### IStore

```csharp
interface IStore
{
    Task<Entry?> GetAsync(string id, ...);
    Task<IReadOnlyList<Entry>> ListAsync(Tier tier, ContentType type, ...);
    Task StoreAsync(Entry entry, ...);
    Task UpdateAsync(string id, ...);
    Task DeleteAsync(string id, ...);
    Task MoveAsync(string id, Tier targetTier, ...);  // for promote/demote
}
```

**All-or-nothing transaction (git sync groundwork).** `SqliteStore.BeginTransaction()` returns a
`SqliteStoreTransaction` (not on `IStore`) offering insert with its vector, move between tiers, delete,
update, `SetSticky` (hot only) and an `import_log` write as one unit: `Commit()` makes them all take effect,
`Dispose()` without `Commit()` rolls everything back. Embeddings are computed by the caller before it opens.
It runs on the store's single connection, so while one is open no other code may use that connection
(Microsoft.Data.Sqlite fails a stray command with `InvalidOperationException`), and a second
`BeginTransaction()` on the same store throws. The statements live in `Storage/SqlRows.cs` (`SqlRows`,
`VecRows`), shared with `SqliteStore`'s own methods. An internal `BeforeStep` seam lets tests inject a
failure at a named step. Nothing in production uses it yet (git sync does, in later milestones).

**Git sync cycle (`GitSync/GitSyncCycle*.cs`; rules in `Core/SyncCycle.fs`).** `GitSyncCycle(GitClient, RepoGuard, SqliteStore, IEmbedder, GitSyncPaths, CycleSettings(Address, Branch, MachineLabel, HotMaxContentChars, LockWait, DeleteGuardPercent = 20), Func<DateTimeOffset> clock, Func<long> nowMs).RunAsync(ct)` returns a `CycleOutcome`: `Done(CycleSummary)`, `NotJoined`, `Refused(reason, step)`, `Busy`, `Failed(step, kind, message, published)` or `Cancelled`; it never throws for the state of the store, the repository, the file system or the network, and prints nothing (the one stderr line is the `ExceptionLogger.LogChain` of an unexpected exception). The constructor throws when `Address` is empty, is not the guard's `ConfiguredRemote`, or the paths' clone directory is not the client's. One instance should live as long as the process (the damaged-clone state is per instance). Order, all under the sync lock: **identity** (`GitSyncFetcher.CheckIdentityAsync`: `NotJoined` records nothing, `Refused` is recorded; it runs before the scan so a copied data directory never issues stamps), **locks** (`RemoveStaleLocks`, best effort), then an *attempt*: **scan**, **clone** (a missing clone is cloned; a directory that is not a sound clone is moved aside with `CloneSideline.Move(.., "broken", ..)`, never deleted, and cloned again), **fetch** (a failed fetch of kind `NotARepository`, or `Other` / `RemoteNotFound` followed by a failing local probe, is damage: replace the clone and restart from the scan), **apply**, the **gate** (`SyncCycle.pushDecision`: an incomplete pull ends `Done` with `PushSkipped = "pull-incomplete"` and no push) and the **push** (a non-fast-forward restarts from the scan, at most three pushes per cycle; a push `Failed("Clone")` followed by a failing probe is damage too). **Once per process** the cycle moves one damaged clone aside; the second damaged clone gives `Failed(clone, CloneUnusable)` and is kept, so every later `RunAsync` on the instance returns it at once, without the lock, a read or a write. A clone that failed or was cancelled leaves a partial directory that is deleted (or moved aside as `partial`) and never counts. **Recording** (the last act under the lock; `OnStep("record")` fires only when something is written): `Done` deletes `git_sync_last_error` and then sets `git_sync_last_ok` (also for an incomplete pull: nothing failed); `Failed` and `Refused` set `git_sync_last_error` to `SyncCycle.errorLine(...)`; `Busy`, `NotJoined` and `Cancelled` write nothing; a failed write is swallowed. A cancel is decided from the token after each part returns, except that once the push command has succeeded the cycle is `Done`. Test seams: `OnStep(step)` (`lock`, `identity`, `locks`, `scan`, `clone`, `fetch`, `apply`, `push`, `record`), `BeforeMetaWrite(key, value)`, `DeleteDirectory`, `MoveDirectory`. Known limit: a partial clone directory that can be neither deleted nor moved aside stays, can hold a `.git` that passes the sound-clone check, and then counts as damage on the next cycle. Nothing calls the cycle until 1b-iii (the secret scan) has merged, because it calls the push; a source scan test (`GitSyncCycleCallerTests`) pins that nothing outside `GitSyncCycle*.cs` names it and nothing outside the pusher and the cycle names the pusher. The cycle reads no configuration and has no command; the caller (1b-iii `sync now`, milestone 2 timer and session hooks) builds the settings (`DeleteGuardPercent` is passed to the applier's `ApplyOptions`; the caller takes it from `[git_sync] delete_guard_percent`). Held items, applied deletions and repush markers never make a pull incomplete (milestone 1b-ii), so the push runs with them in play. Tests: `CycleRig` (one machine) and `TwoMachineRig` (two machines, each with its own database, clone and clocks, on one bare remote) in `tests/TotalRecall.Infrastructure.Tests/GitSync/`; the deletion scenarios across two machines are `TwoMachineTests` and `GitSyncCycleDeletionTests`.

**Git sync push (`GitSync/GitSyncPusher*.cs`; rules in `Core/SyncPush.fs`).** `GitSyncPusher(GitClient, SqliteStore, branch, machineLabel).PushAsync(apply, scan, ct)` returns `PushOutcome`: `Pushed`, `NothingToPush`, `NotReady`, `NonFastForward` or `Failed(kind, message, published)` (`published` is `No`, `Yes` only after the push command succeeded, or `Unknown`). The caller holds the sync lock and passes the summaries of the scan and a **complete** pull; only the cycle calls it (and its tests), and nothing calls the cycle until a later part of 1b-iii adds `sync now`. Order: **prepare** (readiness: `apply.Complete` and `_meta` last sha equal to `apply.Head`; write `.git/info/attributes` with every filter off, then `reset --hard L` and `clean -fd`, `HEAD` must be `L`; read the tree at `L` as git objects with `PullTree`; select candidates and decide the exclusions), **plan** (one `BEGIN IMMEDIATE` transaction before any file is written: re-read each row and entry, defer an entry that no longer agrees with its row (`changed-since-scan`), build the record (a new record, or a patch of the record at `L` through `RecordPatcher`), check the limits (`SyncValidation.checkWritable`), write the block marks and the `pushing_stamps` of the entries that will be written; a failure rolls it back whole), **write** (temporary file under `.git`, renamed over the record path; `StagePaths` in batches of 100; the staged set must be exactly the written paths (added or modified) and the removed paths (deleted); one commit `sync: <label> +<new> ~<changed> -<removed>`; `push` without force; any failure up to the push command resets and cleans the clone; a non-fast-forward leaves it as it is and the next prepare resets it), **finalize** (one transaction: `base_json`, the cleared pending stamps, `pushing_stamps`, the writer's block marks, then `_meta.git_sync_last_sha`; then the protective ref; the token is never consulted after the push command succeeded). The comparison columns are left alone so an edit after the scan is still seen. Test seams: `BeforePlanTransaction`, `BeforeStep(id, step)` (`reread`, `plan`, `pushing`, `block`, `write`, `stage`, `commit`, `finalize`, `meta`, `ref`), `BeforePush`, `TransactionCount`. The cycle (1b-i-4) removes a stale `.git/*.lock` under the lock before it runs and does not push after a `Refused`, `Failed` or incomplete pull; only a review postponement (answered in 1b-iii), a deferral or a cancellation makes a pull incomplete. **Deletions and repair (milestone 1b-ii).** *Removals* (D14): each id of `apply.PushDeletions` that is still one of the scan's `LocalDeletions` and whose record at `L` is accepted, unobstructed and not named by a rejected path (`SyncPush.removable`) has its record file deleted and staged as a removal in the same commit; any other is listed in `Skipped` as `removal-not-possible` and left alone. A push with removals only is a push, the staged-set check expects each removed path once as a deletion, and `PushSummary.Removed` lists them. *Write-back* (D15): a candidate with a base, no accepted record at `L` and a pending content stamp (a text edit beat a remote deletion) is written as a new file from its `base_json` patched with the entry's groups and the fresh stamp (counted as `+`; unknown fields of the base survive). *Repush* (D16): a row with a `repush_groups` marker is a candidate even with nothing pending; each flagged group with no pending stamp is set, in the record at `L`, to the base's value and stamp (`SyncPush.repushPlan`, no clock call; nothing local-only such as a declined pin or a local demotion enters the record), the base's stamp goes into `pushing_stamps`, and the finalize clears the marker (a failed or non-fast-forward push keeps it; a repush whose record already says what the base says writes nothing and clears it). A base whose `source` this version cannot parse keeps the repository record's source in a repush. *Held entries* (D17): a candidate with an open `held-delete` item (read with `GitSyncReviews` when the push prepares) is excluded as `held-delete` and writes nothing, so a tag, pin or promotion cannot write a vanished record back. `record-missing` now covers only a row with a base, no record and no pending content stamp (defensive after a complete pull); `not-reconciled` is unchanged. *Finalize* (D14): after a pushed commit each removed id's `active` row becomes `forgotten` in the same transaction (base kept; pending stamps, `pushing_stamps` and `repush_groups` cleared), even when a memory with that id exists again (the next scan reactivates it and the next push republishes it). A kill after the push command and before the finalize is repaired by the next apply (record absent, entry gone, base present: the row becomes `forgotten`), with no second commit. Test seams gain the `forget` step. **Secret re-check (milestone 1b-iii part 1, #161, spec D5).** The scan runs before the apply, and the apply can write text or tags the scan never saw (a merged tag union; a restore, `swap` or `merge` later), so the push checks again. The **preparation** (outside any transaction) runs `SecretDetector` (default `SensitiveContentScanner.ScanSecrets`) over the text, project, scope and each tag of every candidate that is not excluded, guided by `SyncSecret.precheck`, and keeps a `SecretCheck(Key, Hit)` per candidate; a candidate whose row already carries `secret-detected` is excluded and listed in `Skipped` with the reason `secret-detected` (other non-writer reasons keep `blocked-elsewhere`). `PlanOne` only compares keys inside the plan transaction (the detectors never run while `BEGIN IMMEDIATE` is held): a key that differs from the prepared one defers the entry as `changed-since-scan`, a candidate with no prepared key is deferred (`unreadable-entry` when the re-read is absent, else `changed-since-scan`), and a hit that is not allowed writes the row with `blocked_reason = 'secret-detected'` and `blocked_hash` = the key in the same transaction, lists the id in `Blocked` with that reason and builds no record, patch or write-back for it. **The pusher applies only the Block outcome**: it never clears a secret mark or reads `allowed_hash` except to let an allowed entry through, and `secret-detected` is not a writer reason, so `SyncPush.markAction` and the finalize's own-mark test leave it. No record holding a token or key is produced by the pusher, whatever produced the text.

**Git sync pull (`GitSync/GitSyncFetcher.cs`, `PullTree.cs`, `GitSyncApplier*.cs`, `LocalEntryReader.cs`, `BaseRenderer.cs`, `GitSyncIssues.cs`, `GitSyncReviews.cs`; rules in `Core/SyncApply.fs`, `Core/SyncMerge.fs` and `Core/SyncDeletion.fs`).** Two classes, called in order by the cycle (milestone 1b-i-4; nothing else calls them, and nothing calls the cycle until 1b-iii has merged).
- **`GitSyncFetcher.FetchAsync`** (cycle step 2) returns `FetchOutcome`: `Fetched` / `NothingNew` (a `FetchResult(Head, Last, Proven, NothingNew)`), `Refused(reason)` or `Failed(kind, message)`. It never writes the store. Order: read `L` (`git_sync_last_sha`, only when 40 or 64 lowercase hex), the identity half of the guard (join state and host identity, no clone and no network), `ls-remote`, then either `NothingNew` (the head equals `L` **and** the clone holds `L`: no fetch and no reset, the full guard runs at `L`) or `fetch`, `R` from `rev-parse` of the tracking ref, the full guard at `R`, the proof state, and only then `reset --hard R`. **Proven** means there is an `L`, `L` is an ancestor of `R` (or equal) and `refs/total-recall/last-synced` equals `L`; a ref that is a strict ancestor of `L` is repaired in place (`UpdateLastSyncedAsync`); a `Tri.No`, `Tri.Unknown`, missing or elsewhere ref is just unproven; a git call that gives no answer is `Failed`. A manifest that could not be read because a git call failed or was cancelled is `Failed`, never `Refused`.
- **`PullTree.ReadAsync`** reads the tree at `R` as git objects (`ls-tree`, `diff-tree`), never the working tree: the listing alone decides what is present and what is rejected (`SyncTree.classifyRecordEntry`; a rejection names an id only for the exact path `memories/<shard>/<id>.json`); the diff, only when proven, gives the ids whose record changed.
- **`GitSyncApplier.ApplyAsync(fetch, scan, ct)`** returns `ApplyOutcome`: `Done(ApplySummary)` or `Failed(reason)` (the listing, the rows or the `L` writes failed). It builds the visit set (`SyncApply.visitSet`, united with the scan's skipped ids), decides each id in ordinal order with `SyncMerge.decide`, and applies each in **one `BEGIN IMMEDIATE` transaction** (`ApplyOne`): re-read of the row and the entry (any difference from the decision's inputs, including an edit the scan has not stamped, is a **deferral**), the store operations, the `git_sync_entry` row (comparison columns from the stored entry, so a scan right after stamps nothing), the `import_log` row for an insert (`git-sync`, `sync:<id>`) and the removal of the entry's `apply-failed` row. A busy database defers and **stops** the apply (the rest is counted `NotAttempted`). Only a decision that opens a review item (a pin, a pin edit, a conflict; answered in 1b-iii) is **postponed** (`SyncApply.postponement`, reason `review`): nothing is written and `L` is held back. **Deletions (milestone 1b-ii).** Before the first entry transaction a **preview** decides every deletion candidate (a visited id with no accepted record and no rejected path, or a local deletion whose accepted record parses; never a scan-skipped id) with the same input as the real decision, open held item included, writes nothing, and counts `DeleteEntry` as remote and `PushDeletion` as local; the limit is `SyncDeletion.guardLimit(ApplyOptions.DeleteGuardPercent, active rows at the start of the pull)` (`ApplyOptions(HotMaxContentChars, DeleteGuardPercent = 20)`), and a direction over it has each of its deletions turned into a hold with the cause `guard` (`ApplySummary.GuardTripped` gives the count and limit). The carry-outs, each inside the entry's transaction: a `DeleteEntry` deletes the content and vector rows and makes the row `forgotten` (base kept; pending stamps, `pushing_stamps` and `repush_groups` cleared); a `HoldDeletion` upserts one open `held-delete` item through `GitSyncReviews` (direction `remote` or `local`, cause `guard`, `unproven` or `sticky`, the record's blob id in R (NULL for a `remote` hold), `arrived_at` the applier clock; a local hold also clears the pending stamps; the entry stays); a `CloseHeldDelete` deletes the item first, so a decision that closes and holds leaves exactly one open item; a `PushDeletion` writes nothing to the store and lists the id in `PushDeletions` for the push; a `LeaveEntry` with `FreshContentStamp` (a text edit beat a remote deletion) issues a new pending content stamp at the `row` step; a `MergeGroups` replaces the row's `repush_groups` with its repush flags (`SyncApply.repushMarker`). The open items are read once per pull (`GitSyncReviews.ReadOpenHeld`) and passed to `decide` as `HeldDelete`. A local deletion whose record in `R` is rejected or accepted by path but invalid is **skipped** (`Skipped`: `the record is rejected` / `the record is invalid`): never decided, counted or written, and it does not hold `L`. **Unproven fallback hold:** in an unproven pull a deletion candidate of a row that was `active` with a base (a vanished record whose entry exists, or a local deletion with an accepted record) whose entry ended failed, skipped or scan-skipped, so that its own transaction wrote no hold, gets an open `unproven` item in a small transaction of its own after that (`hold-unproven` step; an open item is never replaced); if that write fails the entry is deferred and `L` stays, so a later proven pull can never delete it. `ApplySummary` adds `Deleted`, `Held` (id, direction, cause), `GuardTripped`, `PushDeletions`, `ClosedHeld`, `Notes` (`edit-beats-deletion`, `pending-changes-dropped`) and `RepushMarked` (id, groups). `GitSyncReviews` (`ReadOpenHeld`, `ReadOpenHeldItem`, `UpsertHeld`, `DeleteHeld`, each with the caller's transaction) touches only `held-delete` rows of `git_sync_review`; `payload_json` is `{"cause":"<cause>"}`. `L` (`git_sync_last_sha`, then `refs/total-recall/last-synced`) advances only for a complete pull (every id processed, nothing postponed or deferred). `apply-failed` rows record a failed entry (attempts, blob id, a one-line detail) and also `scan-skipped:` / `unreadable:` entries that were not applied (no attempt counted; never overwrite a real failure); `rejected-record` rows are keyed by `sanitizeText(path)` and swept at the end of a pull that processed every id. A rejected path is never read as a deletion (and a local deletion with one is skipped, above). `GitSyncIssues` (`apply-failed` and `rejected-record` rows: `ReadOpen`, `Read`, `Upsert`, `Delete`) and `SyncSql` (`GetMeta`/`SetMeta` on a given transaction: `SqliteStore.GetMeta` has none and throws while one is open) are the plain SQL the apply uses. Test seams: `BeforeEntry`, `BeforeTransaction`, `BeforeEntryStep(id, step)` (the store transaction's own steps plus `reread`, `row`, `close`, `hold`, `hold-unproven`, `issue`, `commit`, `write-issue`), `TransactionCount`. **Store groundwork the apply uses:** `SqliteStore.BeginImmediateTransaction`, `SqliteStoreTransaction.ReplaceEmbedding`, and `UpdateEntryOpts.SourceTool` / `ClearSourceTool` / `CreatedAt` (Postgres refuses them). A null vector array must stay "no vector" (`Memory(...)`): the implicit array-to-memory conversion would make it an empty one.

**Git sync scan (`GitSync/GitSyncScanner*.cs`, `GitSyncRows.cs`; rules in `Core/SyncScan.fs`).** `new GitSyncScanner(connection, nowMs).Scan()` returns `ScanOutcome`: `Done(ScanSummary)`, `NotJoined` (no valid `git_sync_machine`) or `Failed(reason)`. It reads inside **one** read transaction (raw `BEGIN`, first `SELECT` starts the WAL snapshot: the machine id, the clock and warning keys, every `git_sync_entry` row, the `ineligible-id` issues, the hot, warm and cold memory tables, the knowledge ids), decides per id with `SyncScan.decide` in ordinal id order, and writes in **one** `BEGIN IMMEDIATE` transaction (rows, issues, clock, warning). A scan with nothing to write opens no write transaction; two scans in a row with no edit change nothing. It takes no lock, runs no `git`, uses no network and writes only `git_sync_entry`, `git_sync_issue` and the `_meta` keys `git_sync_hlc` and `git_sync_clock_warning` (and removes the `held-delete` item of a row it moves to `left` or reactivates, in the same transaction, so the item cannot outlive the base it was opened for; milestone 1b-ii), never a memory, knowledge, vector, `import_log` or `compaction_log` row. Per group it stamps content (text, project, scope, entry type, source tool, `created_at`), sticky (a pin or unpin) and tier (only a warmer tier; demotion just updates `local_tier`); a tags change updates the tags hash and issues no stamp. Stamps come from `Clock.issue` with the greater of the group's base stamp (read from `base_json`) and its pending stamp as the floor; `Clock.guardStartup` runs once per scan. An id in two tiers, an unreadable entry and an ineligible id are skipped (listed in `Skipped` or `Ineligible`, and never treated as deleted); a group whose clock is exhausted is skipped and retried next scan. An active row whose memory exists nowhere is only **reported** in `LocalDeletions` (with has-base and has-pushing-stamps flags); the scan never drops, forgets or deletes it (1b-ii acts on it); a retype to knowledge moves the row to `left`. Internal test seams: `AfterFirstTableRead`, `BeforeWrite(step)`, `WriteTransactionCount`. What the scan requires of the later parts: the apply sets `scanned_content_hash`, `scanned_tags_hash`, `local_sticky` and `local_tier` from the stored entry after its own write (or the next scan reads the pulled change as a local edit), reaches rows with no base and no entry whose id is absent from the repository, and exempts groups the scan skipped; the push and the apply clear the pending stamps. A stamp column that does not parse reads as none and is written back as NULL when the row is rewritten. **Secret block (milestone 1b-iii part 1, #161, spec D1 to D4, D9, D10).** After the plan is built and before the write transaction, `ApplySecretPass` (`GitSyncScanner.Secrets.cs`) evaluates every eligible entry whose row the plan will write or whose stored row is `active` (skipped and ineligible ids, and `forgotten`/`left` rows without an entry, are not evaluated), so an entry the plan calls `NoChange` is still checked when its key matches neither stored key. The key is `SyncSecret.secretKey` (text, project, scope and normalised tags in one hash); `SyncSecret.precheck` skips the detectors for an allowed (`allowed_hash` = key) or still-blocked (`secret-detected` with `blocked_hash` = key) entry, and otherwise `SecretDetector` (test seam; default `SensitiveContentScanner.ScanSecrets`, the Token and PrivateKey detectors only, so `git@github.com` and plain addresses such as `noreply@example.com` are not blocked) runs over the text, project, scope and each tag. A hit writes `blocked_reason = 'secret-detected'` and `blocked_hash` = the key, replacing any writer reason (such as `too-large`, which the next push re-marks after a clean re-scan lifts the secret mark); no hit lifts a secret mark and keeps any other reason; an allowed entry clears a secret mark. The pass edits the row the plan already holds, so one id gives one row write; `allowed_hash` is never written by the scan (only `FreshRow` starts a new or reactivated row with all three columns empty). `ScanSummary.Blocked` and `Unblocked` list the ids whose secret mark appeared or disappeared. Reasons are category labels (`API token or secret`, `private key block`, `scan timed out`); nothing stores, logs or prints a matched value. **The detector (`Eval/SensitiveContentScanner.cs`, spec D9)** is the union of `Redact`'s token patterns and private-key begin line (a PGP `PRIVATE KEY BLOCK` included), reused from `RedactionPatterns`, and its own earlier patterns kept as legacy entries (the unbounded `sk-[A-Za-z0-9]{16,}`, which also holds back text such as `mask-abcdefghijklmnopqrst`, and `-----BEGIN [A-Z ]*PRIVATE KEY-----`); `Redact` itself, its output and `RedactionVersion` are unchanged. Every pattern is matched with a 1 s timeout (`SecretScanTimeout`; an internal overload takes the bound for tests), and a timeout is a hit (`scan timed out`, fail closed) in `ScanSecrets` and in `Scan`, so `eval_grow`'s accept guard fails closed too. **Cost (D10).** No marker is stored: every scan runs the detectors on each eligible active entry whose key matches neither `blocked_hash` nor `allowed_hash`, which in the common case (a clean entry) is every active entry once per cycle; an entry blocked and unchanged, or allowed and unchanged, is skipped. The work is linear in the text and bounded per pattern by the timeout. Measured for #161 (Linux, Intel Core i5-14500, Debug test build, one throwaway run of three passes, not a committed test): one scan pass over 10,000 clean active entries of 2,000 characters each, with nothing changed since the previous scan, took 350 to 460 ms, of which the detectors took 25 to 29 ms over the 10,000 calls; the first scan of that store (creating every row) took about 1.0 s. Memory text has no single size cap (`memory_store` allows 100,000 characters, other paths none); an entry over the writer's limit is `too-large` for the push anyway.

**Git sync guards (`GitSync/RepoGuard*.cs`, `ManifestFile.cs`, `HostIdentity.cs`, `GitSyncIdentityStore.cs`, `PublicRepoProbe.cs`).**
`RepoGuard.EvaluateIdentityAsync(revision = Head)` reads the settings, the stored identity (`_meta` keys `git_sync_remote`,
`git_sync_repo_id`, `git_sync_host`), the host fingerprint and the repository's manifest from the **local clone** at the given
revision (the cycle passes `refs/remotes/origin/<branch>` after its fetch, when `HEAD` is still the old commit) and returns a
`GuardVerdict`: the `Core.SyncIdentity.JoinState`, the `ManifestRead` outcome (`NoClone` = no `.git` under the clone directory,
`NoManifest`, `Invalid`, `Unreadable` for any git failure or a blob over 64 KiB, `Newer`, `Ok`) and `HostUnverified`.
`GuardVerdict.CanSync` is true only when joined, the host fingerprint was available and the manifest is `Ok` or there is no
clone yet; a caller reports a newer format before `not-joined`. It runs no network command (`Clone`, `LsRemote`, `Fetch`,
`Push`). `ManifestFile.Parse` lets a newer `format` win over every refusal except non-JSON and invalid UTF-8 (a strict UTF-8
check first, then a permissive look for the first root `format`), then applies the 4 KiB cap and the `v1` rules, and never
throws on hostile input. `HostIdentity` hashes the OS machine id (Linux `/etc/machine-id`, macOS `ioreg`, Windows `MachineGuid`)
with the canonical database path (links resolved one hop at a time, `\\?\` stripped, lower-cased on Windows and macOS);
a source that exists but fails makes the fingerprint unavailable (`null`) instead of reading as a moved directory.
`RepoGuard.CheckPublicAsync` asks `GET https://api.github.com/repos/<owner>/<repo>` with no credentials, no cookies, at most 3
redirects and a 5-second limit (only an answer from the base address's host counts): 200 is public (refused), 404 private (cached
24 hours in `_meta.git_sync_public_check`; nothing else is cached, and a store fault on the cache is a miss), anything else is
unknown (one warning per guard); a non-GitHub remote is skipped with one notice. There is no in-process memo of the verdict.
`ServerComposition.OpenGit` (the `git` storage mode) validates the remote, the branch and that a `git` executable is on the
`PATH` (`GitExecutable.TryFind`, no process) and otherwise opens the plain local store with the label `git`; a failure falls back
to `sqlite (git failed)`.

**Git sync transport (`GitSync/GitProcess.cs`, `GitClient*.cs`, `GitSyncPaths.cs`, `SyncLock.cs`).** Every git call is one
of the closed `Core.SyncGit.GitCommand` cases (no case can force, delete or mirror; a recording `IGitProcess` plus
`ForceFlags.AssertNone` checks every argument list the tests produce). `GitProcess` is the only place a process starts:
no shell, argument list from `SyncGit.build`, the `GIT_*` environment scrubbed to an allow-list, a hard kill of the
process tree at the call's limit or cancellation, bounded output, and standard error redacted as it is read (a
credential or a UTF-8 character split across reads is never redacted in pieces). `GitClient` types the operations
(version, init, clone, ls-remote, fetch, reset, clean, ancestry, rev-parse, the protective ref, stale locks, ls-tree,
diff-tree, cat-file, stage, stage-paths, staged-set, commit, push; `stage` is `add -A -f` so a remote's `.gitignore` cannot drop a record); a clone-scoped command carries `--git-dir`/`--work-tree` so a damaged clone
can never make git act on an enclosing repository, and every call sets `GIT_CEILING_DIRECTORIES` to the database's
directory (a database directory whose path contains the path-list separator, `:` on Unix, therefore fails every call but
the version check). The user's ssh command (the process's `GIT_SSH_COMMAND`, else `core.sshCommand` as git resolves it:
system, global and XDG files with includes, read from the sync directory under the ceiling; once per client) is passed on as `GIT_SSH_COMMAND` with
`-o BatchMode=yes -o ConnectTimeout=8` appended (`-batch` for a plink or putty program; the variable is left alone when
only `GIT_SSH` is set, since such a program takes no OpenSSH options). The layout is
`<directory of the database file>/sync/<dbKey>/{lock, no-hooks, empty, <cloneKey>/repo}` where `dbKey` is the first 12 hex of
the SHA-256 of the database file's *name* (lower-cased on Windows and macOS), so two databases in one directory never
share a clone, a protective ref or a lock. `SyncLock` is a file opened with `FileShare.None` (released by the operating
system if the holder dies; the file is never deleted and holds no text); it throws `NotSupportedException` when .NET's
file locking is disabled (`DOTNET_SYSTEM_IO_DISABLEFILELOCKING` is `1` or `true`, or the `System.IO.DisableFileLocking`
switch is set) and tells "busy" (a sharing violation, or EAGAIN) from a real error. .NET ignores a flock failure other than
"would block", so on a file system without flock support the lock excludes no one. The tests that need `git` are
`[GitFact]`, the ones that need `sh` are `[ShFact]`, the ones that need both are `[GitShFact]` and the one that needs
`flock(1)` is `[FlockFact]`: they are ordinary tests (not `Integration`), so the standard verify runs them and a missing
tool shows as skips. Windows runs the `[GitFact]` tests and skips the `sh`, `git`+`sh` and `flock` ones.

Known limits, left to later milestones: a failed or interrupted `CloneAsync` can leave a partial clone directory that the
caller must move aside; a remote can send a very large blob or pack (only the call's timeout bounds clone and fetch); and
a remote's `.gitattributes` can select a filter driver the user's own configuration defines (the repository is the
user's own, and the filter is theirs).

**Git sync setup commands (`GitSync/SyncSetupService*.cs`, `Cli/Commands/Sync/`).** `total-recall sync init --url <repo> [--branch <b>]`, `sync join <repo> [--branch <b>]`, `sync leave`, `sync status` and `sync unblock <id>` are **hidden** until milestone 1b-iii (`ICliCommand.Hidden`: the top-level help leaves them out and no document, help text or `config set` refusal names them; `HiddenCommandTests` scans for that). The pure rules are `Core/SyncSetup.fs`; the service does the I/O. `init` and `join` refuse on Cortex or Postgres (the user sets `storage.mode = "local"` by hand first) and on an address holding a credential. Recording is ordered so that a crash is repaired by running the command again: place the clone (a new one, or the earlier one moved aside), then ONE database transaction (identity and `_meta` keys; `ClearForNewRepository` only when the repository differs), then the `config.toml` write (`storage.mode = "git"` and `[git_sync]`; `config.toml` is rewritten **without comments**, as `config set` does). A clone is never deleted: `CloneSideline.Move` renames it to `repo.<label>-<time>` (`unpushed`, `broken`, `left`, `superseded`) and the commands print where it went. A `join` that finds a clone refreshes it (fetch, then `reset --hard` to the remote branch, never `clean`); a clone git cannot read is sidelined as `broken`, and a fetch failure of kind `Other` is probed locally first (Interpretation 13 of the plan). `init` and `join` open the database with `SqliteConnection.Open(path, pooling:false)` plus `RunMigrations` and a borrowed `SqliteStore`; `LegacyDatabaseProbe` refuses a database of an older format first. `status` is read-only and creates nothing: it opens the database read-only (`ReadOnlyDatabase`, `ReadOnlyMeta`: an immutable URI when no `-wal`/`-shm` sidecar exists, `Mode=ReadOnly` when both do, and nothing when exactly one does), reads the manifest only when the `no-hooks` directory and `empty` file already exist, and never touches the network. Its `blocked` section (#161, spec D8), printed right before the final `last sync` line, comes from `SyncSetupService.ReadBlocked` on the same read-only connection: one statement over `git_sync_entry` for `state = 'active'` rows with a non-null `blocked_reason` (a `forgotten` or `left` row that kept its columns is not listed, and a state this build does not know is simply not matched, because the columns are read directly and never through `GitSyncRows.ReadAll`), at most 20 `(id, reason)` pairs ordered by `entry_id` (binary, i.e. ordinal) plus a `count(*) OVER ()` total, then `  and N more`; every reason is listed (a writer's `too-large` as well as `secret-detected`). It takes no lock and creates nothing; no `git_sync_entry` table (an unmigrated database) or a read error prints `blocked: unknown (<reason>)`, as do a settings load error and a database `ReadOnlyDatabase` will not open (with their reason), and `blocked: unknown (no database)` only when the database file does not exist. **`sync unblock <id>`** (`SyncSetupService.Unblock.cs`, `Cli/Commands/Sync/UnblockCommand.cs`; #161, spec D7) releases a memory held back by a secret: it refuses a missing database as `unknown-entry` before opening or creating anything, takes the sync lock (`busy` when held, or when the database is busy), opens the database read-write with migrations as `init` does, and in one immediate transaction reads the entry (`LocalEntryReader.Read`: absent is `unknown-entry`, in two tiers or unreadable is `unreadable-entry`), then the row (none, not `active`, or a reason other than `secret-detected` is `not-blocked`), then compares the current `SyncSecret.secretKey` with `blocked_hash` (different is `changed-since-blocked`: the fields changed after the block and the user was not shown them), and writes `allowed_hash` = the key with the mark cleared. After the commit it runs the detector on the content it read and prints `unblocked <id>` and the categories found, never a value. A later change to any of the four fields changes the key, so the entry is checked again. **A release is per machine**: `allowed_hash` is local state and is never synced, so another machine that pulls the released memory checks it there, and its own later changes to it are held until it is released there as well. Neither `unblock` nor `status` names the cycle or the pusher (`GitSyncCycleCallerTests`). Results go to standard output, notices and git progress to standard error, then `refused: <name>: <text>` and `note:` lines; exit 0 done, 1 refused or failed, 2 usage. Tests: `SyncSetup*Tests` (Infrastructure, `SetupRig` and `BareRemote` in `SyncSetupTestSupport.cs`) and `SyncCommandTests` (Cli, end to end with git and the data directory redirected).

**Borrowed-connection pattern**: `SqliteStore` accepts a `SqliteConnection` and does NOT dispose it.
The process-lifetime connection is owned by `ServerComposition.OpenSqlite()` → `ServerCompositionHandles`.

### Backend Selection

`ServerComposition.OpenProduction()` reads `[storage] mode` from config:
- `"local"` (default) → `SqliteStore` + `VectorSearch` (sqlite-vec)
- `"postgres"` → `PostgresStore` + `PgvectorSearch`
- `"cortex"` → `RoutingStore` wrapping `SqliteStore` + `CortexClient` for bidirectional sync

**Fallback**: If Cortex/Postgres fails to open, the composition root catches the exception and
falls back to SQLite, setting `StorageMode` to `"sqlite (cortex failed)"`.

### Schema

18 sequential migrations in `Storage/Schema.cs`. **Never modify existing migrations** — only append.

| Migration | What it adds |
|-----------|-------------|
| 1 | 6 content tables (hot/warm/cold × memory/knowledge) + vec0 virtual tables + indexes + system tables |
| 2 | `_meta` KV store + `benchmark_candidates` |
| 3 | FTS5 virtual tables + insert/delete/update triggers + backfill |
| 4 | `compaction_log.source` column (default `'compaction'`) |
| 5 | Orphan row cleanup (one-time hot-fix for 0.6.7) |
| 6 | Usage telemetry: `usage_events`, `usage_daily`, `usage_watermarks` |
| 7 | `sync_queue` for Cortex outbound buffering |
| 8 | `scope TEXT NOT NULL DEFAULT ''` on all 6 content tables + indexes |
| 9 | `entry_type` column on all content tables |
| 10 | `skill_cache` table for local skill metadata sync |
| 11 | `sync_queue.next_attempt_at` column (exponential backoff retry) |
| 12 | `skill_cache` content + frontmatter + content_hash + embedding + fingerprint columns |
| 13 | Skill usage counter columns + `skill_usage_events` table |
| 14 | `times_injected` column on all 6 content tables |
| 15 | `tool_cache` table |
| 16 | Pinned tier: `pinned_memories` / `pinned_knowledge` with the full current column set + vec0 + FTS5 + triggers |
| 17 | `sticky INTEGER NOT NULL DEFAULT 0` column and an `idx_<table>_sticky` index on `hot_memories` / `hot_knowledge` (tier model v2) |
| 18 | Git sync state: `git_sync_entry`, `git_sync_review`, `git_sync_issue`, `git_sync_alias` and `idx_import_log_target_entry_id`. SQLite only; `GitSyncStateStore` clears them for a different repository (`sync join`); `GitSyncScanner` (milestone 1b-i-1), `GitSyncApplier` (1b-i-2) and `GitSyncPusher` (1b-i-3) write `git_sync_entry` (the first two also `git_sync_issue`); the cycle that fills the rest is milestone 1b. The tests are in `GitSync/GitSyncSchemaTests.cs`, which is not `Integration`-trait, so the standard verify runs them |

**The transcript archive has its own migration chain.** `Storage/TranscriptSchema.cs` holds `TranscriptMigrationRunner` (internal), with its own `Migrations[]` and its own `tx_schema_version` table. It mirrors `RunMigrations` (one transaction, applies the versions above `MAX(version)`, idempotent) but writes no `_meta` marker and never touches `MigrationRunner`, so the main chain stays at 18 and `SchemaTests` / `GitSyncSchemaTests` are unchanged. Migration 2 adds to `tx_session` the columns `settled_user_turns` (real user turns before the watermark), `built_under_cutoff` (the forget-everything cutoff the stored pass was built under, as text) and `project_root` (the repository directory the project is excluded under) and the index `idx_tx_session_project_kind` on `(project_key, kind)`. Migration 1 creates `tx_session`, `tx_chunk`, the FTS5 table `tx_chunk_fts` (external content, with the secure-delete option on), `tx_forgotten`, `tx_state` and `tx_schema_version`. The archive tables live in the main database or in the file `transcripts.db_path` names; `Transcripts/TranscriptStore.cs` owns its connection and decides which. Do not add archive tables to `MigrationRunner`, and do not mention `tx_`, `Transcript` or `Archive` under `Sync/`, `GitSync/` or the Core sync modules (a test pins it). See `docs/transcripts.md`.

Table naming: `{tier}_{type}` (e.g., `hot_memories`, `cold_knowledge`, `pinned_memories`). Vec tables: `{table}_vec`. FTS tables: `{table}_fts`.
`MigrationRunner.TableName(tier, type)` is the canonical helper — use it, don't inline strings.

**Three table-pair arrays — know which includes pinned:**

- `TierNames.AllTablePairs` (`Memory/TierNames.cs`) and `EntryMapping.AllTablePairs` (`Server/Handlers/EntryMapping.cs`) — **8 pairs** (pinned included). Use these for runtime enumeration.
- `MigrationRunner.AllTablePairs` (`Storage/Schema.cs`) — **deliberately frozen at the 6 historical pairs**. Migrations 1–15 predate the pinned tier and must keep producing the same schema; Migration 16 creates the pinned tables with the full current column set instead of replaying the historical column evolution.

**Postgres:** no schema change for pinned — tier is a column on the `memories` / `knowledge` tables, so pinned is just a new `tier='pinned'` value (`PostgresMigrationRunner` is untouched).

---

## Embedding Layer

### IEmbedder

```csharp
interface IEmbedder
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    int Dimensions { get; }  // 384 for local bge-small-en-v1.5
}
```

**Factory**: `EmbedderFactory.CreateFromConfig(cfg.Embedding)` selects the implementation:
- `provider = "local"` (default) → `OnnxEmbedder` using bundled `models/bge-small-en-v1.5/model.onnx`
- `provider = "openai"` → `RemoteEmbedder` with OpenAI-compatible endpoint
- `provider = "bedrock"` → `RemoteEmbedder` with Amazon Bedrock

**Model path**: `ModelManager.cs` handles model discovery. It validates the bundled
`models/bge-small-en-v1.5/model.onnx` and throws a clear error if it is absent — it does **not**
download from HuggingFace. The model is fetched + sha256-verified at release build time
(`scripts/fetch-bge-small.sh`, pinned HF revision) and bundled into the per-RID release artifact;
for a git-source build, run `sh scripts/fetch-bge-small.sh` once to fetch it (it is not committed
to the repo / not in Git LFS).

`bge-small-en-v1.5` uses **CLS pooling** (not mean pooling) and an asymmetric query prefix:
search queries are embedded with the `bge` retrieval-instruction prefix while stored documents are
not. After swapping the local embedder the startup guard (`EmbedderMigration.EnsureCompatibleSqlite`,
called from `ServerComposition.OpenSqlite`/`OpenPostgres`/`OpenCortexCore`) detects a fingerprint
**mismatch** — or an **unstamped-but-populated** index (vectors present but no fingerprint, e.g. a
cortex DB created before stamping was wired into that path) — and **returns a decision** (it does no
re-embedding itself) per `embedding.on_model_change` (`OnModelChange`, default `auto`):
- `auto` → returns `ReindexInBackground`. Composition then starts an **in-process background worker**
  (its own SQLite connection + its own embedder) that re-embeds the sqlite / cortex **local** vec0
  index via `ReindexCoordinator` → `EmbeddingReindexer.RunBatched`. This is **never on the MCP boot
  path** — a synchronous re-embed of a large DB (~27 min for ~25k rows) would blow the stdio startup
  timeout into an atomic-rollback boot loop (the v3.0.2 regression this design fixes). `RunBatched`
  embeds OFF the write lock and commits per batch, advancing a `_meta` cursor (`embed.reindex.*`) so an
  interrupted run **resumes** instead of restarting; the coordinator takes a best-effort advisory lock
  (`embed.reindex.lock`) and stamps the fingerprint only after a full pass. While it runs, local
  retrieval is degraded; `session_start`/`status` surface `backgroundTasks.reindex` + a hint.
- `warn` → returns `Warned`: continues without re-stamping (recurring nag).
- `block` → throws `EmbedderFingerprintMismatchException`.
Postgres `auto` throws an explicit unsupported error (no local vec index to background-reindex). An
**empty** unstamped DB just stamps (genuinely fresh — nothing to migrate). For cortex only the local
index is migrated; the remote re-embeds independently via content-only sync. `total-recall
reindex-embeddings` runs the **same** `ReindexCoordinator` foreground (run-to-completion, also
resumable) — for `warn`/`block` deferrals and manual offline re-embeds.

**Known limitation — tokenizer accent handling**: the bundled F# WordPiece tokenizer lowercases but
does **not** strip accents (canonical BERT also strips accents). Accented words that don't match a
vocab entry therefore fall through to `[UNK]`. Impact is low for the English/code-heavy content this
memory system embeds; the behavior is pinned by a tokenizer test so it can't regress silently.

---

## Search Layer

`HybridSearch` combines two sources:
1. **Vector search** (`VectorSearch` / `PgvectorSearch`) — cosine similarity via sqlite-vec / pgvector
2. **FTS search** (`FtsSearch` / `PostgresFtsSearch`) — BM25-ranked full-text via FTS5 / tsvector

Results are merged and re-ranked by `TotalRecall.Core.Ranking` (F# pure function).
`similarity_threshold` from config filters low-score vector results before merge.

`KbSearchHandler` can additionally query a `IRemoteBackend` (Cortex) for global team KB.

---

## Importers

### IImporter

```csharp
interface IImporter
{
    string SourceTool { get; }  // e.g., "claude-code"
    Task ImportAsync(CancellationToken ct);
}
```

Each importer:
1. Detects the host tool's memory directory (platform-specific paths)
2. Reads the host's memory format (markdown, JSON, etc.)
3. Deduplicates via `ImportLog` (content hash lookup in `import_log` table)
4. Calls `IStore.StoreAsync()` + `IVectorSearch.UpsertAsync()` for new entries

Importers run during `session_start` step 2. Order in `ServerComposition` matches the registration order.

**Memory import ids and crash repair.** An import that inserts a **memory** (today: Claude Code memory
files via `ImportUtils.ImportMarkdownFile` with `ContentType.Memory`, and the Hermes `MEMORY.md`/`USER.md`
sections) goes through `ImportUtils.InsertOrRepairMemory` (internal to the assembly). It derives the entry id from the import hash
(`ImportIds.FromHash`: first 16 bytes of `SHA-256(hash)`, lowercase `8-4-4-4-12`), so the same host memory is
the same entry on every machine (git sync relies on this), looks for an existing entry with that id in the
target tier and then the other tiers, and if one exists skips the insert and repairs it: it re-embeds the
**stored** text, inserts the vector (which replaces an orphan row) and writes the missing `import_log` row.
A kill between the insert, the embedding and the log row is therefore repaired by the next run instead of
leaving a duplicate. It is a repair on retry and not a transaction because in cortex mode importers write
through `RoutingStore`, which cannot share one. Knowledge imports keep random ids; memories imported before
this was added keep their random ids.

**Adding a new importer**: implement `IImporter`, add to the `importers` list in both `OpenSqlite`
and `OpenPostgres` / `OpenCortexCore` in `ServerComposition.cs`.

Current importers: `ClaudeCode`, `CopilotCli`, `Cursor`, `Cline`, `OpenCode`, `Hermes`, `ProjectDocs`.

---

## Ingestion

`FileIngester` → `HierarchicalIndex` → `TotalRecall.Core.Chunker` (F# pure) → `IStore` + `IVectorSearch`.

Flow:
1. `IngestValidator` checks for duplicate collections (by source path hash)
2. `Chunker.chunk()` splits file content into semantic chunks (Markdown heading-aware or regex code parser)
3. Each chunk is embedded and stored as a `cold_knowledge` entry with `collection_id` linking siblings
4. `HierarchicalIndex` tracks parent/child relationships via `parent_id`

**Supported parsers** (in `TotalRecall.Core/Parsers.fs`): Markdown, code (regex-based).

---

## Telemetry

| Class | Table | Purpose |
|-------|-------|---------|
| `CompactionLog` | `compaction_log` | Records tier movements (promote/demote/compact) with semantic drift and preservation ratio |
| `UsageEventLog` | `usage_events` | Raw token usage events (one row per assistant turn) |
| `UsageDailyRollup` | `usage_daily` | Aggregated daily token counts; 30-day raw retention |
| `UsageWatermarkStore` | `usage_watermarks` | Per-host scan watermark for incremental indexing |
| `RetrievalEventLog` | `retrieval_events` | Per-search retrieval events with score, tier, and outcome signal |

**Retrieval event logging**: `memory_search` and `kb_search` handlers call `LogRetrievalEvent()`
after each search. They pass `SessionConfigSnapshot.Resolve()` (pinned by `session_start`, resolved
lazily otherwise) so events are tagged to the session's config snapshot. Each event records a `query_source` (`assistant` / `web-ui` / `cli`)
so metrics can be scoped to assistant traffic; the search handlers return the logged event's
`retrievalId`, and `memory_feedback` calls `UpdateOutcome()` to mark whether it was used.

**Read-time outcome inference** (`Eval/Metrics.cs` → `Compute(events, threshold, nowMs, graceWindowMs, …)`):
outcomes are resolved when metrics are computed, not stored. `OutcomeUsed == true` is a hit;
`false` is a miss; `NULL` is a miss only once the event is older than `nowMs - graceWindowMs`
(otherwise it is pending and excluded). `eval_report`, the Insights health score, and the CLI
`eval report` all pass a grace window (default 60 min) so un-acted assistant retrievals age into
misses rather than silently counting as hits.

---

## Cortex Sync

`RoutingStore` wraps `SqliteStore`:
- Reads go to local SQLite (fast, offline-safe)
- Writes enqueue items to `sync_queue` AND write locally
- `SyncService` drains `sync_queue` → `CortexClient` HTTP calls
- `PeriodicSync` calls `SyncService` every N seconds (default 300s) on a background thread

**Offline resilience**: if Cortex is unreachable, local reads/writes continue. The queue
is durable (SQLite) and survives process restarts.

**Pinned is local-only (deliberate)**: the remote Cortex backend has no pinned-tier support
yet, so the pinned tier is excluded from every sync path:

- `RoutingStore` never enqueues pinned upserts or deletes to `sync_queue`
- `SyncService` pull/reconcile excludes pinned — remote updates and tombstones can never
  mutate a local pin
- `migrate_to_remote` silently skips pinned entries

Pinning an entry that already exists remotely leaves the remote copy alone (accepted edge:
a later re-pull can reintroduce it as a separate non-pinned entry). Lifting these gates
requires a companion Cortex server change — tracked in `docs/TODO.md`.

---

## Config

`ConfigLoader.LoadEffectiveConfig()` merges:
1. Embedded defaults (`Config/defaults.toml` compiled as an embedded resource)
2. User config at `~/.total-recall/config.toml` (TOML via Tomlyn)
3. Environment variable overrides (e.g., `TOTAL_RECALL_DB_PATH`, `TOTAL_RECALL_CORTEX_URL`)

`ConfigLoader.GetDbPath()` → `~/.total-recall/total-recall.db` (or `TOTAL_RECALL_DB_PATH`).
`ConfigLoader.GetDataDir()` → `~/.total-recall/`.

---

## Diagnostics

`ExceptionLogger.LogChain(prefix, ex)` walks the entire `InnerException` chain and writes to
`Console.Error` with indented `-> <Type>: <Message>` formatting. AOT-safe — no reflection.

**Use at every catch boundary** that can hit a static-constructor failure or P/Invoke failure:
- Migration guard
- Server composition (host startup)
- CLI commands that touch the embedder or open the DB

A bare `Console.Error.WriteLine(ex.Message)` at these boundaries hides `DllNotFoundException`
under `TypeInitializationException` and produces unactionable output.

---

## AOT Checklist (for new Infrastructure code)

- [ ] No `Type.GetType()`, `Activator.CreateInstance()`, or `Assembly.Load()`
- [ ] All JSON serialization uses source-generated `JsonContext` attributes
- [ ] No `dynamic` types
- [ ] P/Invoke signatures (sqlite-vec, ONNX) are declared as `[DllImport]` or via `NativeLibrary.Load`
- [ ] `IsAotCompatible=true` in the csproj — verify with `dotnet publish -p:PublishAot=true -r win-x64` producing 0 trim warnings
