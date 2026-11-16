# Git sync, milestone 1b-i-1: the scan

Date: 2026-11-17. Status: draft for review. Part of #93 (git sync). This is the first third of the umbrella's milestone
1b-i ("cycle core"), which the owner split into three: **1b-i-1 the scan** (this document), 1b-i-2 the pull (fetch and
apply) and 1b-i-3 the push and the cycle. The umbrella design is `2026-11-15-git-sync-design.md` (the sections "The sync
cycle", step 1, "Ordering: a hybrid logical clock" and "What syncs"); where this document is more precise it wins for
this milestone, and every departure is listed under "Departures from the umbrella design".

## Goal

Give the sync cycle its first step: a local-only pass that finds what changed in this machine's memories since the last
pass, **stamps** each change with a hybrid logical clock stamp and **persists** the stamp at once, so that a change made
offline carries the time it was found and not the time it is finally pushed. Nothing leaves the machine and nothing is
read from the network: the scan needs no `git`, no clone and no remote.

Success: after a scan, `git_sync_entry` holds one row per eligible memory with the hashes, the local sticky flag and
tier it was last seen with and a pending stamp for every group the user changed; the memory tables are byte-for-byte
what they were; and a second scan with no edits in between changes nothing at all.

## Non-goals

Left to the later parts of milestone 1b, and **not** built here:

- Anything that touches the repository or the network: fetch, apply, the writer, commit, push, the lock and the cycle
  that calls the scan (1b-i-2 and 1b-i-3). The scan assumes the caller holds the sync lock; it takes none.
- Acting on a local deletion: the scan only reports it (decision 8). Deleting, holding, `forgotten` rows and the delete
  guard are 1b-ii.
- Size limits on a record (256 KiB, 64 tags, 128-character tags), which need the rendered record: the writer (1b-i-3).
- The secret scan and `blocked_hash`: 1b-iii. The scan never writes `blocked_*`, `allowed_hash`, `repush_groups` or
  `pushing_stamps`.
- Review items, aliases, arrivals, the `sync` block of `status`, any command or tool that runs a scan.

## What exists and what is new

Built already: the record codec, `SyncStamp` (`Clock.issue`, `Clock.guardStartup`, `Clock.serialize`), the merge
decisions, the row tables (migration 18), `SqliteStore` and its transaction, the sync lock, the setup commands. The new
pieces:

- `TotalRecall.Core.SyncScan` (F#, pure): the group hashes, the local view of an entry, and `decide`, which turns one row
  and one local entry (or the absence of either) into a `ScanAction`. Shared with the apply of 1b-i-2, which must set the
  hashes, `local_sticky` and `local_tier` from the view of the stored entry **after** its own write, in the same
  transaction, or the next scan would read a pulled change (a promotion, an unpin, a text) as a local edit and stamp it
  (and a hash taken from the record instead of the stored entry would differ, for instance a `Some ""` scope that the
  store keeps as the empty text).
- `TotalRecall.Infrastructure.GitSync.GitSyncRows`: typed access to `git_sync_entry` (the row record, read, insert,
  update), also used by 1b-i-2 and 1b-i-3.
- `TotalRecall.Infrastructure.GitSync.GitSyncScanner`: the read snapshot, the stamping and the one write transaction.
- Two `_meta` key names in `SyncIdentity.MetaKeys`: `Hlc` (`git_sync_hlc`) and `ClockWarning` (`git_sync_clock_warning`).

## Decisions

### Which entries the scan looks at

1. **Eligible** (umbrella "What syncs"): a `ContentType.Memory` entry in the hot, warm or cold table whose id matches
   `^[0-9a-f][0-9a-f-]{7,63}$` (the rule already in `SyncValidation`). A sticky memory is a hot memory with `sticky = 1`.
   Knowledge entries are never read except by id (decision 8).
2. **An entry with any other id is not eligible.** It gets no row and no stamp. The scan records it in
   `git_sync_issue` (`kind = 'ineligible-id'`, `detail` the tier). The id and the tier are read before the entry is mapped, so
   an entry that cannot be read (decision 3) still gets its issue. An issue row is **written only when it is absent or its
   `detail` differs, and removed only when it is present and the id is no longer in any memory table**; a skipped entry
   still counts as existing. (The umbrella says `status` lists these; `status` is 1b-iii.)
3. **One consistent snapshot.** The scan reads inside one `BEGIN` read transaction on its own connection (WAL mode, so
   writers are not blocked), so a concurrent `Move` between tables can neither hide an entry nor show it twice. The
   scanner runs its own `SELECT` per table with the transaction set (the store's `List` and `Get` open their own
   commands and cannot join a snapshot), reads the `sticky` column from the hot table, and maps rows with the store's row
   mapper. The scan never writes a memory, knowledge, vector or `import_log` row.
   **An entry that cannot be read** (an unknown `source_tool`, tags that are not valid JSON) **or whose id is in two memory
   tables** is **skipped for this scan** and listed in the result's `Skipped` list with its id and the reason: its row is
   untouched and it is never reported as a local deletion. One bad entry never fails the scan.
   Entries are processed **in id order** (ordinal), so the counters of the stamps do not depend on the table scan order.

### The local view and the hashes

4. **The local view of an entry** is exactly `SyncMerge.LocalEntry`: the content fields (text, project, scope, entry
   type, source tool, `created_at` in milliseconds), the tags (sorted, unique, through `SyncRecord.normaliseTags`), the
   tier and the sticky flag. The store holds an absent project as null and an absent scope as the empty text; **the view
   maps an empty scope to `None` and an empty project to `None`**, the same mapping the apply of 1b-i-2 writes in reverse.
   Fields that never sync (summary, metadata, parent, `source` path, usage counters, `updated_at`, decay) are not in the
   view and cannot change a hash.
5. **Two hashes, both SHA-256 in lowercase hex of a length-prefixed UTF-8 encoding** (so no two different field lists
   encode alike): the **content hash** over the content fields in the order above, with an absent value encoded
   differently from an empty one; the **tags hash** over the normalised tag list. They are defined once, in
   `SyncScan.contentHash` and `SyncScan.tagsHash`, with fixed golden vectors in the tests, because the hash is stored in
   `scanned_content_hash` / `scanned_tags_hash` and a change of encoding would stamp every entry once.

### What the scan decides per entry

`SyncScan.decide` is pure. Its input is the row (or none), the local view (or none, and whether the id exists as a
knowledge entry) and nothing else; its output says what to write to the row and which stamps to issue. Let *changed*
mean differs from what the row holds.

6. **A new entry** (no row): a row in state `active`, `base_json` NULL, the three stamps for **content, sticky and tier**
   (the umbrella: "all groups stamped"), the content and tags hashes, `local_sticky` and `local_tier`.
7. **An existing `active` row and an entry present:**
   - content hash *changed*: issue a new `content` stamp, store the new hash. A new stamp replaces an earlier pending one,
     so a second edit made while the first is still unpushed is re-stamped (umbrella, "Ordering").
   - tags hash *changed*: store the new hash. **No stamp** (tags merge as a three-way set and never conflict).
   - the sticky flag *changed* from `local_sticky` (a pin or an unpin, the only actions that change it): issue a `sticky`
     stamp, store the flag.
   - the tier *warmer* than `local_tier` (hot above warm above cold): issue a `tier` stamp (a promotion event, whatever
     caused it: the user, or the machine because the entry was used). A *colder* tier (a sweep, budget eviction,
     compaction, `demote`) stores the new `local_tier` and issues **nothing**: demotion stays local. `local_tier` always
     follows the current tier.
   - **the clearing test comes first.** When the row has a base and the current tier is **not warmer than the base
     record's tier**, no tier stamp is issued (a tier that rose above `local_tier` but not above the base's counts as no
     event) and a **pending `tier` stamp is cleared**: a promotion that was undone before it was pushed is no event any more, so the push can never
     publish a colder tier under a promotion stamp (umbrella: "a move to a colder tier never syncs"; the merge decisions
     use the same condition). A row without a base keeps its tier stamp (a new record carries the tier it has).
   - a row whose `local_sticky`, `local_tier`, `scanned_content_hash` or `scanned_tags_hash` is NULL (nothing was stored)
     is treated as "nothing seen": the current value is stored and no stamp is issued for that group.
   - an unchanged entry writes nothing.
   Stamps are issued per group independently, so a change is attributed to the group that really changed. Apart from the
   tier rule above, the scan never clears or lowers a pending stamp; the push and the apply do that.
8. **An `active` row with no memory entry:**
   - the id exists as a **knowledge** entry (a memory retyped to knowledge): the row moves to state `left`, its stamps
     and its `repush_groups` are cleared, its base is kept; nothing is deleted anywhere. A later record with that id is
     ignored (the merge decision for a `left` row).
   - the id exists in **no** table: a **local deletion**. The row is left exactly as it is, and the id is returned in the
     scan result's `LocalDeletions` list with two flags (the row has a base; the row has `pushing_stamps`) for milestone
     1b-ii and the apply, which decide what to do. The scan deletes nothing, drops nothing, writes nothing for it and never
     turns it into `forgotten`. **In particular it never drops a base-less row**: the scan cannot see R, and the merge
     decisions treat "no base, no entry, record present in R" (the memory is not re-inserted, the row becomes `forgotten`)
     differently from "record absent from R" (the row is dropped); dropping the row here would turn the first case into a
     re-insert of a memory the user deleted. The apply of 1b-i-2 must therefore reach a row with no base and no entry **whose id is absent from R**: the
     umbrella's visit set (rows with no base whose id is in R's tree) already reaches the case where R holds the record, but
     not this one, and the merge table's "drop the row" case needs it (see "Requirements on the later parts").

9. **A `forgotten` or `left` row whose id exists again as an eligible memory** (the user re-stored it, imported it, or
   retyped it back) returns to `active` **with its base cleared** (all other columns reset) and is stamped like a new
   entry (decision 6). A `forgotten` or `left` row with no entry stays as it is.
10. **Own pushes stay the apply's job.** A row with no base but `pushing_stamps` set and no entry is a local deletion
    (decision 8): reported, untouched; recognising that its push reached the repository is the apply's (the merge table's
    "own push" rows).

### Stamps and the clock

11. **The clock.** The scan loads the persisted clock (`git_sync_hlc`, `Clock.tryDeserialize`) and applies
    `Clock.guardStartup` once per scan: a clock more than 24 hours ahead of the wall clock is reset to the wall clock; an
    absent value starts from the wall clock; a value that is present but unreadable is replaced by the wall clock; the
    reset and the unreadable value each add their kind to the warning (below). A new stamp is issued with
    `Clock.issue` from the wall clock, the local clock and a **floor stamp: the greater of the group's base stamp (the
    stamp the row's base record carries, read from `base_json`; none when there is no base or it is unreadable) and the
    group's pending stamp**, so a stamp is always greater than the clock, than anything this machine issued before and
    than the group's base **and** than the stamp it replaces. (`Clock.issue` does not move the clock when the floor is
    far in the future, so the pending stamp must be part of the floor or a second edit would re-issue the same stamp, and
    the own-push rule could then clear a real edit.) A far-future floor still yields a stamp above it and adds the kind `far-future` to the warning. The new clock state is persisted with the stamps; the scan adds to the warning and never clears it (showing and
    clearing it belong to `status`, milestone 1b-iii).
    **`Clock.issue` can fail** (`Exhausted`: a floor stamp at the very end of the clock range, which a valid record can
    carry). For that **group** of that entry the scan issues **no stamp** and stores **nothing new for the group** (the
    content or tags hash, `local_sticky` or `local_tier`, whichever belongs to it), so the next scan finds the same change
    and tries again; it adds the entry to the result's `Skipped` list with the group and the reason, adds the kind `exhausted` to the warning, and
    **writes everything else**, including the entry's other groups. One such group never stops the others.
    **The warning is a set of kinds, only ever added to.** The kinds are `clock-reset`, `clock-unreadable`, `far-future`
    and `exhausted`; `git_sync_clock_warning` holds their names, sorted and joined by `,`, behind the fixed prefix `clock: `
    (no time, no id: the details are in the result). A scan adds the kinds it met that are not stored yet and writes the key
    only when that changes it, so a condition that recurs on every scan (a skipped group, a far-future floor) and a mix of
    conditions in one scan never make an otherwise idle scan write (decision 14).
12. **The machine id** is `_meta git_sync_machine` (written by `init` and `join`), read inside the snapshot. A scan on a
    store with no valid machine id refuses (`ScanOutcome.NotJoined`) and writes nothing. The scan does **not** check the
    rest of the join state (remote, repository id, host): the caller, the cycle of 1b-i-3, has already confirmed it with
    `SyncIdentity.evaluate`, which is what stops a copied data directory (same machine id on two machines) from stamping.
13. **One write transaction.** After the snapshot, all row changes, issue changes, the new clock and the warning are
    written in **one** `BEGIN IMMEDIATE` transaction. A failure or a lock timeout leaves the rows and the clock exactly as
    they were, and the scan reports it (`ScanOutcome.Failed`, with the reason); the next scan finds the same changes and
    stamps them. A change made to an entry between the snapshot and the write is found by the next scan, because the
    stored hash is the snapshot's, not a re-read. A clock reset or a new warning kind counts as something to write; a scan with nothing to write opens no write
    transaction. The tests reach two seams that are internal to the scanner (as `SqliteStoreTransaction.BeforeStep` is for
    the store transaction): a hook called between the scan's table reads, and a fault hook before each group of writes.
14. **Idempotent.** Two scans in a row with no edit between change nothing: no row, no issue, no clock and no stamp.

### The result

15. `ScanOutcome` is `Done(ScanSummary)`, `NotJoined` or `Failed(reason)`. `ScanSummary` carries the counts (new,
    content, tags, sticky, tier, left, reactivated, ineligible), the `LocalDeletions` list (id and the two flags) and the `Skipped`
    list (id and reason). It is plain data: the cycle of 1b-i-3 reads it, and no text for the user is produced here.

## Requirements on the later parts

This milestone fixes what the scan leaves in the rows. The following are requirements on 1b-i-2 and 1b-i-3 (and 1b-ii),
repeated in their own definitions, because the scan's results are only correct if they are honoured:

- **The apply writes what the scan will compare** (the Goal note): after it writes a pulled change it sets the content and
  tags hashes, `local_sticky` and `local_tier` from the view of the stored entry, in the same transaction.
- **The apply reaches a row with no base and no entry whose id is absent from R** (decision 8). This extends the
  umbrella's visit-set text; it is needed for the merge table's "drop the row" case.
- **An entry the scan listed in `Skipped` for a group is exempt for that group:** the apply's "the scanned hash, `local_sticky`
  or `local_tier` differs from the row's" deferral must not hold `L` back for it (it would on every cycle, and no push
  would ever happen), the apply must not overwrite that group from the repository, and the push must not write it. The
  entry is retried by every scan and listed, like an `apply-failed` entry.
- **1b-ii acts on `LocalDeletions`** and is the only part that creates `forgotten` rows.

## Interfaces (for the plan)

- `Core.SyncScan`: `contentHash`, `tagsHash` and `decide`. `decide` takes the row (a record of the stored columns it needs:
  state, whether a base exists and its tier and stamps, whether `pushing_stamps` is non-empty, the two hashes,
  `local_sticky`, `local_tier`, which stamps are pending; the scanner parses `base_json` for the base facts, and a
  `base_json` that cannot be read means "has a base, tier unknown (no clearing), no floor"), the local view
  (`SyncMerge.LocalEntry`, or none) and whether the id exists as a knowledge entry, and returns one action: insert a new
  row, update named groups (each marked "stamp" or "store only"), move to `left`, reactivate, report a local deletion, or
  no change. It issues no stamp itself (the clock is I/O state): the scanner turns each "stamp" into a `Clock.issue` call.
- `Infrastructure.GitSync.GitSyncRows`: the row record and its read, insert and update, on the caller's connection and
  transaction. Used again by 1b-i-2 and 1b-i-3.
- `Infrastructure.GitSync.GitSyncScanner`: constructed with a connection and a wall-clock source; `Scan()` returns a
  `ScanOutcome`.

The exact signatures are the plan's. What is fixed here: `decide` is pure and total, the hashes live in Core, and the
scanner is the only writer of rows and issues in this milestone.

## Tests

Core (`SyncScanTests.fs`): golden vectors for both hashes; fields that never sync leave the hash unchanged; tag order
and duplicates do not change the tags hash; absent versus empty values hash differently; every row of decisions 6 to 10
as a table test; `decide` is total over its input space.

Infrastructure (`GitSyncScannerTests.cs`, a temporary SQLite database seeded through `SqliteStore`, no git, no network):
each acceptance criterion below; idempotence; the memory tables compared before and after every scan; a fault injected
in the write transaction; a concurrent writer moving an entry between the scan's table reads; a base stamp ahead of the
wall clock; a persisted clock a year ahead; a missing machine id; an id that is a path; knowledge entries ignored; a mixed set of clock conditions in one scan (a reset,
a far-future floor and an exhausted group) written once and then left alone by a second scan; an ineligible-id issue that
already exists is not rewritten.

## Departures from the umbrella design

- **`left` is set by the scan.** The umbrella lists "`forgotten`/`left` rows" under 1b-ii. The scan is what sees a retype,
  and leaving it for later would leave a retyped memory pushing stamps, so the transition `active -> left` and the
  return to `active` are here; `forgotten` (a deletion) is left to 1b-ii.
- **Local deletions are detected here and only reported.** The scan already reads every row and every table, so it
  names the ids and 1b-ii acts, instead of 1b-ii adding a second pass.
- **The size limits are the writer's.** The umbrella's AC "an oversized entry is listed and not pushed" needs the rendered
  record, so it belongs to 1b-i-3.
- **Ineligible ids are recorded by the scan** (the umbrella says only that they are reported in `status`).
- **One batch write instead of "persisted immediately in the entry's row".** All stamps, rows and the clock go in one
  transaction, which is stronger (a stamp and the clock that issued it are never apart) and still survives any crash.
- **The clock guard runs on every scan**, not only "at startup": the scan is the only place the persisted clock is read in
  this milestone, and a scan is cheap.
- **The scan reads every eligible entry but skips one it cannot read** (an unknown source tool, invalid tags, an id in two
  memory tables) and lists it, instead of failing; a skipped id still counts as existing, so it never produces a deletion
  and keeps its `ineligible-id` issue.
- **The visit set grows** by one case (a base-less row with no entry whose id is absent from R), a requirement on 1b-i-2.
- **The tier stamp can be cleared by the scan** when a promotion is undone before it is pushed (the umbrella is silent;
  the merge decisions use the same condition).
- **Retype back reactivates a `forgotten` row** as well as a `left` one (the umbrella lists `forgotten` rows under 1b-ii;
  the transition itself is the scan's, the creation of `forgotten` rows is 1b-ii's).

## Acceptance criteria

1. Given memories in the hot, warm and cold tiers (one of them sticky), when the scan runs, then each has a row in state
   `active` with no base, a content hash and a tags hash, its sticky flag and tier, and a pending stamp for content,
   sticky and tier; and no memory, knowledge, vector or import-log row has changed.
2. Given a scan with nothing changed since the last one, when it runs again, then no row, issue, stamp or clock value
   changes (including the clock), and it opens no write transaction.
3. Given an edit of a memory's text, project, scope, entry type or source tool, when the scan runs, then only its content
   group gets a new stamp, greater than the previous one and than the group's base stamp; a second edit before any push
   is stamped again, above the first.
4. Given a change of a memory's tags only, when the scan runs, then its tags hash changes and no stamp is issued; given a
   change of only fields that never sync (summary, metadata, usage counters, decay), then nothing changes.
5. Given a pin or an unpin, when the scan runs, then the sticky group is stamped; given a promotion to a warmer tier
   (made by the user or by the machine), then the tier group is stamped; given a demotion or a sweep to a colder tier,
   then the stored tier follows it and no stamp is issued; given a promotion that was pending and is undone (the tier is
   no warmer than the last synced tier) before it is pushed, then its tier stamp is cleared.
6. Given an edit made while offline, when the scan runs with no network and no `git` available, then the stamp is
   persisted at once, carries the time of the scan, and a later scan after a long pause stamps a newer edit above it.
7. Given a persisted clock more than 24 hours ahead of the wall clock, when the scan runs, then the clock is reset to the
   wall clock and a warning is recorded (also for a clock value that cannot be read); given a base stamp ahead of the wall
   clock, then a new stamp is issued above it, and a second edit before any push is stamped above the first even then;
   given a base stamp at the very end of the clock range, then that group of that entry is skipped and listed while every other
   group and entry is stamped, and a second scan with the same condition writes nothing.
8. Given a memory retyped to knowledge, when the scan runs, then its row moves to `left`, its stamps are cleared, its base
   is kept and nothing is deleted; given it is retyped back, then the row is `active` again with its base cleared and all
   groups stamped; given a `forgotten` row whose id exists again as a memory, the same.
9. Given an `active` row whose id exists in no table, when the scan runs, then the id is reported as a local deletion
   (saying whether the row has a last synced version and whether it has in-flight push stamps) and the row and the data
   are unchanged, whether or not the row has a last synced version; given a `forgotten` or `left` row with no entry,
   nothing happens.
10. Given a memory whose id does not match the id rule, when the scan runs, then it has no row and no stamp and is listed as
    an `ineligible-id` issue, which disappears when the memory does; knowledge entries and entries of other types are
    never touched; given a memory that cannot be read (an unknown source tool, invalid tags) or whose id is in two memory
    tables, then it is skipped and listed, its row is untouched, it is not reported as deleted and every other memory is
    stamped.
11. Given a concurrent writer that moves a memory between tiers while the scan reads, when the scan runs, then the memory
    is seen exactly once and is never reported as a deletion.
12. Given a failure while the scan writes its rows, when it ends, then the rows and the clock are as before, the scan reports
    the failure, and the next scan stamps the same changes; given a store with no valid machine id, then the scan writes
    nothing and reports `NotJoined`.
13. Given the existing automated checks, the shim tests, the tool catalog check and the release build, when they run, then
    they pass and the release build shows no new warning; and given the contributor guides (`AGENTS.md`,
    `src/TotalRecall.Core/AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`), then they describe the new pieces.
