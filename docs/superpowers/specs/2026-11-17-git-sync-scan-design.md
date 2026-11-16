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
  same hashes when it writes a pulled change, or the next scan would read the pulled change as a local edit.
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
   `git_sync_issue` (`kind = 'ineligible-id'`, `detail` the tier). The issue row is removed when the entry no longer
   exists as a memory. (The umbrella says `status` lists these; `status` is 1b-iii.)
3. **One consistent snapshot.** The scan reads inside one `BEGIN` read transaction on its own connection, so a concurrent
   `Move` between tables can neither hide an entry nor show it twice. Entries are read with the store's existing row
   reader; the scan never writes a memory, knowledge, vector or `import_log` row.

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
   - a row whose `local_sticky` or `local_tier` is NULL (nothing was stored) is treated as "nothing seen": the current
     value is stored and no stamp is issued for that group.
   - an unchanged entry writes nothing.
   Stamps are issued per group independently, so a change is attributed to the group that really changed. The scan never
   clears or lowers a pending stamp; the push and the apply do that.
8. **An `active` row with no memory entry:**
   - the id exists as a **knowledge** entry (a memory retyped to knowledge): the row moves to state `left`, its stamps
     and its `repush_groups` are cleared, its base is kept; nothing is deleted anywhere. A later record with that id is
     ignored (the merge decision for a `left` row).
   - the id exists in **no** table: a **local deletion**. The row is left exactly as it is, and the id is returned in the
     scan result's `LocalDeletions` list for milestone 1b-ii, which decides what to do. The scan deletes nothing, writes
     nothing for it and never turns it into `forgotten`.
9. **A `forgotten` or `left` row whose id exists again as an eligible memory** (the user re-stored it, imported it, or
   retyped it back) returns to `active` **with its base cleared** (all other columns reset) and is stamped like a new
   entry (decision 6). A `forgotten` or `left` row with no entry stays as it is.
10. **A row with no base and no entry** (a never-pushed entry that was deleted) is handled like any other `active` row
    with no entry (decision 8): reported, untouched. Dropping it or adopting an own push is the apply's job (the merge
    table's "drop the row" and "own push" rows).

### Stamps and the clock

11. **The clock.** The scan loads the persisted clock (`git_sync_hlc`, `Clock.tryDeserialize`) and applies
    `Clock.guardStartup` once per scan: a clock more than 24 hours ahead of the wall clock is reset to the wall clock
    and `git_sync_clock_warning` is set to a short text; an absent or unreadable value starts from the wall clock. A new
    stamp is issued with `Clock.issue` from the wall clock, the local clock and the **base stamp of the same group** (the
    stamp the row's base record carries, read from `base_json`; no base, or an unreadable one, gives none), so a stamp
    is always greater than the clock, than anything this machine issued before and than the group's base. The new clock
    state is persisted with the stamps. If the base stamp is far in the future the stamp is still issued above it and a
    clock warning is recorded.
12. **The machine id** is `_meta git_sync_machine` (written by `init` and `join`). A scan on a store with no valid machine
    id refuses (`ScanOutcome.NotJoined`) and writes nothing.
13. **One write transaction.** After the snapshot, all row changes, issue changes, the new clock and the warning are
    written in **one** `BEGIN IMMEDIATE` transaction. A failure or a lock timeout leaves the rows and the clock exactly as
    they were, and the scan reports it (`ScanOutcome.Failed`, with the reason); the next scan finds the same changes and
    stamps them. A change made to an entry between the snapshot and the write is found by the next scan, because the
    stored hash is the snapshot's, not a re-read. A scan with nothing to write opens no write transaction.
14. **Idempotent.** Two scans in a row with no edit between change nothing: no row, no issue, no clock and no stamp.

### The result

15. `ScanOutcome` is `Done(ScanSummary)`, `NotJoined` or `Failed(reason)`. `ScanSummary` carries the counts (new,
    content, tags, sticky, tier, left, reactivated, ineligible) and the `LocalDeletions` id list. It is plain data: the
    cycle of 1b-i-3 reads it, and no text for the user is produced here.

## Interfaces (for the plan)

- `Core.SyncScan`: `contentHash`, `tagsHash` and `decide`. `decide` takes the row (a record of the stored columns it needs:
  state, whether a base exists, the two hashes, `local_sticky`, `local_tier`, which stamps are pending), the local view
  (`SyncMerge.LocalEntry`, or none) and whether the id exists as a knowledge entry, and returns one action: insert a new
  row, update named groups (each marked "stamp" or "store only"), move to `left`, reactivate, report a local deletion, or
  no change. It issues no stamp itself (the clock is I/O state): the scanner turns each "stamp" into a `Clock.issue` call.
- `Infrastructure.GitSync.GitSyncRows`: the row record and its read, insert and update, on the caller's connection and
  transaction. Used again by 1b-i-2 and 1b-i-3.
- `Infrastructure.GitSync.GitSyncScanner`: constructed with a connection, the machine id and a clock source; `Scan()`
  returns a `ScanOutcome`.

The exact signatures are the plan's. What is fixed here: `decide` is pure and total, the hashes live in Core, and the
scanner is the only writer of rows and issues in this milestone.

## Tests

Core (`SyncScanTests.fs`): golden vectors for both hashes; fields that never sync leave the hash unchanged; tag order
and duplicates do not change the tags hash; absent versus empty values hash differently; every row of decisions 6 to 10
as a table test; `decide` is total over its input space.

Infrastructure (`GitSyncScannerTests.cs`, a temporary SQLite database seeded through `SqliteStore`, no git, no network):
each acceptance criterion below; idempotence; the memory tables compared before and after every scan; a fault injected
in the write transaction; a concurrent writer moving an entry between the scan's table reads; a base stamp ahead of the
wall clock; a persisted clock a year ahead; a missing machine id; an id that is a path; knowledge entries ignored.

## Departures from the umbrella design

- **`left` is set by the scan.** The umbrella lists "`forgotten`/`left` rows" under 1b-ii. The scan is what sees a retype,
  and leaving it for later would leave a retyped memory pushing stamps, so the transition `active -> left` and the
  return to `active` are here; `forgotten` (a deletion) is left to 1b-ii.
- **Local deletions are detected here and only reported.** The scan already reads every row and every table, so it
  names the ids and 1b-ii acts, instead of 1b-ii adding a second pass.
- **The size limits are the writer's.** The umbrella's AC "an oversized entry is listed and not pushed" needs the rendered
  record, so it belongs to 1b-i-3.
- **Ineligible ids are recorded by the scan** (the umbrella says only that they are reported in `status`).

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
   then the stored tier follows it and no stamp is issued.
6. Given an edit made while offline, when the scan runs with no network and no `git` available, then the stamp is
   persisted at once, carries the time of the scan, and a later scan after a long pause stamps a newer edit above it.
7. Given a persisted clock more than 24 hours ahead of the wall clock, when the scan runs, then the clock is reset to the
   wall clock and a warning is recorded; given a base stamp ahead of the wall clock, then a new stamp is issued above it.
8. Given a memory retyped to knowledge, when the scan runs, then its row moves to `left`, its stamps are cleared, its base
   is kept and nothing is deleted; given it is retyped back, then the row is `active` again with its base cleared and all
   groups stamped; given a `forgotten` row whose id exists again as a memory, the same.
9. Given an `active` row whose id exists in no table, when the scan runs, then the id is reported as a local deletion and
   the row and the data are unchanged; given a `forgotten` or `left` row with no entry, nothing happens.
10. Given a memory whose id does not match the id rule, when the scan runs, then it has no row and no stamp and is listed as
    an `ineligible-id` issue, which disappears when the memory does; knowledge entries and entries of other types are
    never touched.
11. Given a concurrent writer that moves a memory between tiers while the scan reads, when the scan runs, then the memory
    is seen exactly once and is never reported as a deletion.
12. Given a failure while the scan writes its rows, when it ends, then the rows and the clock are as before, the scan reports
    the failure, and the next scan stamps the same changes; given a store with no valid machine id, then the scan writes
    nothing and reports `NotJoined`.
13. Given the existing automated checks, the shim tests, the tool catalog check and the release build, when they run, then
    they pass and the release build shows no new warning; and given the contributor guides, then they describe the new
    pieces.
