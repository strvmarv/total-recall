# Git sync, milestone 1b-iii part 5: alias detection and skip-insert

Parent: #159, Part 5 (docs/superpowers/specs/2026-11-21-git-sync-review-secrets-design.md)

Date: 2026-11-25. Status: draft for review. Part of #93 (git sync); sub-ticket #165 of the parent #159. The parent spec
(`2026-11-21-git-sync-review-secrets-design.md`) governs: this document is the engineering design of its Part 5 and follows its
"Shared decisions". The umbrella design is `2026-11-15-git-sync-design.md` (its "Legacy alias resolution" section and the pull
tables). The two earlier parts this one builds on are `2026-11-23-git-sync-pin-review-design.md` (pin items: `new-pin`, `pin-edit`) and
`2026-11-24-git-sync-conflict-review-design.md` (conflict items and the `ConflictItems.Append` helper that this part uses). Where this
document is more precise it wins for this part; every departure is listed under "Departures".

## Goal

A memory imported from a host file (Claude Code, Copilot CLI, ...) before git sync existed has a random id on each machine, so the same
host file imported on two machines is two entries and, once synced, two records with the same `origin.hash`. The pull must never show
the person a duplicate, and a machine that holds the other id must not read the later disappearance of that record as a deletion.
After this part:

- the pull **detects an alias**: a record with no local entry and no row whose `origin.hash` maps in this machine's `import_log` to a
  *different, existing* local memory id. It records the group in `git_sync_alias` (canonical id = the lowest id, compared ordinally,
  which every machine computes alike) and **skips the insert**, so no duplicate appears. The skip is decided by the decision itself
  (the rules W1 to W14 are unchanged), writes no `git_sync_entry` row, never holds `L` back, and is decided the same way on every pull;
- the pull reads aliases **before the deletion checks**, so a catch-up that holds both the canonical record's add and the removal of the
  other record is read correctly in either processing order, and the delete guard does not count that removal as a deletion;
- a machine that holds the non-canonical id as an entry treats the **disappearance of that record under an open alias as convergence**:
  in one transaction it replaces its copy with the canonical record, carrying over its tier, tags and pin; where the text differs, the
  canonical text is applied and the local text is filed as a `conflict` item, or, when the local entry is pinned, the canonical text is
  held as a `pin-edit` and the local pin text stays; a canonical record that is pinned goes to pin approval. An open alias is never
  read as a user deletion.

Success: with a database, a local bare repository and the real applier, a pulled record whose origin hash matches a different local
import creates no duplicate and one alias row; a machine that holds the other id ends, after that record is removed, with the canonical
entry, its tier, tags and pin carried and a differing local text kept in a conflict item (or in a held pin edit under a local pin); the
existing checks pass with nothing calling the cycle.

## Non-goals

- The alias **pass** (the background convergence of the machine's own non-canonical entry, the `converged` step, the deletion of this
  machine's own non-canonical record), the **withhold rule** (the holder of a non-canonical entry not pushing it) and any background
  task or `backgroundTasks.syncDedupe` progress: milestone 5. Until then a non-canonical entry is pushed like any other.
- The `abandoned` state and everything that closes an alias group (the canonical record disappearing while the group is open): milestone 5.
- Any change to `SyncMerge.decide`'s rules (W1 to W14 and the field rules), to the record format or to the schema. `git_sync_alias`
  (migration 18) is used as it is, with no new index.
- Any caller of the cycle, `sync now`, the status block and un-hiding any verb (Part 6), a new `ApplySummary` field, the `sync_review`
  MCP tool and `session_start` text (milestone 2).
- Text typed separately on two machines (no shared origin): umbrella follow-up.

## Design

### D1. Detecting an alias

An entry **candidate** for detection is an id that the pull visits with a record accepted at R, **no `git_sync_entry` row and no local
memory entry** under that id (the case the decision table calls W1, "insert it"). When the decision is `InsertFromRecord` for such an
id and the record has `origin.hash` `h`, look up the local ids for `h`:

- `import_log` rows with `content_hash = h` and `target_type = 'memory'`, from **any** `source_tool` (a host tool's import, or a
  `git-sync` row written by an earlier pull of a record carrying the same origin), taking `target_entry_id`;
- keep the ids that **exist** as a memory in the hot, warm or cold table and **differ from the record's id**. An id whose entry was
  deleted is no alias target, and a log row for the record's own id is the ordinary case.

With one or more such ids the **group** is the record's id plus those ids, the canonical id is the ordinal minimum, and one
`git_sync_alias` row `(canonical, x, h, 'open')` is written for every other member `x`, with `INSERT OR IGNORE`: an existing row of any
state is left as it is, so a re-detection never reopens a `resolved` group. The insert is skipped, whichever of the group is canonical:
the record's holder of the other id must not get a second copy, and the canonical entry still has to arrive through the pass (milestone 5)
or, for a machine that does not hold it, through convergence (D3).

The skip is a change of the decision after `decide` returns, in the way `SyncDeletion.holdInstead` already is: a new Core function
(`SyncAlias.skipInstead`) turns `InsertFromRecord` of a no-row, no-entry id into `Ignore`, and `planRow` then writes nothing. Nothing in
`decide`, `MergeInput` or the record format changes. It is a pure function of the `import_log` rows and the entries, **not** of the alias
rows or their state, so it is the same on every pull. A restore, an edit that beats a deletion and any other re-insert (W3, W8.1) are not
W1 and are untouched.

The alias row is the only write. It is an `AliasPlan` (like the pin plan): planned before `BEGIN IMMEDIATE`, recomputed inside from the
same inputs, and the entry is deferred (the existing deferral, with its existing reason text) when the two differ, for example because an
importer wrote a matching `import_log` row or deleted the local entry in between. A repeated pull finds the row, plans nothing and
opens no transaction. A skip itself never defers and never makes a pull incomplete.

### D2. Aliases before the deletion checks

Before the delete-guard preview, one pre-pass reads the open `git_sync_alias` rows and the detections of every candidate id (D1), and
computes the set of **converging ids** (D3). The preview counts no converging id as a remote deletion. The loop uses the same map, and
each entry recomputes inside its transaction. Ids are visited in ordinal order, so the canonical id of a group (the lowest) is visited
before the others, but nothing relies on it: the pre-pass makes the result the same in any order, and a record added and a record removed
in one pull are read together.

### D3. Convergence

An id `x` **converges** in a pull when all of these hold:

1. `x` has a local memory entry (one tier) and a `git_sync_entry` row that is active with a base (the record was reconciled once);
2. `x`'s record is **absent** from R (no path at all, not a rejected one);
3. an alias row `(c, x)` is `open` and `x` is the other id;
4. the canonical record `c` is accepted at R and parses;
5. no entry with id `c` exists locally, and `x` has no held-delete item (open or approved: a person's pending decision stands).

In **one `BEGIN IMMEDIATE` transaction**, after embedding the canonical text outside it (when the text differs from the local one):

1. re-read `x` and `c` and compare with the plan (a difference defers the entry);
2. **re-key** the entry from `x` to `c`: the memory row, its tier, its vector and its tags move under the new id; nothing else of the
   entry changes yet;
3. apply the canonical record to `c` **as the ordinary first-sync merge of a record against an existing local entry with no base** (row
   `None`, record `c`, local view = the entry): the unchanged no-base rules decide (D4), including the pin and conflict items they
   request, planned before the transaction and recomputed inside as in Parts 3 and 4;
4. delete `x`'s `git_sync_entry` row and write `c`'s row as the apply does (base := the canonical record, stamps as the decision says);
5. retarget the `import_log` rows of `x` (`target_entry_id` := `c`) and write the `git-sync` row for `c` as an insert does, so the later
   reappearance of `x`'s record is read as an alias again (D1) and never inserts;
6. carry `x`'s review items (D5);
7. mark the alias `resolved`.

The state is `resolved`, not `converged`: the umbrella's `converged` means "the entry is converged and this machine's own record still
has to be deleted", and a machine whose record has already disappeared has none. A crash leaves the transaction rolled back, so the
next pull converges again. **It also happens in an unproven pull** ("a missing file is never proof"): convergence loses no content (the
local text is carried, D4), and if the record was only missing, its return is an alias skip.

An open alias for `x` whose canonical record is **absent or does not parse** (conditions 1 to 3 hold, 4 fails) is **not a deletion**:
the decision is `Ignore`, the entry and its row stay as they are, no hold is opened and nothing is counted by the guard. The group is
left `open`; closing it as `abandoned` is milestone 5. If condition 5 fails because an entry with the canonical id already exists, or
a held deletion exists, the ordinary rules decide for `x` (the entry is a duplicate of one the machine has, or a person has the question).

### D4. What convergence carries

Convergence adds no rule: it feeds the existing no-base rules (N-rules of `SyncMerge`, unchanged). The outcomes, to be proven by tests
that name them:

| Local entry `x` | Result on `c` |
|---|---|
| same text, project, scope and type as the canonical record | adopt `created_at`; nothing filed |
| text differs, entry not pinned (NC4) | the record's content (text, project, scope) is applied; the local text is filed as a `conflict` item on `c` (`local`, with this machine's id) |
| text, project or scope differs, entry pinned (NC2) | the local pin text stays; the canonical content is held as a `pin-edit` item on `c`; the entry stays sticky |
| text equal, only project or scope differs, not pinned (NC3) | the record's project and scope; no item |
| entry pinned, record not sticky (NS2) | the entry stays pinned (the pin is the person's; no review) and the pin is re-stamped so it is pushed |
| record sticky, entry not pinned (NS3) | the entry stays non-sticky; a `new-pin` item on `c` |
| tags | the union of the local and record tags (the no-base tag rule) |
| tier | the warmer of the two tiers (the no-base tier rule); a local winner is re-stamped so it is pushed |
| a pending local text edit on `x` | its text is the local view, so a differing one is filed as a conflict (it is **not** written back as W6.1 would: that would recreate the other record) |

`access_count` is not carried (it is not synced and no store method sets it). Historical `compaction_log` and `retrieval_events` rows
keep the old id (umbrella). The conflict arrival uses the existing arrival rule (Part 4 D3) and the bound of 8.

### D5. The old id's review items

`x`'s items cannot follow it blindly, because a hash covers the applied text:

- `new-pin` and `pin-edit` items on `x` are removed (the record they were about is gone; the sticky flag itself is carried by D4);
- a `conflict` item on `x` holds the only copy of earlier losing texts: each of its losers is **re-filed on `c`**, in order, with
  `ConflictItems.Append` (the helper Part 4 built for this part; it throws `InvalidOperationException` when the entry is absent,
  unreadable or in two tiers, which cannot happen here because the re-key came first), after the item D4 files, so the shared bound of 8 and
  the dedupe by text apply; then `x`'s item is removed;
- there is no held-delete item on `x` (condition 5).

### D6. Interplay with the delete guard and the push

- A converging id is not a deletion in the guard's preview (D2) and does not count toward the active-row total's ratio as removed.
- The skipped record has no row, so the pusher sees nothing for it. A non-canonical entry is still pushed (no withhold, Non-goals).
- The cycle's result and `ApplySummary` are unchanged: a skip is a `NoOp`, a convergence counts as `Updated`.

### D7. Existing tests

No existing test pulls two records with the same origin or pulls onto a matching `import_log` row, so none is expected to change; the
ones that count `NoOp`s, transactions or deletion previews are watched. A test that changes is changed in the task that changes the
behaviour and is named in the commit message.

## Tests (what proves it)

At the applier (`ApplyRig`, with a helper that seeds an entry and its `import_log` row):

- a record with an origin hash that a different existing local id has in `import_log`: no insert, one alias row `(min, other, h, open)`,
  `NoOp`, no `git_sync_entry` row; the same pull again: no new row, no transaction;
- the group's canonical id: record lower than the local id, and record higher; three members (two local ids for one hash);
- a matching log row whose entry was deleted: no alias, the record is inserted; a matching row for the record's own id: no alias;
- a `git-sync` log row (a pulled copy with the same origin) is a target; an `import_log` row of another type is not;
- an existing alias row of each state is left alone and the skip still applies;
- a racing importer (an `import_log` row or a deleted entry between the plan and the transaction) defers the entry, nothing is written;
- an unproven pull decides the skip the same way.

Convergence (applier, one test per row of D4's table, each asserting the entry's id, tier, tags, sticky, content, row base, the items,
the `import_log` retarget, the alias state and that `x` is gone from the store, the vectors and the rows):

- the canonical record present in R while `x`'s record is absent, in a pull that also adds the canonical record, and in a later pull;
- the two orders of the catch-up (canonical added and other removed in one pull) with the same final state;
- the delete guard's preview does not count it (a pull whose only removals converge does not trip a guard limit of 1);
- the canonical record absent as well: `Ignore`, the entry kept, no hold, alias still `open`; the canonical record not parsing the same;
- an existing entry under the canonical id, and a held-delete item on `x`: the ordinary rules decide (the deletion path, the hold stands);
- an unproven pull converges; a rolled-back transaction (a fault injected after the re-key) leaves `x` and its row untouched;
- `x`'s conflict item losers re-filed on `c` (order, dedupe, bound of 8); `x`'s pin items removed;
- the reappearance of `x`'s record after convergence is skipped as an alias (no entry `x`).

Across machines (`TwoMachineRig` extended with a third machine, `CycleRig` on the same bare repository): A and B import the same host
file (random ids); each pushes; each pulls: neither has a duplicate and each has one alias row; a third machine T that pulled B's
record first and holds it as an entry, then pulls A's canonical record and, after B's record is removed from the repository by an
ordinary commit, ends with one entry under the canonical id carrying its tier, tags and pin and a differing local text in a conflict item
(a held pin edit under a local pin).

## Documentation

- `AGENTS.md` (the pull row and "where to look" for git sync), `src/TotalRecall.Infrastructure/AGENTS.md` (an "Alias detection and
  convergence" paragraph: the lookup, the skip, the pre-pass, the converging ids, the transaction, the limits) and
  `src/TotalRecall.Core/AGENTS.md` (the `SyncAlias` row).
- The acceptance-criteria draft `docs/superpowers/specs/2026-11-15-git-sync-acceptance-criteria.md` gets
  `### 1b-iii part 5. Alias detection and skip-insert (#165)` with the ticket's criteria.
- No README, user doc or catalog names anything new: there is no new verb.

## Acceptance criteria mapping (ticket #165)

The ticket's criteria (the issue body) map to D1 to D6: detection and skip (D1), order independence and the guard (D2, D6),
convergence (D3), carry-over and differing text (D4, D5), the open alias that is not a deletion (D3), the unchanged checks (all).

## Departures from the earlier designs

- **A `git-sync` log row is an alias target.** The pull design's wording "a different local legacy import" would leave a machine that
  pulled one record of the group (a third machine) with a duplicate when the other arrives; the umbrella says "the local `import_log`".
  Any `source_tool` counts.
- **Convergence also runs in an unproven pull** and ahead of W6.1 (a pending text edit is filed as a conflict, not written back), for the
  reasons in D3 and D4.
- **A canonical record that is also absent keeps the entry and leaves the alias `open`.** The umbrella's `abandoned` close belongs to
  the pass (milestone 5); here the point is only that nothing reads an open alias as a deletion.
- **Convergence ends in `resolved`, not `converged`** (D3).
- **A difference in project or scope alone follows NC3** (the record's wins, no item), because the no-base rules decide and are not
  changed here; the parent's wording "(text, project or scope)" is met by NC2 (pinned) and NC4 (text differs).
- **A held deletion already on the old id stands** (D3, condition 5): the alias does not override a pending question to the person.
- **The re-key is a new store step** inside the transaction (the umbrella describes "remove the local non-canonical entry ... insert the
  canonical record"): moving the entry keeps its vector and tags and lets the unchanged first-sync merge decide.

## Known limits

- A local twin that the person deleted is not protected from a later insert of the other record (the umbrella's "a locally deleted alias
  target is not resurrected" belongs to the pass, milestone 5).
- Both records stay in the repository and the non-canonical entry is still pushed until the withhold rule and the pass (milestone 5).
- An open group whose canonical record is gone stays open and its entry is kept.
- `access_count` and a summary or source path are not carried.

## Deviation from the parent design

None to the breakdown. Detail the parent leaves open and this part fixes: the lookup source (any `source_tool`), the state after
convergence, the unproven and both-gone behaviour, and the re-key, as listed under "Departures".
