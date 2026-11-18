# Git sync, milestone 1b-i-3: the push

Date: 2026-11-17. Status: draft for review. Part of #93 (git sync). The umbrella's milestone 1b-i ("cycle core") was split
by the owner into the scan (1b-i-1, merged), the pull (1b-i-2, merged) and the push and the cycle, which the owner then
split again: **1b-i-3 the push** (this document) and **1b-i-4 the cycle** that calls scan, fetch, apply and push under
the lock. The umbrella design is `2026-11-15-git-sync-design.md` (the sections "The sync cycle" step 4, "Repository
layout", "What syncs", "Imports" and "Failure handling"); the scan's and the pull's designs are
`2026-11-17-git-sync-scan-design.md` and `2026-11-17-git-sync-pull-design.md`. Where this document is more precise it
wins for this milestone, and every departure is listed under "Departures from the umbrella design".

## Goal

Give the sync cycle its fourth step: **push** what this machine changed since the repository last held it. After a
completed pull the clone holds exactly what the store reconciled (`L`); the push writes the records of the entries the
scan flagged (a new memory, an edit, tags, a pin or unpin, a promotion), commits them once and pushes the commit without
force. Afterwards each pushed entry's row says the record just pushed is its last synced version, nothing is pending for
what was sent, and `L` is the new commit.

Success: a local change reaches the remote as a minimal patch of the record that was there (nothing else in the file
moves, unknown fields included); an entry that cannot be pushed (too large, too many tags, a bad edit) is listed and
never written; an edit made after the scan is never published with the wrong stamp; a rejected push (another machine got
there first) leaves everything pending and changes nothing; a crash or a lost response at any point is repaired by the
next pull through the stamps this step wrote first.

## Non-goals

Left to later milestones and **not** built here:

- The cycle: the lock, calling scan, fetch, apply and push in order, the retry after a non-fast-forward, recording
  `git_sync_last_ok` and `git_sync_last_error`, a typed result for the user, sidelining a corrupt clone (1b-i-4). The push
  assumes the caller holds the sync lock, has just run the scan and a **complete** pull, and takes no lock, runs no scan,
  no fetch and no network command other than the push.
- Everything that deletes: pushing a local deletion, the delete guard, `forgotten` rows, `repush_groups` markers, writing
  back a record whose remote deletion lost to a local edit (1b-ii). The push never removes a file and never writes a
  record for an entry whose row has a base while the record is gone from the repository.
- The secret scan and its block (1b-iii): the writer's own size limits are here, a detected secret is not. **Until 1b-iii
  has merged nothing may call the push**, so no secret can be sent; that is why this milestone has no command and no host
  integration, as the scan and the pull had none.
- Pin and conflict review, aliases (1b-iii), the importance order, the time budget, progress (milestones 2 and 3).
- Arrivals and the `sync` block of `status`.

## What exists and what is new

Built already: the git client (`reset --hard`, `clean -fd`, `rev-parse`, `ls-tree`, `cat-file`, `add -A -f -- memories` (`StageAsync`),
`diff --cached --quiet`, `commit` as `total-recall <noreply@total-recall.local>`, `push HEAD:<branch>` with no force, a
non-fast-forward as its own failure kind, the protective ref), `RecordPatcher` and `RecordWriter` (the canonical bytes of a
patched or new record), `SyncValidation.checkWritable` (the size and tag limits), the record reader and `PullTree` (the
listing of a tree at a commit with its rejections), the scan (stamps, `LocalDeletions`, `Skipped`), the pull and its
rows, issues and `pushing_stamps` codec, the immediate store transaction and `GitSyncRows` / `GitSyncIssues` on a given
transaction. The new pieces:

- `Core.SyncPush` (F#, pure): which groups of an entry are sent, the patch or the new record from the entry's view, the
  row after a push, the commit message, the machine label.
- `Infrastructure.GitSync.GitSyncPusher`: the clone reset, the tree and records at `L`, the plan transaction (guard,
  `pushing_stamps`, block marks), the writes and the commit, the push, the finalize transaction and the move of `L`.
- `SyncIdentity.MetaKeys.LastSha` is now also written by the push.

## Decisions

### What the push starts from

1. **`GitSyncPusher.PushAsync(ApplySummary apply, ScanSummary scan, ct)`** returns a `PushOutcome` and never throws for a
   state of the store or the repository. It is **ready** only when both hold, else it returns `NotReady(reason)` having
   written nothing: `apply.Complete` is true, and `_meta.git_sync_last_sha` (read as the pull does, 40 or 64 lowercase hex)
   equals `apply.Head`. A pull that postponed, deferred or was cancelled has `Complete` false, so nothing is ever pushed
   after one. **The clone's own state is not a readiness condition**: a failed push leaves its commit in the clone, and the
   fetcher's `NothingNew` path (the remote did not move) never resets it, so a check on the clone's `HEAD` before the reset
   would refuse every later push.
2. **The clone is reset first**: `reset --hard <apply.Head>` then `clean -fd`, before any read or write (the reset step first writes `.git/info/attributes`, decision 10), because a commit
   or a file left behind by an earlier failed or killed push must never be published by accident. Then `rev-parse HEAD`
   must equal `apply.Head`; a failed reset or clean, a commit that the clone does not hold, or a `HEAD` that differs after
   the reset is `Failed`. (Nothing else writes the clone's working tree while this runs: the caller holds the lock.)
3. **The tree at `L`** is read as git objects with `PullTree.ReadAsync` (no diff: unproven reads the listing only): which
   ids have an accepted record (path and blob id), which paths are rejected and name an id. Each blob of a **candidate**
   (decision 4) that has an accepted record is read with `cat-file` and `RecordReader.Parse`, **before** any database
   transaction. A blob that cannot be read or parsed leaves that entry unpushed and listed (`record-unreadable`); a
   read failure of kind `Cancelled` stops the push as `Failed("Cancelled")` before anything is written.

### Which entries are pushed

4. **Candidates** are the rows with state `active` whose entry exists as a memory in exactly one tier and that either have
   **no base** or have a **pending group**: a pending `content`, `sticky` or `tier` stamp, or tags that differ from the
   base's tags (as sets; tags have no stamp). A row with no base and no record in the tree is a **new entry**; a row with a
   base is a **changed entry**.
5. **A candidate is left out and listed with its reason** when one of these holds, **the first that matches in this order**:
   - `issue`: its id has an open `apply-failed` issue (a failed apply, an unreadable entry, a scan-skipped id: they are
     left out of the push until reconciled, so nothing is patched over them) or is in the scan's `Skipped` list for any
     group;
   - `rejected-record`: a rejected path in the tree names its id (the writer never patches from a rejected record);
   - `not-reconciled`: the row has no base **and** the tree has an accepted record for the id (a complete pull reconciles
     it, so this is a defensive case: nothing is pushed blind);
   - `record-missing`: the row has a base **and** the tree has no path for the id (a remote deletion: 1b-ii);
   - `record-unreadable`: decision 3;
   - `unreadable-base`: the row's `base_json` cannot be read (it is the entry's own failure, never "no base");
   - `path-occupied`: the tree at `L` holds a path that is not exactly this record's own path but is in the way of it: any
     listed path (accepted or rejected) that, **compared without case**, equals the record's path, equals its shard directory or
     `memories`, starts with the record's path and a `/`, or **has the same leading directories as the record** (`memories`, then
     `memories/<shard>`) **up to case but not exactly** (a clone on Windows or macOS folds case, so a foreign `memories/AB/x.json`
     or `memories/ab/ABCDEF12.json` would make the file land elsewhere or stage nothing; the rule does not depend
     on the host's file system, so Linux tests run it); a sibling `memories/ab/other.json` with the exact same directories never
     blocks, and the record's own exact path is never a collision. It would fail the write or the stage of every push. (Known residual: the listing is
     of `memories` only, so a root-level case variant of that name (`MEMORIES/x.json`, a file `Memories`) is never read; on a folding
     clone its only effect is a `Failed` push at the staged-set check on every push, never a wrong publication.)
   - `blocked-elsewhere`: the row has a `blocked_reason` that is not one of the writer's (decision 8), which later
     milestones own;
   - `unreadable-entry`: the entry cannot be mapped, or is gone or is no longer a memory in one tier when the plan
     transaction re-reads it (decision 9). (An id in two tables is not a candidate; the scan lists it.)
   A row that is not `active` (`forgotten`, `left`) is not a candidate. A `LocalDeletions` row is not a candidate (its
   entry does not exist) and is **never pushed**: the push neither writes nor removes a file for it.

### What is written

6. **Groups.** For a changed entry the push starts from the record at `L` and patches (through `RecordPatcher`) only the
   pending groups, leaving everything else in the file as it was:
   - **content**: the entry's text, entry type, project, scope (an empty local scope is no scope), source tool and the
     row's pending content stamp. `created_at` is never changed;
   - **tags**: the entry's tags, written sorted and unique, when they differ from the record's tags (a tag change made
     by two machines to the same value writes nothing);
   - **sticky**: the entry's sticky flag with the row's pending sticky stamp (a pin that raises a colder record to hot
     needs the tier stamp: it is sent with the tier group, whose pending stamp the scan issues for the promotion; a pin
     without one is `missing-stamp`);
   - **tier**: the entry's tier with the row's pending tier stamp. Only a promotion has a pending stamp (demotion stays
     local).
   The **source tool** is sent as `Keep` unless the entry has a tool that differs from the record's parsed one (`Set`) or
   the entry has none while the record has a parsed one and no unparsed `source` text (`Clear`): a record written by a
   newer version may carry a source this version cannot parse, and clearing it would lose it for every machine.
   A group whose pending stamp is **not greater** than the record's stamp for that group is not written and the entry is
   listed as `stale-stamp`, **the entry's other groups are still written** (the scan and the apply floor every stamp above
   the record's, so this is defensive: a stamp must never move backwards in the repository); the stale group stays
   pending. An entry with **no group to write** (its tags equal the record's, or every pending group is stale) is **unchanged**,
   whatever the bytes: nothing is written and it is finalized like a pushed one (decision 12). An entry with a written
   group whose patched bytes **equal the blob bytes read at `L`** is unchanged too. A blob that is not in the writer's
   canonical form is normalised only by a patch that writes a group (the writer's bytes are the repository's bytes).
   `PatchFailure.MissingTierStamp` is `missing-stamp` and `PatchFailure.NotEncodable` is the `not-encodable` block; both
   leave the **whole entry** out (`stale-stamp` is the only reason that leaves out one group and writes the others).
7. **A new entry** gets a new record built from the entry: id, text, entry type, tags, source tool, `created_at`, project,
   scope (an empty scope is none), placement (the entry's tier, sticky flag and the row's pending tier and sticky
   stamps), the row's pending content stamp, `written_by` (the machine label, decision 10) and **`origin`**: the first
   `import_log` row of the entry (`target_entry_id`, ordered by `timestamp`, then `id`) whose `source_tool` is **not**
   `git-sync`, as `{tool, hash}`; none when there is no such row, so a pulled entry never gets an origin from its own
   `git-sync` row. A new entry whose row lacks a pending content, sticky or tier stamp is `missing-stamp` (the scan
   stamps all three groups of a new entry, so this is defensive). A pin on a new entry is sent as a sticky hot record.
8. **Limits.** Each record is checked with `SyncValidation.checkWritable` on the bytes that would be written (more than
   256 KiB, more than 64 tags as a set, a tag over 128 characters, an id that is not eligible, text that cannot be
   encoded). A violation **blocks the whole entry** for this push: nothing is written for it, its row gets
   `blocked_hash` (the scan's content hash of the entry) and `blocked_reason` (`too-large`, `too-many-tags`,
   `tag-too-long`, `ineligible-id` or `not-encodable`: the cases `checkWritable` returns), and it is listed. The limits are evaluated on every push for the
   candidates; and every `active` row whose `blocked_reason` is one of these names is looked at **whether or not it is a
   candidate**: when it has no pending group left (the entry went back to its base), or its record now passes, both
   columns are cleared in the plan transaction. The writer never touches `allowed_hash` or a `blocked_reason` of any other
   name. Marks are written and cleared only for entries that reach the limit check: a candidate left out by decision 5, or deferred by decision 9, keeps
   the marks it had.

### The plan transaction

9. **One immediate transaction** (`BeginImmediateTransaction`, before any file is written) takes the write lock and, for
   each candidate not already left out: re-reads the row and the entry; **requires that the entry still agrees with its
   row** as the apply requires (its content and tags hashes, sticky flag and tier equal the row's `scanned_*`, `local_sticky`
   and `local_tier`; a column that is `NULL` is not compared). A difference (an edit, a pin or a move that landed after the
   scan and has no stamp yet) leaves the entry **deferred** (listed `changed-since-scan`, nothing written for it, the next scan
   stamps the edit): publishing it under the older stamp would put new text under an old stamp. Then it builds the
   record (decisions 6 to 8) from the re-read view, writes the block marks, and writes the entry's `pushing_stamps`: the
   `{"content","sticky","tier"}` object (the pull's codec) with one entry per group **that is written**, replacing the column.
   If nothing is to be written (no candidate, or all left out), the transaction writes only block marks and its
   clean-ups. A busy database at `BEGIN IMMEDIATE`, or a failure in the transaction, rolls it back whole and is
   `Failed` (kind `Busy` or `Store`); no file has been written and nothing is pending that was not before.
   All the reads of this transaction use the connection's transaction, as the apply does.

### Writing, committing, pushing

10. **Files, commit, push.** For each planned entry the bytes are written to a temporary file in the clone's `.git` directory (never under `memories/`, so
    nothing in the working tree can be staged by accident, and `clean` and ignore rules do not matter) and renamed over
    `memories/<shard>/<id>.json` (the shard directory is created when missing); the clone has `core.symlinks=false` and
    `* -text` in the root attributes, but a `.gitattributes` the repository tracks below `memories/` (listed as a rejected
    path, so nothing excludes it) could add an `ident` or an encoding filter, so **the reset step first writes
    `.git/info/attributes`**, before the reset itself (the highest precedence, never touched by `clean`; the clone is created
    without a template, so the directory is created when missing) with the single line `* -text -eol -ident -filter -working-tree-encoding`, and
    the bytes are the writer's. Then **only the written paths are staged** (a new closed client command `StagePaths`, `add -f -- <paths>`, called **in batches of at most 100 paths** so a first sync of a large store stays under the Windows command-line limit; the first failing batch is a `Failed` push and the clone is reset, next to the existing `StageAsync` scopes, which init and join use and
    which stay as they are; an absent path is a failure there, unlike the empty `memories` scope; so a rejected
    case-variant or a tracked file that is missing is never swept in); **the staged set must
    equal the written paths** (`diff --cached --name-status -z --no-renames`: each written path `A` or `M`, nothing else, in
    particular no deletion), else the clone is reset and cleaned and the push is `Failed`; **no staged change means no commit and no push**
    (outcome `NothingToPush`; the unchanged entries are finalized as decision 12 says, a blocked entry is not finalized and keeps the marks of the
    plan transaction, and `_meta.git_sync_last_sha` and the ref are not written, because `L` does not move). Otherwise one commit with the message
    `sync: <label> +<new> ~<changed> -0` and `push HEAD:<branch>` with no force. The label (the OS machine name, so a hostname does appear in the private repository, as the umbrella's `written_by`
    allows) is `Environment.MachineName`
    reduced to `A-Z a-z 0-9 . _ -` and at most 40 characters, else `unknown` (display only: never identity, never in a
    path); the same label is `written_by` of new records. Any failure after the first file was written (a write, the stage,
    the commit) resets the clone to `L` and cleans it again, removes any temporary file, and is `Failed`; the reset, the clean and the removal run with `CancellationToken.None` (best effort).
11. **The push result.** Success: decision 12. A non-fast-forward (the remote moved on: another machine pushed first) is
    `NonFastForward`: nothing was published, the rows keep their `pushing_stamps` and pending stamps (harmless: the next
    pull recognises a stamp it can prove it sent, or sees none of them in the repository), and **the caller runs the cycle
    again** (1b-i-4). Any other failure of the push is `Failed(kind, message, published)`, kind being the git client's
    failure kind text, and `published` says whether the commit may have reached the remote: **`Yes` only after the push
    command succeeded** (a later failure of the finalize); **`Unknown`** for `Cancelled`, `TimedOut`, `Network` and
    `OutputTooLarge` of the push command (it may have completed before the answer was lost), and for `Other` unless the
    stderr shows the host refused it (`[remote rejected]`, `[rejected]` or `hook declined`); **`No`** for every other kind
    (`Auth`, `RemoteNotFound`, `RemoteBranchMissing`, `NoGit`, `GitTooOld`, `NotARepository`), for a refusal in `Other`, for
    anything before the push command started (a cancellation before it included) and for the local steps. (A
    `NonFastForward` is its own outcome, not a `Failed` kind.) The stamps written in decision 9 are what lets the next pull tell. The clone is left as it is after a
    failed push or finalize (its `HEAD` may be the unpublished or the published commit; decision 2 resets it next time).

### After a successful push

12. **Finalize.** One immediate transaction, in this order, for every entry that was written, left unchanged or finalized
    after a no-op (decision 6): re-read the row; set `base_json` to the record that was pushed (for an unchanged entry,
    the record at `L`), **keeping every other column**: the comparison columns (`scanned_*`, `local_*`) are **not touched**
    (they describe the entry at the scan; an edit since then must still be seen by the next scan); for each written group,
    **clear the pending stamp only when it equals the stamp that was pushed** (a different one, which cannot arise under
    the lock, stays pending against the new base); remove the pushed groups from `pushing_stamps` (the column is `NULL`
    when none is left); clear the writer's own block marks; then write `_meta.git_sync_last_sha` with the new commit. A
    scan straight after a push stamps nothing and writes nothing. After the transaction commits, `update-ref
    refs/total-recall/last-synced` follows (`UpdateLastSyncedAsync`), as in the pull: `_meta` first, then the ref, so a
    crash between them is repaired by the fetcher (a ref that is a strict ancestor of `L` is moved in place). A failed
    finalize or ref write after a published push is `Failed` with `published` `Yes`; the next pull recognises its own
    stamps through `pushing_stamps`.
    **L is the pushed commit**, which `rev-parse HEAD` reports after the commit. Once the push command has succeeded the
    finalize, the `_meta` write and the ref write run **whatever the token says** (they are local and short): a cancelled
    token would otherwise leave to the next pull what this step can finish.

### Reporting, cancellation, crashes

13. **The result.** `PushOutcome` is `Pushed(PushSummary)`, `NothingToPush(PushSummary)`, `NotReady(reason)`,
    `NonFastForward(PushSummary)` or `Failed(kind, message, published)`, where `published` is `No`, `Yes` or `Unknown`
    (`Yes` only after the push command succeeded; `Unknown` when it was cancelled or lost its response). `PushSummary` is plain data: the commit, `New` and
    `Changed` (entries whose bytes changed, which is also the commit message's `+` and `~`), `Unchanged` (entries finalized
    with no change), the list of written ids, `Blocked` (id, reason), `Deferred` (id, reason) and `Skipped` (id, reason). It holds
    no text for the user. A blocked, deferred or skipped entry does **not** make the push fail: the others go out, and the
    cycle reports them.
14. **Cancellation** stops the push between steps, never inside a transaction: the token being cancelled, an
    `OperationCanceledException` and a git call that fails with the client's `Cancelled` kind all give `Failed("Cancelled")`
    (`published` per decision 11). A cancellation is recorded nowhere as a block or an issue. After the push command has succeeded the token is
    no longer consulted (decision 12).
15. **Crash safety**, step by step: before the plan transaction commits, nothing has changed; after it and before the push,
    only `pushing_stamps` and block marks exist (a retry rewrites them; the clone is reset first); after a push and
    before finalize, the repository holds records whose stamps the row remembers, which the pull's own-push rule turns
    into a base and a cleared pending stamp while still merging what another machine changed in between; after finalize
    and before the ref, the fetcher repairs the ref. A killed push leaves a commit in the clone that the next push never
    publishes (reset to `L`).

### Interplay with the scan and the pull

16. **A scan straight after a push stamps nothing** (decision 12), and **a pull that follows a rejected push** meets
    `pushing_stamps` for groups that never reached the repository: the merge rules read a record that does not carry the
    stamp as "not mine" and apply what is there (the row's base is unchanged, so the pending groups are pushed again).
17. **Nothing in the push reads the working tree for a decision**: the records at `L` come from git objects (decision 3);
    the working tree is only written.

## Requirements on the later parts

- **1b-i-4 (the cycle)** calls the push only after a **complete** pull in the same cycle and under the lock, runs the cycle
  again after `NonFastForward` (at most three times per tick), records `git_sync_last_ok` and `git_sync_last_error` from the
  outcomes, and reports blocked, deferred and skipped entries. It must not call the push when the pull was `Refused`,
  `Failed` or incomplete, **removes a stale `.git/*.lock` under the held lock** before the cycle (a killed push can leave
  `index.lock`, which would fail the reset of decision 2 on every push), and is told that **every deletion (local or remote), review and
  re-push in the pull makes it incomplete, so no push happens on a machine holding one until 1b-ii and 1b-iii**.
- **1b-ii (deletions)** adds pushing a local deletion (a `LocalDeletions` row), writing back a record whose deletion lost to a
  pending text edit (`base_json` as the starting record), `repush_groups`, excludes an entry with an open `held-delete` item (the umbrella's step 4), and removes the `record-missing` and
  `not-reconciled` exclusions it makes unreachable.
- **1b-iii (secrets and review)** adds the secret detector to the writer's limits (its own `blocked_reason`, `allowed_hash`),
  replaces `blocked-elsewhere`, and is the milestone before which nothing may call the push.

## Interfaces (for the plan)

- `Core.SyncPush`: `candidateFor`-style selection from (row facts, entry view, record facts, issue facts) to
  `Left of reason | Send of PushPlan`, `groupsToWrite`, the new-record builder, `afterPush` (row from row, the pushed
  stamps and record text), `commitMessage`, `machineLabel`, `blockReasonName`. Pure and total.
- `Infrastructure.GitSync.GitSyncPusher(GitClient, SqliteStore, branch, machineLabel)`; `PushAsync(apply, scan, ct)`. Test
  seams: `BeforePlanTransaction`, a step hook inside the two transactions (`reread`, `plan`, `pushing`, `block`,
  `finalize`, `meta`, `ref`, `commit`), `BeforePush` (where a test makes the remote move), `TransactionCount`.
- `SyncGit` / `GitClient`: two new closed local commands next to the existing ones (`Clean` and the `Stage` scopes, which stay as they
  are): `StagePaths` (`add -f -- <paths>`, each path checked to be a record path) and the staged set (`diff --cached --name-status -z
  --no-renames`). They enter the places that enumerate commands: `SyncGitTests.fs` (the argument lists, the allowed-dashed list, the list
  of all commands), and the client tests that call the new methods.
- `GitSyncRows` / the store transaction already take the caller's transaction; the import-log query is one statement on it.

## Tests

Core (`SyncPushTests.fs`): the selection for every exclusion and every pending group; a new record from every field
(absent project, scope, source, origin); the patch groups and the "not greater" rule; the pin that raises a tier; the row
after a push per group (pending equal and different); the commit message and label; totality over generated rows.

Infrastructure (a temporary database seeded through `SqliteStore` and the real local bare repository and clones of the
pull's rig; the embedder is a stub; nothing uses pins from other machines or deletions as data): a pushed new entry
(record bytes exact, `origin` from the host import and none from `git-sync`, `written_by`, commit message, rows, `L`, ref),
every group alone and together on a changed entry (the other fields and unknown fields untouched), tags, a pin and an unpin,
a promotion; no commit when nothing is pending; a no-op patch; a limit block (size, tags, tag length) with its marks, cleared
when the entry passes; each exclusion (issue, rejected path, scan-skipped, not-reconciled, record missing, unreadable record,
another block reason); a second connection editing text, tag, pin and tier between the scan and the plan transaction
(deferred, never pushed, the next scan stamps it); a fault at each step of the two transactions (everything as before; the
plan fault leaves no file); the remote moving before the push (non-fast-forward: rows keep `pushing_stamps`, the clone is reset
by the next push, and the pull that follows recognises own stamps and clears them); every other push failure kind; a lost
response (the push reached the remote, the finalize never ran: the next pull adopts the record as its base and clears the
pending stamp, while merging another machine's tag change made in between); `_meta` moved and the ref not (repaired by the
next fetch); a left-over commit and an untracked file in the clone before the push (not published); a scan right after a
push stamps nothing; an incomplete pull and a last synced commit that is not the pull's head (`NotReady`, nothing written); a clone ahead of or beside `L` (a left-over commit after each kind of failed push, with the remote not moved so the fetch is `NothingNew`: reset, never published, pushed on the next try); a clone that cannot be reset; cancellation at
each step; a busy database; a record at `L` that cannot be read; shapes in the tree (symlink at an id, a rejected path);
files written atomically (no temporary file survives); a record with a source tool this version cannot parse (kept through a text edit),
a stale pending stamp (that group skipped, the others written), an unreadable `base_json`, a repository entry occupying a record's
file or its shard path (that entry excluded, the others pushed), a left-over commit after each kind of push failure with the remote
not moved (the fetch is `NothingNew`, the clone is reset, never published), temporary files only under `.git` and none left after any
fault, a blocked row whose entry went back to its base (its mark cleared), a fabricated `LocalDeletions` entry (nothing written or
removed), a no-op entry (counted `Unchanged`, finalized, not in the commit message), the mapping of each failure kind to `published`,
a cancelled token after a successful push (the finalize still runs), a pushed commit with an unconventional but valid record (a
non-canonical blob is normalised only when it changes), a repository that tracks `memories/.gitattributes` with `* ident` (text containing `$Id: x$` is committed byte for byte) and a foreign case-variant path (`memories/AB/x.json`, `memories/ab/ABCDEF12.json`) leaving the new entry out as `path-occupied` on every host, a written file removed between the write and the stage, for a changed entry (git stages a deletion, the staged-set check refuses) and for a new one (the stage fails): `Failed` and the clone reset either way, a push of 250 new entries (three stage batches),  `LocalDeletions`, `record-missing` and `not-reconciled` exercised with a hand-built completed `ApplySummary` (a real pull holding a
deletion is incomplete), and `git grep` shows that nothing outside the pusher and the tests calls it.

## Departures from the umbrella design

- **The cycle is a separate milestone** (1b-i-4): the umbrella's step 4 retry "back to step 2 at most three times" and the
  lock are the cycle's; here a non-fast-forward is returned.
- **No deletions** are pushed (1b-ii) and no file is ever removed; the commit message always ends `-0`.
- **No secret scan** (1b-iii); the writer's own size limits are here, as the scan's design left them to the writer.
- **`_meta.git_sync_last_sha` is written in the finalize transaction**, with the rows, and the ref follows; the umbrella
  lists the base, the pending stamps, `repush_groups` and `L` as one step.
- **The finalize leaves the comparison columns alone**, so an edit after the scan is still seen; the umbrella is silent.
- **A group is not written with a stamp that is not greater than the record's** (defensive), and an entry that changed
  since the scan is deferred (the same guard as the apply's), where the umbrella has the scan alone.
- **The reset of the clone is `reset --hard` and `clean -fd`** as the umbrella says, with an `info/attributes` file that switches every filter off written first (ignored files cannot be published, because only the written paths are staged); the working tree is never read for
  a decision.

## Acceptance criteria

The ticket's criteria, numbered here for the tests to name them.

1. Given a completed pull and a memory created on this machine that the repository does not hold, when the push runs, then the repository gains one commit with one record for it (its text, type, tags, source tool, project, scope, creation time, tier, pin state and the machine's label as writer), and the memory's sync record holds that record as its last synced version with nothing pending.
2. Given a new memory that was imported from a host file here, when it is pushed, then its record carries the import's origin; given one that arrived by sync, then its record carries none.
3. Given a memory whose repository record exists and whose text, tags, pin state or tier changed here, when the push runs, then only the changed parts of the record change (every other field, including fields this version does not know, stays byte for byte), and the stamps written are the ones the scan issued.
4. Given a successful push, when it ends, then the last synced commit is the new commit, and a scan run right after stamps nothing and writes nothing.
5. Given nothing pending, or changes that alter no file, when the push runs, then no commit is made, nothing is pushed and the last synced commit does not move.
6. Given a memory whose record would exceed 256 KiB, has more than 64 distinct tags or has a tag over 128 characters, when the push runs, then no file is written for it, it is listed with its reason and marked blocked, the other memories are pushed, and a later push after the memory is fixed (or back to what the repository holds) sends it or clears the mark.
7. Given a memory with an open apply failure or skip, an id the scan skipped, or an id that a rejected repository path names, when the push runs, then nothing is written for it and it is listed with its reason.
8. Given a memory with no last synced version while the repository holds its record, one with a last synced version while the repository has no path for it, a record or last synced version that cannot be read, a repository entry that occupies the record's file or folder, or a block that a later part owns, when the push runs, then nothing is written for it, it is listed with its reason and the other memories are pushed; given a memory deleted here, then no file is written or removed for it.
9. Given a memory edited, pinned or moved after the scan and before the push, or another process holding the database for writing, when the push runs, then that memory is not sent and is listed as changed since the scan (or the push fails as busy with nothing written), the other memories are sent, and the next scan stamps the edit.
10. Given a failure injected at each step before anything is published (the database updates, each file write, staging, the commit), when the push runs, then nothing is published, every memory and its pending changes, last synced version and last synced commit are exactly as before (only a note of what was about to be sent and the marks of memories that cannot be sent may remain, and the next push rewrites them), the local copy holds no changed or left-over file, and the failure is reported; a failure inside the database updates leaves nothing at all.
11. Given a failure after the push was accepted (updating the database or the last synced marker), when the push ends, then it is reported as published, and the next pull recovers every pushed change without loss and without sending it twice.
12. Given the remote moved after the pull (another machine pushed first), when the push runs, then nothing is published and the outcome says so, every memory keeps its pending changes, the next push starts from a clean copy of the last synced commit, and a pull that follows applies the other machine's change.
13. Given a push whose answer was lost (the commit reached the remote and the database was never updated), when the next pull runs, then each sent change becomes the last synced version with nothing pending, and a change another machine made in between is merged, not lost.
14. Given a cancelled push, or a network, authentication, timeout or remote-rejection failure, when the push ends, then the outcome names the failure and whether the push may have reached the remote, nothing in the repository is overwritten, and a cancellation is not recorded as a blocked memory or a problem.
15. Given a push that succeeds, when it ends, then the last synced commit and its marker agree, and after a crash between updating the two the next fetch repairs them.
16. Given a pull that did not complete, or a last synced commit that is not the pull's head, when the push runs, then it reports not ready and changes nothing; given a commit or an untracked file left in the local copy by an earlier failed push (also when the remote has not moved since), then the push resets the copy, never publishes them, and sends what is pending.
17. Given the existing automated checks, the shim tests, the tool catalog check and the release build, when they run, then they pass and the release build shows no new warning; given the contributor guides (`AGENTS.md` and the two in `src/TotalRecall.Core` and `src/TotalRecall.Infrastructure`), then they describe the new pieces; and nothing outside the push calls it.
