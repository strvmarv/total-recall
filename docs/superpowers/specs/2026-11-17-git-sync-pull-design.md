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
- Store groundwork the apply needs, SQLite only: a store transaction that takes the write lock when it begins **and lets
  the sync tables be read and written on the same transaction** (`GitSyncRows` and the issue statements take the
  caller's transaction); an update that can also set or clear the source tool and set `created_at`; and a call that
  replaces an entry's vector (inserting one when the entry has none).
- `SyncIdentity.MetaKeys.LastSha` already exists; it is now read and written.

## Decisions

### The fetch

1. **`GitSyncFetcher.FetchAsync` runs the cycle's step 2** and returns a `FetchOutcome`. It never writes the store and
   never writes `git_sync_last_ok` or `git_sync_last_error`; the cycle does. Steps, in order:
   1. read `L` from `_meta.git_sync_last_sha` (absent, empty or not a full lowercase hex object id is "no `L`");
   2. the parts of the repository guard that need no clone (not joined, a moved data directory, no remote configured) are
      answered first, so such a machine is `Refused` before any network call. Then `git ls-remote origin <branch>`. A failure is `Failed(kind, message)` (unreachable, authentication, timeout, as the
      git client classifies it) with nothing changed. A remote with no such branch is `Refused`;
   3. when the head equals `L` **and the clone holds that commit** (`rev-parse --verify <L>^{commit}` answers), **nothing is
      fetched and the clone is not reset**: `R = L`, **the repository guard runs as in step 4** (it reads the manifest from
      the clone at `R` and runs no network command, so a copied data directory or a re-pointed remote is `Refused` here
      too), the proof state is evaluated locally (decision 2) and the outcome is `NothingNew`. When the clone does **not**
      hold `L`, this step does not apply: the fetch of step 4 brings the head in (and decision 2 makes the apply unproven);
   4. otherwise `git fetch origin <branch>` (incremental, no depth). `R` is then the object id `refs/remotes/origin/<branch>`
      resolves to (a `rev-parse` after the fetch, not the `ls-remote` answer, so a remote that moved in between cannot make
      `L := R` and the tree that was read disagree). Then **the repository guard**
      (`RepoGuard.EvaluateIdentityAsync` at that ref, before the reset, as the guard's own comment says). A verdict that
      cannot sync (not joined, host unverified, a newer, missing or invalid manifest) is `Refused(reason)` with the reason
      text; the clone is not reset and nothing is applied. A fetch that fails is `Failed`;
   5. decide the proof state (decision 2);
   6. `git reset --hard origin/<branch>` on the clone (a cache, never shared history). A failure is `Failed`.
   Every tree and blob the apply reads is addressed by the object id `R`.
2. **Proven.** An apply is proven only when all of these hold: there is an `L`; `L` is an ancestor of `R` (equal counts);
   and the protective ref `refs/total-recall/last-synced` points at `L`. A ref that is a **strict ancestor** of `L` is
   repaired in place (`update-ref`, no network, also on the `NothingNew` path) and does not make the apply unproven: it is
   what a crash between the `_meta` write and the ref write leaves (decision 14 writes `_meta` first). Any other ref value,
   a missing ref with an `L` (a re-cloned clone), a restored database (an `L` the clone does not hold) or a git answer of
   "no" or **"unknown"** (the git client's `Tri.Unknown`: an exit code other than 0 and 1, for instance an `L` that a
   shallow clone does not hold) is **unproven**. Only a git failure that gives no answer (a timeout, a limit) is `Failed`,
   never "unproven". The apply of this milestone treats unproven as the decisions do (`ApplyProven = false`: tags merge as
   a union, nothing deletes); the deletion rules that depend on it are 1b-ii.
3. **`FetchResult`** carries `Head` (`R`), `Last` (`L` or none), `Proven` and `NothingNew`. `NothingNew` still lets the apply
   run, from the local clone and with no network (decision 5).

### Which entries the apply looks at

4. **The records and the paths come from the clone as git objects at `R`**, never from the working tree
   (`ls-tree -r -z`, `diff-tree -r -z --no-renames`, `cat-file`). **The listing of `R`'s tree is read every pull that
   runs the apply, and it alone decides what is present and what is rejected**: each entry is judged by
   `SyncTree.classifyRecordEntry`, then each accepted file is read with `RecordReader.Parse` with the record's path. The
   diff supplies only the ids whose record changed (its `Changed` verdicts); its `Gone` and `RejectedDiff` verdicts are
   not used (a rejection is derived from the listing, so a deleted file never produces one). **An id is present when its
   path is in the listing, accepted or rejected**, where a rejected entry names an id only when its path has the exact form
   `memories/<shard>/<id>.json` with an eligible id and the right shard (a symlink, a submodule, a file over 1 MiB or
   an invalid record there); a rejection for any other path (a bad path, shard or id) names no id. A record that is
   rejected is never applied and never an exception: it gets a `rejected-record` issue (decision 15), and an id it names is
   **excluded from the fifth bullet of decision 5 and is never decided with `Theirs = None`**, so a corrupt
   remote file is never read as a deletion and never makes the pull incomplete. Its entry and row are untouched, **except** that
   an id the scan reported as locally deleted (bullet 7 of decision 5) is postponed as `deletion`: its record cannot be
   judged, so `L` must not move over the deletion.
5. **The set of entries to decide** (the "visit set" of the umbrella, extended as the scan's design requires), in ordinal
   id order:
   - the ids whose record changed between `L` and `R`; with no `L`, or an unproven apply, **every** accepted record in
     `R`'s listing instead (also on `NothingNew`, from the local clone);
   - every row with **no base** whose id has an accepted record in `R` (the no-base rules; this also covers a first `join`);
   - every id with an open `apply-failed` issue, **including the ones the scan skipped** (decision 10);
   - every path in `R`'s listing whose rejection key (decision 15) has an open `rejected-record` issue, which is read and
     judged again (classification and parse), so a rejection that depends on this build (an entry type or format a newer
     build writes) is revisited after an upgrade without the record changing;
   - every `active` row **with a base** whose id is **not in `R`'s listing at all** (a possible remote deletion; the decision
     is postponed in this milestone, but it must be reached so `L` is not advanced over it);
   - every `active` row whose memory exists in no table (the scan's `LocalDeletions`, which the caller passes in; it
     includes a row with no base), **whatever `R` holds**: with a record present the decision is a pushed or held deletion,
     which is postponed, with no record and no base it drops the row (the memory was never in the repository), and it must be
     reached so `L` is not advanced over a local deletion no part has acted on yet.
   An id in the set with neither a row, a local entry nor a record is decided as an ignore (`Ignore`, the merge rules'
   no-op), which counts as a confirmed no-op (decision 15 removes its `apply-failed` row). When
   the outcome was `NothingNew` and the apply is proven, the first bullet is empty: only the other bullets apply.
6. **A row that is not `active`** (`forgotten`, `left`) is decided like any other row when its id is in the set (the merge
   decisions have rows for it), but the fifth and sixth bullets add `active` rows only, so a `forgotten` or `left` row
   with no record is never added by them. (This milestone creates neither kind of row; the scan creates `left` rows.)

### One entry

7. **The input of `decide`** for an id: the row state (none when there is no row); `Base`, the row's `base_json` read with
   `RecordReader` (a `base_json` that cannot be read is an `apply-failed` for the entry, never "no base"); `Theirs`, the
   accepted record or none when the id is not in `R`'s listing; `Local`, the entry read from the store with the mapping
   the scan uses (one function, shared); `Pending`, the row's three stamp columns; `Pushing`, the decoded
   `pushing_stamps` (decision 11); `ApplyProven`, from the fetch result; `HeldDelete`, always none (no `held-delete` item
   can exist before 1b-ii; that milestone supplies it). **An id that exists as a knowledge-base entry** (the apply reads the knowledge tables for the visited ids, as the scan does)
   with no row is decided as an ignore, a confirmed no-op: nothing is inserted, so one id is never in both a memory and a
   knowledge table (a `join` clears the rows, including the `left` row that said so). An entry that cannot be read, or whose id is in two memory tables,
   is not decided: it is treated as decision 10 treats a scan-skipped id (not applied, an issue, `L` not held back), whether
   or not the scan listed it.
8. **Postponement.** `SyncApply.postponement` runs **first**, on the decision, before any vector is computed or any store
   or row write is made, and returns the reason or none. The decision is carried out **unless** it needs a part that is not
   built, which is exactly one of (the reasons, in this order of precedence, when several apply):
   `deletion`: the entry outcome is `DeleteEntry`, `HoldDeletion` or `PushDeletion`, or the decision has a note,
   `FreshContentStamp`, `CloseHeldDelete` or sets the row state to `Forgotten` (all of these belong to the deletion
   rules); `review`: the decision has any review request; `repush`: a group's `Repush` flag is set. A postponed entry gets
   **no change at all**: no store write, no row write, no write or removal of the entry's `apply-failed` issue, no `import_log` row, and no embedder
   call. It is listed in the result with its reason, it **holds `L` back** (decision 14), and it is decided again, from
   scratch, by every later pull (the fifth bullet of decision 5 and the diff keep it in the set). This is what lets 1b-ii
   and 1b-iii replace the postponement with the real handling without a migration: nothing was recorded that they must
   undo. **Consequence, stated so nobody reads it as a bug:** while an entry is postponed this machine does not push (the
   cycle pushes only after a complete apply), so a pinned record, a remote deletion, **a memory deleted on this machine**
   (the commonest case, compaction included) or a first-sync text conflict stalls this machine's pushes until 1b-ii and
   1b-iii land. The cycle tests of 1b-i-3 therefore use none of these as data.
9. **What is carried out** (everything else): `InsertFromRecord` (a record that is not sticky), `MergeGroups` whose parts
   are content (`LeaveContent`, `AdoptCreatedAt`, `TakeTheirs`), tags, sticky (`LeaveSticky`, `UnpinLocally`) and tier
   (promote), `LeaveEntry`, `Ignore` with no row-state change, `DropRow`, and `Defer`.
   - **Insert**: the entry is inserted at the record's tier (hot, warm or cold), **not sticky**, with the record's text,
     project, scope, entry type, source tool (none when the record has none), tags and `created_at`; its vector is
     computed **before** the transaction from the record's text. `updated_at` and `last_accessed_at` are the insert time
     (a pulled entry is new to this machine and is not swept as old).
   - **Content**: `TakeTheirs` writes the record's text, project, scope, entry type, source tool and `created_at`
     (an absent project, scope or source tool is **cleared**, never left as it was), and the vector is replaced from the new
     text computed before the transaction; `AdoptCreatedAt` writes only `created_at`.
   - **Tags**: the merged set (`Merged.Tags.Result`) is written when `ApplyLocal`.
   - **Sticky**: `UnpinLocally` clears the flag; the entry stays in the hot tier.
   - **Tier**: a promotion moves the entry to the warmer tier the decision names (`Merged.Tier`), keeping its vector (or
     the replaced one, when the text changed in the same decision).
   - **The hot limit** (`tiers.hot.max_content_chars`) is judged on the **result** of the decision: an entry that the
     decision would leave in the hot tier with a text longer than the limit (a hot insert, a promotion into hot, a text
     change on an entry that is already hot, or both together) is stored in, or moved to, the **warm** tier instead and
     listed as `ColderTier`. The row's base still holds the record, and `local_tier` is the stored (warm) tier; a tier
     colder than the base's is no event for the scan, and because the base's tier stamp is the record's the promotion is
     not tried again until the record's tier stamp changes. When the stored tier is then **not warmer than the base's** and
     the row has a pending tier stamp, that stamp is cleared in the same step (what the scan would do), so a scan right
     after writes nothing.
   - **`Defer`** applies nothing, is listed as deferred and holds `L` back: it is the decision's own "leave this entry for
     the next cycle" (a row that is `forgotten` or `left` while its memory exists again: the next scan reactivates it).
10. **An id the scan skipped** (the `Skipped` list of the scan's summary, which the caller passes in) and an id found
    unreadable or in two tables at apply time (decision 7) are **not applied for any group**: nothing is written for the
    entry, there is no deferral, and `L` is **not** held back. To make sure it is looked at again, it gets (or keeps) an
    `apply-failed` issue whose `detail` begins `scan-skipped:` or `unreadable:` with the reason (`attempts` is not
    incremented), so every later pull visits it until it can be applied, which removes the issue. **A skip never overwrites an
    existing `apply-failed` row** (a real failure's attempts, hash and detail stay; the row already keeps the id in the
    set), a real failure **replaces** a `scan-skipped:` or `unreadable:` row (`attempts = 1`), and the id counts as
    **not applied** for the clean-up of decision 15: its row is removed only when a later pull really decides it. This is deliberately
    stronger than the scan's design, which says only that the skipped **group** is exempt: the other groups are delayed too,
    because the decisions are whole-entry and a group's result may depend on another's.

### The transaction of one entry

11. **`pushing_stamps` is a JSON object** `{"content":"<stamp>","sticky":"<stamp>","tier":"<stamp>"}` in the stamp text form
    (`Stamp.format`); a missing key is no stamp; `NULL`, empty, `{}` and `[]` mean none. One pure codec in Core reads and
    writes it (1b-i-3 writes it; the scan already treats those four texts as "empty"). A text that cannot be read
    counts as none.
12. **Order inside one entry.** Before the transaction: read the record, run `decide`, run `postponement` (decision 8), and
    only then compute the vector if the decision needs one (an exception here, including the embedder's, is the entry's
    `apply-failed`; a cancellation is not, decision 14). Then **one transaction that takes the write lock when it begins**
    (a deferred transaction could not be upgraded after another process wrote). The store operations and the
    `git_sync_entry` and `git_sync_issue` statements all run on **that** transaction (decision 12.3 to 12.6 must not
    write outside it). In it:
    1. **re-read the entry** and require that it is **the same entry the decision was made on** (present or absent as
       before, and its mapped view equal to the `Local` that `decide` used) and that it still **agrees with the row** as the
       scan would compare: its content and tags hashes, sticky flag and tier equal the row's `scanned_content_hash`,
       `scanned_tags_hash`, `local_sticky` and `local_tier` (a column that is `NULL` is not compared). Any difference (an
       edit, a pin or a move landed after the scan and has no stamp yet) is a **deferral**: roll back, nothing written,
       listed, `L` held back. A transaction that cannot take the write lock (a busy or locked database: an MCP handler on
       another connection is writing) is also a deferral, not a failure: no issue, no attempt, and **the apply stops
       there**: the entries after it are not attempted (they are only counted, `NotAttempted`), because each would wait the
       connection's busy timeout in turn;
    2. carry out the decision's store operations (decision 9);
    3. set the row. The **base** changes only for a group the decision marks `BaseBecomesTheirs`, which takes the record's
       value and stamp (a row with no base and no `BaseBecomesTheirs` keeps no base; `decide` marks all four groups for a
       row with no base that meets a record). The **pending stamps** per group: `KeepPending`, `ClearPending`, and
       `RestampPending`, which issues a new stamp from the persisted clock (`git_sync_hlc`, read in this transaction) above
       the greater of the record's stamp for that group and the row's pending stamp, and writes the clock back (a stamp that
       cannot be issued, because the clock range is exhausted, is the entry's `apply-failed`). The
       **state** (`Active` for a new row). The **comparison columns**: the content and tags hashes, `local_sticky` and
       `local_tier` taken from the view of the entry **as stored after step 2** (the scan's design requires it: a hash taken
       from the record would differ, for instance for an empty scope); for a decision with no entry to read they are left
       as they are. A group's `pushing_stamps` entry is removed when that group's base became the record's (what was pushed
       is now the base); the whole column becomes `NULL` when none is left. `blocked_*`, `allowed_hash` and `repush_groups`
       are kept;
    4. for a newly inserted entry write the `import_log` row in the same transaction (`source_tool = 'git-sync'`,
       `source_path = 'sync:<id>'`, `content_hash` the record's `origin.hash` when it has one, else the scan's content hash
       of the entry), through the existing `INSERT OR IGNORE`, so the host importers skip that content;
    5. remove the entry's `apply-failed` row (decision 15), when there is one;
    6. commit.
    A failure at any step rolls the whole entry back, the removal of the issue included (the store transaction's own seam
    proves it for each step, and the row and issue steps get the same kind of seam) and is the entry's `apply-failed`,
    which is then written in a transaction of its own after the rollback (decision 15).
13. **`DropRow`** deletes the entry's row in a transaction of its own with the same re-read: the entry must still be absent.
    **`LeaveEntry` and `Ignore`** go through step 3 of decision 12 like any other carried-out decision (the base, pending
    stamps and row state follow the decision, the comparison columns follow the stored entry when there is one). **A
    decision whose resulting row equals the stored row, with nothing to do to the store, opens no transaction and writes
    no row.** That rule is about the row and the store only: the removal of the entry's `apply-failed` row (decision 15) is
    done for it in **one small transaction of its own** (only when such a row exists), and `DropRow` removes it in its own
    transaction.

### Failures, issues and `L`

14. **`L` advances only when the pull is complete.** The pull is **complete** when every id of the set was processed and none
    was postponed or deferred (an `apply-failed` entry, a `rejected-record` id and an id the scan skipped do not hold `L`
    back, so one bad record cannot stop every push). Then `L := R`: `_meta.git_sync_last_sha` is written **first**, then
    `refs/total-recall/last-synced` (`update-ref`). Anything less leaves `L` where it was; the next pull repeats the work, and
    that is safe because each entry's transaction records what it applied. **A failure to record what makes an entry's loss
    visible makes the pull incomplete**: if the `apply-failed`, `rejected-record` or `scan-skipped:` issue cannot be
    written (the same lock that failed the entry may fail it), the entry counts as deferred, and `L` is held back. A failure
    to write `_meta` is `Failed` with `L` unchanged; a failure to write the ref after `_meta` is `Failed`, and the next pull is
    unproven when the ref is missing (decision 2), so it reads every record once and writes the ref again when it
    completes (a ref that is a strict ancestor of `L` is repaired in place). **Cancellation**: the token being cancelled, an `OperationCanceledException`, and a git call that
    fails with the git client's `Cancelled` kind (it does not throw) all stop the pull between entries, never inside one;
    none is an `apply-failed`, none writes an issue, and the pull is incomplete. A failure to list or diff the tree, to
    resolve `R`, or to test ancestry (other than "no" or "unknown", decision 2) is `Failed` for the pull; a failure to read
    **one** blob (any kind except `Cancelled`) is that entry's `apply-failed`. A failed removal of an
    `apply-failed` row rolls the entry's transaction back (decision 12.5), so it is that entry's deferral; a failed sweep
    (decision 15) leaves the rows as they are and is ignored until the next pull.
15. **Issues.** `apply-failed` (`git_sync_issue`, `kind = 'apply-failed'`): `record_hash` the record's blob id, `attempts`
    incremented on every failure, `detail` a one-line message with control characters replaced and at most 200 characters
    (never a stack trace), written in its own small transaction after the entry's rollback. **`rejected-record`**:
    `record_hash` the blob id, `detail` the reason word of `SyncTree.RejectReason` or the validation reject, and
    **`entry_id` the rejection key: `SyncValidation.sanitizeText` of the entry's path** (at most 64 characters; two paths
    that share their first 64 characters share one row), one function used for writing, for the fourth bullet of decision
    5 and for the sweep. Neither kind rewrites an identical row. **Clean-up**:
    - the `apply-failed` row of every id **that was decided and carried out or confirmed a no-op** (not postponed, not
      deferred, not skipped, not failed) is removed, in the entry's transaction (decision 12.5) or, for a decision that
      writes nothing, in a small transaction of its own (decision 13);
    - **the sweep**: at the end of a pull in which **every id of the set was processed** (no cancellation, no stop on a busy
      database, no failed listing, and no blob read failed), every `rejected-record` row whose key is not in this pull's set
      of rejection keys is removed (the path vanished or is now accepted), also with no `L`. A pull that stopped early
      keeps every rejection row, so an unread invalid record is not forgotten, and the sweep removes a row whatever
      happened to the id that the path names (a rejection row describes a path, so removing it leaves nothing for a later
      part to undo).
    The rows are visible in `ApplySummary`; showing them in `status` is 1b-iii.
16. **The result.** `ApplyOutcome` is `Done(ApplySummary)` or `Failed(reason)` (the clone cannot be read: a failed listing, a
    corrupt object; nothing was changed by the failing step, entries already done stay done). `ApplySummary` carries the
    counts (`Inserted`, `Updated`, `RowsDropped`, `Unchanged`, `NotAttempted`), `Complete`, `Head`, `Proven`, and the lists `Postponed`
    (id, reason), `Deferred` (id, reason), `Failed` (id, attempts, detail), `Rejected` (id or path, reason), `Skipped`
    (id, reason; the scan's and the apply-time ones) and `ColderTier` (id). It is plain data; no text for the user is
    produced here.

### Interplay with the scan

17. **A scan straight after a pull stamps nothing**, whatever the pull applied or left: that is the contract of decision 12.3,
    and the tests check it for every applied outcome. A change this machine made between the scan and the apply is
    never overwritten (decision 12.1) and is stamped by the next scan.
18. **Unknown fields travel.** The base is rendered from the record read (`RecordWriter` on the parsed record when all four
    groups become the record's, so unknown fields and unknown `ext` namespaces are kept). A base that mixes old and new
    groups is rendered from the record just read with the kept groups' values and stamps taken from the old base (the plan
    chooses the mechanism; unknown content of the record just read survives).

## Requirements on the later parts

- **1b-i-3 (push, cycle)** writes `pushing_stamps` in the codec of decision 11 before each push, runs scan, fetch, apply and
  push in this order under the lock, never pushes after an incomplete apply (`Complete` false), calls the apply with the
  scan's `Skipped` and `LocalDeletions`, records `git_sync_last_ok` and `git_sync_last_error` from the outcomes, and resets the clone to
  `L` before each push. A pull that returned `Refused`, `Failed` or incomplete is the cycle's to report. **The writer
  ignores the `import_log` rows whose `source_tool` is `git-sync` when it derives an `origin` from the log** (a pulled entry
  without an origin must not get one). Its tests use neither pins nor deletions as data (decision 8).
- **1b-ii (deletions)** replaces the `deletion` postponement with the real handling, supplies `HeldDelete`
  from the open `held-delete` items, and owns `repush_groups` (the `repush` postponement) and its place in the visit set
  (the umbrella's "rows with a `repush_groups` marker"). It must keep `ApplyProven` exactly as decision 2 defines it.
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
  embedder, the configuration limits, a wall-clock source and the scan's summary (its `Skipped` and `LocalDeletions`); `ApplyAsync(FetchResult, ct)`
  returns `ApplyOutcome`.
- `Infrastructure.Storage`: an immediate store transaction that exposes its `SqliteTransaction` (or offers the row and
  issue statements itself), `UpdateEntryOpts` source tool (set and clear) and `created_at` (SQLite only, the other store
  throws `NotSupportedException` as it does for scope and entry type), and `SqliteStoreTransaction.ReplaceEmbedding`
  (inserting the vector when the entry has none).
- `GitSyncRows` gains a delete and takes the caller's transaction (its commands carry none today, which fails on a
  connection with a pending transaction); the issue statements do the same.
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
(each of its bullets, and `NothingNew` with no network and the guard run); the proof state (no `L`, a non-descendant, a ref behind, a
ref elsewhere); a fault at each step of an entry's transaction; an edit landing between scan and apply (the second
connection, through a seam); a record that makes the apply throw and one that is rejected, with the others applied and
`L` advanced; `L` not advanced for a postponed or deferred entry; `_meta` written before the ref (a crash between them is
repaired by the next fetch); unreachable remote, a newer manifest and a different repository leaving everything as it was; a rejected path on an id
that has a row with a base (never read as a deletion), an `apply-failed` issue cleaned by a no-op success, an unknown
`Tri` answer read as unproven, the ref repaired on `NothingNew`, a fault in the issue write holding `L` back, a busy
database as a deferral, a record with no source tool clearing a local one, the hot limit on insert, promotion and
text change (with and without a pending tier stamp), `NothingNew` with a clone that lacks `L`, a copied data directory on
`NothingNew`, a local deletion whose record is unchanged, a scan-skipped row surviving the clean-up and not overwriting
a real failure, rejection keys (vanished, now accepted and postponed), cancellation through a git `Cancelled` failure
writing no issue, a busy database stopping the apply, an exhausted clock on `RestampPending`, a rejection sweep that is skipped after an early stop, a knowledge id that is not inserted, a rejected id that was deleted here, and a machine that is not joined being refused before any network call.

## Departures from the umbrella design

- **Postponement.** The umbrella applies deletions, held deletions and reviews in the apply. Here they are postponed
  (decision 8), by the owner's choice, so each lands with its own guard or command. A postponed entry holds `L` and so
  stalls this machine's pushes until those milestones land; nothing is advertised before then.
- **No clock observation.** The umbrella's "every stamp read from a pulled record advances the local clock" is left out:
  every stamp this milestone issues has the record's stamp for its group (or the row's pending stamp) as its floor, and a
  later scan floors a new stamp by the group's base, which is now the record's stamp, so stamp order per group holds
  without it.
- **A busy or locked database is a deferral** (the umbrella: "a database busy error ... `L` is not advanced"), not an
  `apply-failed`, so a transient lock from another connection does not count an attempt or write an issue.
- **The visit set has no `repush_groups` bullet** (1b-ii adds it) and has bullets the umbrella lacks (a rejected record
  judged again, a local deletion passed in by the scan, decision 5; a row's absence read from the listing and not from a
  rejection, decision 4).
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
4. Given a decision that deletes (including a memory deleted here that the scan reported), holds or pushes a deletion, opens a review item, sets a `forgotten` row or marks a group to
   re-push, when the pull runs, then nothing about that entry changes (store, row, issues, `import_log`), it is listed
   with its reason, and `L` does not advance.
5. Given a decision that would leave a memory in the hot tier with text longer than the hot per-entry limit (a record for
   a hot memory being inserted, a promotion into hot, a text change on a hot memory), when the pull applies it, then the
   memory is stored in or moved to the warm tier and listed.
6. Given a record that makes the apply throw (the embedder fails), a record that is rejected (a symlink, a bad path, an
   invalid record) and valid records, when the pull runs, then the valid ones are applied, the two are listed with an
   attempt count or a reason, neither holds `L` back, an `apply-failed` entry is retried by the next pull even when the remote
   head has not changed, and a later success, even one that changes nothing, removes its issue.
7. Given a rejected record at the path of a memory that has a row and a last synced version, when the pull runs, then the
   memory is not read as deleted, nothing is postponed for it (unless it was deleted here), and the rejection is listed;
   given the path disappears or the record becomes valid, then the listing is cleared (also when the entry is postponed
   for another reason), but not after a pull that stopped early.
8. Given an edit, a pin or a move that lands on an entry after the scan and before its transaction, when the pull runs, then
   that entry is not changed, is listed as deferred, `L` does not advance, and the next scan stamps the edit.
9. Given a failure injected at each step of an entry's transaction, when the pull runs, then the content row, the vector,
   the `import_log` row, the entry's row, its earlier listed failure and the clock are exactly as before (the row and
   problem writes included) and the entry is listed as failed, with its new `apply-failed` entry as the only addition.
10. Given the remote head equals `L` and the clone holds that commit, when the pull runs, then it makes one `ls-remote` call, no fetch and no network call
   otherwise, still decides the visit set from the local clone, and applies an entry whose previous apply failed.
11. Given no `L`, an `L` that is not an ancestor of the remote head, an answer from git that is neither yes nor no (for
    instance an `L` the clone does not hold), or a protective ref that points elsewhere, when the pull runs (including when
    the remote has not moved), then it is treated as unproven (every record is read, tags merge as a union, nothing is
    deleted); given a ref that is only a strict ancestor of `L` (a crash between two writes), then it is repaired, also when
    the remote has not moved, and the pull is proven.
12. Given a complete pull, when it ends, then `_meta.git_sync_last_sha` and then the protective ref name the remote head;
    given a postponed, deferred or cancelled pull, then neither moved; given an unreachable remote, a refused guard
    verdict (not joined, a newer or missing manifest, a different repository) or a failed fetch, then the store, the rows,
    the clone's checked-out commit and `L` are unchanged and the failure is reported (also when the remote has not moved and
    the guard refuses); given a cancelled pull or a database held by another process, then `L` has not moved, no issue was
    written for the cancellation and the rest was not attempted.
13. Given an id the scan skipped, when the pull runs, then nothing is applied for it, `L` is not held back, and an
    `apply-failed` issue `scan-skipped:` keeps it in every later pull until the scan stops skipping it.
14. Given records of every shape the repository can hold (a symlink, a submodule, a path outside `memories/<shard>/<id>.json`,
    a file over 1 MiB, a record with an unknown field), when the pull runs, then none is read through the working tree,
    none throws, and an unknown field survives in the row's base.
15. Given the existing automated checks, the shim tests, the tool catalog check and the release build, when they run, then
    they pass and the release build shows no new warning; given the contributor guides (`AGENTS.md`,
    `src/TotalRecall.Core/AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`), then they describe the new pieces; and
    nothing outside the new code calls the fetcher or the applier.
