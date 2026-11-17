# Git sync, milestone 1b-i-2: the pull

Date: 2026-11-17. Status: draft for review. Part of #93 (git sync). This is the second third of the umbrella's milestone
1b-i ("cycle core"), which the owner split into three: 1b-i-1 the scan (merged), **1b-i-2 the pull** (this document:
fetch and apply) and 1b-i-3 the push and the cycle. The umbrella design is `2026-11-15-git-sync-design.md` (the sections
"The sync cycle" steps 2 and 3, "Deletions", "Imports" and "Failure handling"); the scan's design is
`2026-11-17-git-sync-scan-design.md`. Where this document is more precise it wins for this milestone, and every departure
is listed under "Departures from the umbrella design".

## Goal

Give the sync cycle its second and third steps: **fetch** the remote branch into the local clone (a cache of the remote),
and **apply** what the repository holds to this machine's memories, one entry at a time, with the pure merge decisions
that already exist (`SyncMerge.decide`). After a completed pull the store holds what the other machines learned, each
entry's row records what was reconciled (its base), and the row says exactly what the scan will compare, so a scan
straight after changes nothing.

Success: a record another machine pushed (a new memory, a text edit, a tag edit, an unpin, a promotion) reaches this
machine's store, its vector and its row; the same pull applied twice changes nothing; an edit this machine made
meanwhile is never overwritten; one bad record never stops the others; and every case that needs a part not built yet
(a deletion, a review item) is left exactly as it was and reported, so nothing is lost and nothing is deleted by
accident.

## Non-goals

Left to later milestones and **not** built here:

- The push, the writer, commit, the cycle that calls fetch and apply, the lock, retry on non-fast-forward, recording
  `git_sync_last_ok` and `git_sync_last_error` (1b-i-3). The pull assumes the caller holds the sync lock and has just run
  the scan; it takes no lock and runs no scan.
- Everything that deletes or holds a deletion: applying a remote deletion, `held-delete` items, the delete guard,
  unproven-apply handling, `forgotten` rows, `repush_groups` markers, push of a local deletion (1b-ii). Such an entry is
  **postponed** (decision 8).
- Everything that opens a review item: a record that pins (`new-pin`), a remote edit to a sticky entry (`pin-edit`), a
  text conflict (`conflict`), the commands to answer them, the secret block, alias detection (1b-iii). Such an entry is
  **postponed** (decision 8).
- The importance order, the adaptive time budget, progress reporting and `session_start` integration (milestones 2 and
  3). The pull processes entries in ordinal id order and runs to completion unless it is cancelled.
- Arrivals (what to tell the user was received) and the `sync` block of `status`.

## What exists and what is new

Built already: the git client (`ls-remote`, `fetch`, `reset --hard`, `merge-base --is-ancestor`, `diff-tree`, `ls-tree`,
`cat-file`, the protective ref), `RepoGuard`, the record reader, the tree verdicts (`SyncTree`), the pure merge decisions
(`SyncMerge.decide`), the scan and its row access (`GitSyncRows`), the store transaction (`SqliteStoreTransaction`) and
the sync tables. The new pieces:

- `Core.SyncApply` (F#, pure): from a `MergeDecision` and the entry's row, the **postponement** rule (which decisions
  this milestone does not carry out), the new row (base, pending stamps, row state), the new base record per group, and
  the codec of `pushing_stamps` (decision 11).
- `Infrastructure.GitSync.GitSyncFetcher`: the network and the clone: `ls-remote`, the skip when nothing is new, fetch,
  the repository guard, the proof state (`L` an ancestor of `R`, the protective ref against `_meta`), reset of the clone.
- `Infrastructure.GitSync.GitSyncApplier`: which entries to look at, reading their records as git objects, one
  transaction per entry, row updates, issues, and the advance of `L`.
- Store groundwork the apply needs, SQLite only: a store transaction that takes the write lock when it begins; an update
  that can also set the source tool and `created_at`; and a call that replaces an entry's vector.
- `SyncIdentity.MetaKeys.LastSha` already exists; it is now read and written.

## Decisions

### The fetch

1. **`GitSyncFetcher.FetchAsync` runs the cycle's step 2** and returns a `FetchOutcome`. It never writes the store and
   never writes `git_sync_last_ok` or `git_sync_last_error`; the cycle does. Steps, in order:
   1. read `L` from `_meta.git_sync_last_sha` (absent, empty or not a full lowercase hex object id is "no `L`");
   2. `git ls-remote origin <branch>`. A failure is `Failed(kind, message)` (unreachable, authentication, timeout, as the
      git client classifies it) with nothing changed. A remote with no such branch is `Refused`;
   3. when the head equals `L`, **nothing is fetched and the clone is not touched**: the outcome is `NothingNew` with `R = L`;
   4. otherwise `git fetch origin <branch>` (incremental, no depth), then **the repository guard**
      (`RepoGuard.EvaluateIdentityAsync` at `refs/remotes/origin/<branch>`, before the reset, as the guard's own comment says).
      A verdict that cannot sync (not joined, host unverified, a newer, missing or invalid manifest) is `Refused(reason)`
      with the reason text; the clone is not reset and nothing is applied. A fetch that fails is `Failed`;
   5. `git merge-base --is-ancestor L R` decides **proven** (below);
   6. `git reset --hard origin/<branch>` on the clone (a cache, never shared history). A failure is `Failed`.
2. **Proven.** An apply is proven only when all of these hold: there is an `L`; `L` is an ancestor of `R` (equal counts);
   and the protective ref `refs/total-recall/last-synced` points at `L`. A ref that is a **strict ancestor** of `L` is
   repaired in place (`update-ref`) and does not make the apply unproven: it is what a crash between the `_meta` write and
   the ref write leaves (decision 14 writes `_meta` first). Any other ref value, a missing ref with an `L`, a restored
   database (an `L` the clone does not hold) or a failed ancestor test with a valid answer "no" is **unproven**. A git
   failure while testing is `Failed`, never "unproven". The apply of this milestone treats unproven as the decisions do
   (`ApplyProven = false`: tags merge as a union, nothing deletes); the deletion rules that depend on it are 1b-ii.
3. **`FetchResult`** carries `Head` (`R`), `Last` (`L` or none), `Proven` and `NothingNew`. `NothingNew` still lets the apply run for
   the visit set (decision 5).

### Which entries the apply looks at

4. **The records and the paths come from the clone as git objects**, never from the working tree
   (`ls-tree -r -z`, `diff-tree -r -z --no-renames`, `cat-file`), and every path and file is judged by the existing rules
   (`SyncTree.classifyRecordEntry` and `classifyDiffEntry`, then `RecordReader.Parse` with the record's path). The listing of
   `R`'s tree is read **every** pull that runs the apply (it supplies the paths for the absence checks). A record
   that is rejected (a symlink, a submodule, a path that is not `memories/<shard>/<id>.json`, over 1 MiB, an invalid
   record) is never applied and never an exception: it gets a `rejected-record` issue (decision 12) and its entry is left
   untouched.
5. **The set of entries to decide** (the "visit set" of the umbrella, extended as the scan's design requires), in ordinal
   id order:
   - the ids whose record changed between `L` and `R` (`Changed` verdicts of `diff-tree`); with no `L`, or an unproven
     apply, **every** accepted record in `R`'s tree instead;
   - every row with **no base** whose id has a record in `R` (the no-base rules; this also covers a first `join`);
   - every id with an open `apply-failed` issue, **including the ones the scan skipped** (decision 10);
   - every `active` row **with a base** whose id has no record in `R` (a possible remote deletion; the decision is
     postponed in this milestone, but it must be reached so `L` is not advanced over it);
   - every `active` row with **no base and no memory entry** whose id has no record in `R` (the decision drops the row: it
     was never in the repository).
   An id in the set with neither a row, a local entry nor a record is ignored. When the outcome was `NothingNew` the first
   bullet is empty: only the other four apply, from the local clone, with no network.
6. **A row that is not `active`** (`forgotten`, `left`) is decided like any other row when its id is in the set (the merge
   decisions have rows for it), but the fourth and fifth bullets of decision 5 add `active` rows only, so a `forgotten`
   or `left` row with no record is never added by them. (This milestone creates neither kind of row; the scan creates
   `left` rows.)

### One entry

7. **The input of `decide`** for an id: the row state (none when there is no row); `Base`, the row's `base_json` read with
   `RecordReader` (a `base_json` that cannot be read is an `apply-failed` for the entry, never "no base"); `Theirs`, the
   record or none when the id has no record in `R`; `Local`, the entry read from the store with the mapping the scan uses
   (one function, shared; an entry that cannot be read, or whose id is in two memory tables, is skipped like the scan
   skips it: not applied, not an `apply-failed`, listed); `Pending`, the row's three stamp columns; `Pushing`, the decoded
   `pushing_stamps` (decision 11); `ApplyProven`, from the fetch result; `HeldDelete`, always none (no `held-delete` item
   can exist before 1b-ii; that milestone supplies it).
8. **Postponement.** The decision is carried out **unless** it needs a part that is not built, which is exactly one of:
   the entry outcome is `DeleteEntry`, `HoldDeletion` or `PushDeletion`; the decision has any review request, any note,
   `FreshContentStamp` or `CloseHeldDelete`; the decision sets the row state to `Forgotten`; or a group's `Repush` flag is
   set. `SyncApply.postponement` returns the reason (`deletion`, `review`, `repush`, `row-state`) and is pure and total. A
   postponed entry gets **no change at all**: no store write, no row write, no issue, no `import_log` row. It is listed
   in the result with its reason, it **holds `L` back** (decision 14), and it is decided again, from scratch, by every later pull (the
   `active`-with-a-base-and-no-record bullet of decision 5 and the diff keep it in the set). This is what lets 1b-ii and 1b-iii replace
   the postponement with the real handling without a migration: nothing was recorded that they must undo.
9. **What is carried out** (everything else): `InsertFromRecord` (a record that is not sticky), `MergeGroups` whose parts
   are content (`LeaveContent`, `AdoptCreatedAt`, `TakeTheirs`), tags, sticky (`LeaveSticky`, `UnpinLocally`) and tier
   (promote), `LeaveEntry`, `Ignore` with no row-state change, `DropRow`, and `Defer`.
   - **Insert**: the entry is inserted at the record's tier (hot, warm or cold), **not sticky**, with the record's text,
     project, scope, entry type, source tool (none when the record has none), tags and `created_at`; its vector is
     computed **before** the transaction from the record's text. `updated_at` and `last_accessed_at` are the insert time
     (a pulled entry is new to this machine and is not swept as old). A record for the hot tier whose text is longer than
     this machine's hot per-entry limit (`tiers.hot.max_content_chars`) is inserted in the **warm** tier instead and
     listed as `ColderTier`; the row's `base_json` still holds the record, and `local_tier` is `warm` (a tier
     colder than the base's is no event for the scan).
   - **Content**: `TakeTheirs` writes the record's text, project, scope, entry type, source tool and `created_at`, and the
     vector is replaced from the new text computed before the transaction; `AdoptCreatedAt` writes only `created_at`.
   - **Tags**: the merged set (`Merged.Tags.Result`) is written when `ApplyLocal`.
   - **Sticky**: `UnpinLocally` clears the flag; the entry stays in the hot tier.
   - **Tier**: a promotion moves the entry to the warmer tier the decision names (`Merged.Tier`), keeping its vector (or
     the replaced one, when the text changed in the same decision). A promotion into the hot tier of an entry whose text
     exceeds the hot limit is not made (listed as `ColderTier`; the tier stays, and the base's tier stamp is still the
     record's, so the promotion is not tried again until the record's tier stamp changes).
   - **`Defer`** applies nothing, is listed as deferred and holds `L` back: it is the decision's own "leave this entry for
     the next cycle" (a row that is `forgotten` or `left` while its memory exists again: the next scan reactivates it).
10. **An id the scan skipped** (the `Skipped` list of the scan's summary, which the caller passes in) is **not applied for
    any group**: nothing is written for it, no deferral, and `L` is **not** held back. To make sure it is looked at again, it
    gets (or keeps) an `apply-failed` issue whose `detail` begins `scan-skipped:` with the scan's reason (`attempts` is not
    incremented), so every later pull visits it until the scan stops skipping it and the apply succeeds, which removes the
    issue. This is deliberately stronger than the scan's design, which says only that the skipped **group** is exempt: the
    other groups are delayed too, because the decisions are whole-entry and a group's result may depend on another's.

### The transaction of one entry

11. **`pushing_stamps` is a JSON object** `{"content":"<stamp>","sticky":"<stamp>","tier":"<stamp>"}` in the stamp text form
    (`Stamp.format`); a missing key is no stamp; `NULL`, empty, `{}` and `[]` mean none. One pure codec in Core reads and
    writes it (1b-i-3 writes it; the scan already treats those four texts as "empty"). A text that cannot be read
    counts as none.
12. **Order inside one entry's transaction.** Before it: read the record, run `decide`, compute the vector if the decision
    needs one (an exception here, including the embedder's, is the entry's `apply-failed`). Then **one transaction that
    takes the write lock when it begins** (a deferred transaction could not be upgraded after another process wrote):
    1. **re-read the entry** and compare it with the row exactly as the scan would: the entry's presence, its content and
       tags hashes, its sticky flag and its tier must equal the row's (`scanned_content_hash`, `scanned_tags_hash`,
       `local_sticky`, `local_tier`; for a row with a `NULL` column nothing is compared for that column; for an entry with
       no row, the entry must still be absent). Any difference (an edit, a pin or a move landed after the scan and has no
       stamp yet) is a **deferral**: roll back, nothing written, listed, `L` held back;
    2. carry out the decision's store operations (decision 9);
    3. set the row: the **base** per group (a group the decision marks `BaseBecomesTheirs` takes the record's value and stamp,
       the others keep the old base; no old base means all four groups are the record's, and a decision that mixes them
       for a row with no base is an `apply-failed`), the **pending stamps** per group (`KeepPending`, `ClearPending`, and
       `RestampPending`, which issues a new stamp from the persisted clock above the record's stamp for that group and
       writes the clock back), the **state** (`Active` for a new row), and the **comparison columns**: the content and tags
       hashes, `local_sticky` and `local_tier` taken from the view of the entry **as stored after step 2** (the scan's
       design requires it: a hash taken from the record would differ, for instance for an empty scope). A group's
       `pushing_stamps` entry is removed when that group's base became the record's (what was pushed is now the base);
       the whole column becomes `NULL` when none is left. `blocked_*`, `allowed_hash` and `repush_groups` are kept;
    4. for a newly inserted entry write the `import_log` row in the same transaction (`source_tool = 'git-sync'`,
       `source_path = 'sync:<id>'`, `content_hash` the record's `origin.hash` when it has one, else the scan's content hash
       of the entry), through the existing `INSERT OR IGNORE`, so the host importers skip that content;
    5. remove the entry's `apply-failed` and `rejected-record` issue rows;
    6. commit.
    A failure at any step rolls the whole entry back (the store transaction's own seam proves it for each step) and is the
    entry's `apply-failed`.
13. **`DropRow`** deletes the entry's row (and its issues) in a transaction of its own with the same re-read: the entry
    must still be absent. **`LeaveEntry` and `Ignore`** go through step 3 of decision 12 like any other carried-out
    decision (the base, pending stamps and row state follow the decision, the comparison columns follow the stored
    entry). **A decision whose resulting row equals the stored row, with nothing to do to the store, writes nothing and
    opens no transaction.**

### Failures, issues and `L`

14. **`L` advances only when the pull is complete.** The pull is **complete** when every id of the set was processed and none
    was postponed or deferred (an `apply-failed` and a `rejected-record` entry, and an id the scan skipped, do not hold `L`
    back, so one bad record cannot stop every push). Then `L := R`: `_meta.git_sync_last_sha` is written **first**, then
    `refs/total-recall/last-synced` (`update-ref`). Anything less leaves `L` where it was; the next pull repeats the work, and
    that is safe because each entry's transaction records what it applied. A cancellation (a token) stops between entries,
    never inside one, and counts as incomplete.
15. **`apply-failed`** (`git_sync_issue`, `kind = 'apply-failed'`): `record_hash` the record's blob id, `attempts` incremented
    on every failure, `detail` a one-line sanitised message (`SyncValidation.sanitizeText`, at most 200 characters, no stack
    trace), written in its own small transaction after the entry's rollback. **`rejected-record`**: `record_hash` the blob
    id, `detail` the reason word of `SyncTree.RejectReason` or the validation reject; `entry_id` is the record's id when it
    has one and else the sanitised path (at most 200 characters). Neither rewrites an identical row, and a successful apply
    of the entry, or the disappearance of its path, removes them. They are visible in `ApplySummary`; showing them in
    `status` is 1b-iii.
16. **The result.** `ApplyOutcome` is `Done(ApplySummary)` or `Failed(reason)` (the clone cannot be read: a failed listing, a
    corrupt object; nothing was changed by the failing step, entries already done stay done). `ApplySummary` carries the
    counts (`Inserted`, `Updated`, `RowsDropped`, `Unchanged`), `Complete`, `Head`, `Proven`, and the lists `Postponed`
    (id, reason), `Deferred` (id, reason), `Failed` (id, attempts, detail), `Rejected` (id or path, reason), `ScanSkipped`
    (id) and `ColderTier` (id). It is plain data; no text for the user is produced here.

### Interplay with the scan

17. **A scan straight after a pull stamps nothing**, whatever the pull applied or left: that is the contract of decision 12.3,
    and the tests check it for every applied outcome. A change this machine made between the scan and the apply is
    never overwritten (decision 12.1) and is stamped by the next scan.
18. **Unknown fields travel.** The base is rendered from the record read (`RecordWriter` on the parsed record when all four
    groups become the record's, so unknown fields and unknown `ext` namespaces are kept); a base that mixes old and new
    groups is rendered from the merged record model.

## Requirements on the later parts

- **1b-i-3 (push, cycle)** writes `pushing_stamps` in the codec of decision 11 before each push, runs scan, fetch, apply and
  push in this order under the lock, never pushes after an incomplete apply (`Complete` false), calls the apply with the
  scan's `Skipped` list, records `git_sync_last_ok` and `git_sync_last_error` from the outcomes, and resets the clone to
  `L` before each push. A pull that returned `Refused`, `Failed` or incomplete is the cycle's to report.
- **1b-ii (deletions)** replaces the `deletion` and `row-state` postponements with the real handling, supplies `HeldDelete`
  from the open `held-delete` items, and owns `repush_groups` (the `repush` postponement). It must keep `ApplyProven`
  exactly as decision 2 defines it.
- **1b-iii (review)** replaces the `review` postponement: it must open the items from the decision's `Reviews` in the same
  transaction as the rest of the entry, and must not change what a postponed entry looked like (nothing was recorded).
  Alias detection happens before an insert (the umbrella's "Legacy alias resolution").

## Interfaces (for the plan)

- `Core.SyncApply`: `postponement : MergeDecision -> PostponeReason option`; `newBase`, the per-group base; the pending
  stamp and row-state updates as one pure function from (row, decision, theirs) to a row description; `PushingStamps`
  (`decode`, `encode`).
- `Infrastructure.GitSync.GitSyncFetcher`: constructed with the git client, the guard, the store connection and the branch;
  `FetchAsync(ct)` returns `FetchOutcome` (`Fetched`, `NothingNew`, `Refused`, `Failed`).
- `Infrastructure.GitSync.GitSyncApplier`: constructed with the git client, a `SqliteStore` on the sync connection, the
  embedder, the configuration limits, a wall-clock source and the scan's skipped list; `ApplyAsync(FetchResult, ct)`
  returns `ApplyOutcome`.
- `Infrastructure.Storage`: an immediate store transaction, `UpdateEntryOpts` source tool and `created_at` (SQLite only,
  the other store throws `NotSupportedException` as it does for scope and entry type), and
  `SqliteStoreTransaction.ReplaceEmbedding`.
- `GitSyncRows` gains a delete.
- The mapping from a stored entry to the scan's local view becomes one shared function.

The exact signatures are the plan's. What is fixed here: `postponement` and the row computation are pure and total, one
entry is one transaction, the visit set is exactly decision 5, and the pull writes the memory tables only through an
entry's transaction.

## Tests

Core (`SyncApplyTests.fs`): `postponement` for every outcome and flag; the row computation per `BaseUpdate` and
`StampAction`; the `pushing_stamps` codec round trip and its four empty forms; totality over generated decisions.

Infrastructure (a temporary SQLite database seeded through `SqliteStore`, and a real local bare repository with two
clones for the git side, as the 1a milestones did; the embedder is a stub): every carried-out outcome (insert, text edit,
tags, unpin, promotion, no-base adopt and no-base differing text, the hot limit) checks the stored row, the vector, the
`import_log` row, the entry's row, and that a following scan stamps nothing and writes nothing; applying twice; each
postponed case leaves the store, the rows and the issues byte-for-byte as before and lists the reason; the visit set
(each of its five parts, and `NothingNew` with no network); the proof state (no `L`, a non-descendant, a ref behind, a
ref elsewhere); a fault at each step of an entry's transaction; an edit landing between scan and apply (the second
connection, through a seam); a record that makes the apply throw and one that is rejected, with the others applied and
`L` advanced; `L` not advanced for a postponed or deferred entry; `_meta` written before the ref (a crash between them is
repaired by the next fetch); unreachable remote, a newer manifest and a different repository leaving everything as it was.

## Departures from the umbrella design

- **Postponement.** The umbrella applies deletions, held deletions and reviews in the apply. Here they are postponed
  (decision 8), by the owner's choice, so each lands with its own guard or command.
- **A text conflict is postponed** even where the umbrella's no-base table says "the record wins": the decision also opens
  a conflict review, which is 1b-iii, and applying the record's text alone would lose the local text.
- **Alias detection** is 1b-iii: a pulled record whose `origin.hash` matches a different local legacy import is inserted
  here like any other. Nothing is advertised before 1b-iii merges, so no user can meet it.
- **No importance order, no budget** (milestone 2): entries apply in id order.
- **An immediate transaction per entry** instead of the store transaction's deferred begin, and the re-read inside it.
- **The scan-skipped rule is stronger** (decision 10).
- **`HeldDelete` is always none** until 1b-ii.
- **The hot limit** is applied on insert and on promotion into hot, as the umbrella says for a hot record only on insert.

## Acceptance criteria

1. Given a record another machine pushed for a memory this machine does not have (not sticky), when the pull runs, then the
   memory exists at the record's tier with the record's text, project, scope, entry type, source tool, tags and
   `created_at`, it has a vector, an `import_log` row `git-sync` exists, its row is `active` with the record as its base,
   and a scan right after stamps nothing and writes nothing.
2. Given a record changed by another machine (text, tags, an unpin, a promotion), when the pull runs, then the change is in
   the store exactly as the merge decision says, the vector follows a text change, an edit this machine made in a
   different group is kept, and a scan right after stamps nothing; given the same pull applied again, nothing changes.
3. Given the same id with a record and a local entry and a row with no base (a first sync), when the pull runs, then equal
   content adopts the record's `created_at`, tags become the union of both sets, and the row's base becomes the record, as
   the no-base rules say; given differing text, then the entry is postponed (it needs a conflict review) and nothing about
   it changes.
4. Given a decision that deletes, holds or pushes a deletion, opens a review item, sets a `forgotten` row or marks a group to
   re-push, when the pull runs, then nothing about that entry changes (store, row, issues, `import_log`), it is listed
   with its reason, and `L` does not advance.
5. Given a record for a hot memory longer than the hot per-entry limit, when the pull inserts it, then it is stored in the
   warm tier and listed; given a promotion into hot of such an entry, then it is not made and is listed.
6. Given a record that makes the apply throw (the embedder fails), a record that is rejected (a symlink, a bad path, an
   invalid record) and valid records, when the pull runs, then the valid ones are applied, the two are listed with an
   attempt count or a reason, neither holds `L` back, an `apply-failed` entry is retried by the next pull even when the remote
   head has not changed, and a later success removes its issue.
7. Given an edit, a pin or a move that lands on an entry after the scan and before its transaction, when the pull runs, then
   that entry is not changed, is listed as deferred, `L` does not advance, and the next scan stamps the edit.
8. Given a failure injected at each step of an entry's transaction, when the pull runs, then the content row, the vector,
   the `import_log` row, the entry's row and the clock are exactly as before and the entry is listed as failed.
9. Given the remote head equals `L`, when the pull runs, then it makes one `ls-remote` call, no fetch and no network call
   otherwise, still decides the visit set from the local clone, and applies an entry whose previous apply failed.
10. Given no `L`, an `L` that is not an ancestor of the remote head, a protective ref that points elsewhere, or one that is
    a strict ancestor of `L`, when the pull runs, then the first three are unproven (every record is read, tags merge as a
    union, nothing is deleted) and the last is repaired and proven.
11. Given a complete pull, when it ends, then `_meta.git_sync_last_sha` and then the protective ref name the remote head;
    given a postponed, deferred or cancelled pull, then neither moved; given an unreachable remote, a refused guard
    verdict (not joined, a newer or missing manifest, a different repository) or a failed fetch, then the store, the rows,
    the clone's checked-out commit and `L` are unchanged and the failure is reported.
12. Given an id the scan skipped, when the pull runs, then nothing is applied for it, `L` is not held back, and an
    `apply-failed` issue `scan-skipped:` keeps it in every later pull until the scan stops skipping it.
13. Given records of every shape the repository can hold (a symlink, a submodule, a path outside `memories/<shard>/<id>.json`,
    a file over 1 MiB, a record with an unknown field), when the pull runs, then none is read through the working tree,
    none throws, and an unknown field survives in the row's base.
14. Given the existing automated checks, the shim tests, the tool catalog check and the release build, when they run, then
    they pass and the release build shows no new warning; given the contributor guides (`AGENTS.md`,
    `src/TotalRecall.Core/AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`), then they describe the new pieces; and
    nothing outside the new code calls the fetcher or the applier.
