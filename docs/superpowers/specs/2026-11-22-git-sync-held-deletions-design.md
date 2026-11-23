# Git sync, milestone 1b-iii part 2: answering held deletions

Parent: #159, Part 2 (docs/superpowers/specs/2026-11-21-git-sync-review-secrets-design.md)

Date: 2026-11-22. Status: draft for review. Part of #93 (git sync); sub-ticket #162 of the parent #159. The parent spec
(`2026-11-21-git-sync-review-secrets-design.md`) governs: this document is the engineering design of its Part 2 and follows its
"Shared decisions". The deletion milestone's design is `2026-11-19-git-sync-deletions-design.md` (the held-delete item, the
delete guard, the proof rule) and the umbrella is `2026-11-15-git-sync-design.md` ("Deletions: a missing file is never proof").
Where this document is more precise it wins for this part; every departure is listed under "Departures".

## Goal

A deletion the pull held back (a `held-delete` item: it is held because the delete guard tripped, because the pull could not
prove the repository's state, or because the memory is pinned here) can be answered by the person, one item at a time:

- `accept`: carry the deletion out, whichever side it came from;
- `restore`: keep (or bring back) the memory, and publish it again where the repository lost it.

The answers are made by one engine service that the CLI calls now and the `sync_review` tool of a later milestone will call.
Nothing is deleted or published that the rules say needs the person, and an answer given on stale information is refused and
leaves the item held. Success: with two machines, a local bare repository and temporary databases, every cause of both
directions can be accepted and restored, an accepted deletion is not stopped by the guard, answering some items leaves the rest
held, a stale answer is refused, and the existing checks pass with nothing calling the cycle.

## Non-goals

- Any caller of the cycle, `sync now`, the `sync` status block, the MCP tool, un-hiding the verbs, `GitSyncCycleCallerTests`
  changes (parent Shared decision 6: Part 6). The verbs added here are hidden like the others.
- Answering `new-pin`, `pin-edit` or `conflict` items (Parts 3 and 4). This part creates one `new-pin` item (D6) and nothing
  reads it yet; the pull still postpones every kind whose part has not merged (parent Shared decision 7).
- Re-deciding a held item. The guard, the proof rule and the lifecycle of 1b-ii stand (parent Shared decision 8); this part
  adds an answer, one new item state and a hash refresh.
- A new column, table or migration (`state` already admits `approved`, `declined`, `resolved`).

## Design

### D1. The review service

A new `ReviewService` (`src/TotalRecall.Infrastructure/GitSync/ReviewService*.cs`, partial files by concern) with three
operations. Every operation takes the sync lock (`SyncLock`, the 3 s wait of the setup service; busy is a refusal `busy`), opens
the database like the setup service, runs the migrations, then works in **one immediate transaction that first re-reads the item,
the row and the local entry**. Nothing is computed from a read taken before the transaction except the embedding of a restore
(D5), which is re-validated inside it.

- `List()`: the open and approved `held-delete` items, by id, each with its direction, cause, shown hash, `arrivedAt`, and the
  memory's text (D7). Read-only; it takes no lock.
- `Accept(id, shownHash?)`, `Restore(id, shownHash?)`: answer the item of `id`.

Results are `Answered(lines)` or `Refused(name, text)`. Refusal names (added to `Core/SyncSetup.fs`): `no-held-item` (no
open item for the id), `stale-item` (D4), `edited-locally` (D3), `unreadable-entry` (the id is in two tiers or unreadable;
reuse the name if Part 1 added it first) and the existing `busy`, `failed`. A refusal never changes the item's state, except
that a stale answer returns the item as it is now (Shared decision 3: a `held-delete` item stays `open` and never becomes
`unseen`). The environment carries what the service needs and the setup environment lacks: a lazily created embedder
(`Func<IEmbedder>`; restore of a local hold only) and the clock.

There is **no "all" in the service**; the bulk forms are CLI loops over `Accept`/`Restore` (D8). The service never names
`GitSyncPusher` or `GitSyncCycle` (`GitSyncCycleCallerTests` stays unchanged): it leaves work for the next cycle through the
rows and items it writes.

### D2. Accepting a held remote deletion

The record is gone from the repository and the memory is still here. `Accept`:

1. refuses `edited-locally` when the entry's current content hash (`SyncScan.contentHash` of its text) differs from the row's
   `scanned_content_hash`, or the row has a pending content stamp (the memory was edited here after the last scan, so the person
   never saw this text when the hold was opened; the item stays open);
2. otherwise, in the same transaction, deletes the entry and its vector (the apply's own `tx.Delete`), writes the row exactly as
   the apply writes it for a proven remote deletion (state `forgotten`, base kept, pending stamps and `repush_groups`
   cleared: the row `SyncApply.planRow` produces for `DeleteEntry`), and removes the item.

No guard is involved: the apply never counts a forgotten row (a row `forgotten` with no record and no entry is W10/W12, which
decides nothing). This holds for every cause (`guard`, `unproven`, `sticky`), pinned entries included: the person answers for
the pin.

### D3. Restoring a held remote deletion

`Restore` keeps the memory and publishes it again. In one transaction it:

1. leaves the entry (and its sticky flag) exactly as it is;
2. rewrites the row as a **new** one: `base_json` null, a fresh stamp for each of the three groups issued from the clock
   (`IssueStamp`; the scanner's `FreshRow` is the precedent), `pushing_stamps` and `repush_groups` cleared, the scanned and
   `local_*` comparison columns as they are;
3. removes the item.

The next push sees a row with no base and pending stamps, so it writes a new record (the existing `isNew` path; the item no
longer excludes it). A restore of a pinned entry (cause `sticky`) publishes a sticky record, which other machines see as a
new pin and answer through pin review (Part 3). **Documented limit** (umbrella, "same-text restore"): a machine that already
forgot the memory applies W3 and does not resurrect it unless the record's text is newer than the base it kept, so a restore
reaches machines that never forgot it and does not force the others.

### D4. Accepting a held local deletion: the `approved` item state

The memory was deleted here and its record is still in the repository. Accepting must make the next push remove the record
and must not be stopped by the guard (the decision of the owner: **an approved item state**).

`Accept` sets the item's `state` to `approved` (direction `local`, hash and cause unchanged) and writes nothing else; the row and
the entry are untouched. The shown hash is compared first (D9). The item stays until the push has removed the record.

How the pull treats an approved item (the applier's `BuildInput` gains the approved item as a second input beside the open
one; `SyncMerge.decide` itself is unchanged: the applier calls it as it does for W8.3, below):

| the id's situation in this pull | what happens |
|---|---|
| row `active` with a base, entry absent, record present, text not newer than the base (W8.1 does not apply), and the record's blob id **equals** the item's hash | the decision is `PushDeletion` with W8.3's base update, **even in an unproven pull**, and it is **not counted** by the guard's preview and never converted by it; the item stays `approved` |
| the same, but the blob id **differs** | the item is re-opened (`open`, hash refreshed to the current blob id, cause kept) and the decision is W8.2 (`Ignore`): a record that changed after the person answered is shown again, never deleted blind |
| the record's text is newer than the base (W8.1: an edit beats the deletion) | the entry is inserted as the apply does today and the approved item is removed |
| the record is absent | the row is forgotten as today (W10) and the item is removed |
| the entry is present again (reactivated) | the item is removed (the scanner already deletes a held item, whatever its state, when it reactivates a row) |

`GitSyncReviews.ReadOpenHeld` keeps reading only `state = 'open'`, so the push exclusion (`Prepare`) and the existing readers are
unchanged and an approved item never blocks anything. A new reader returns the approved items. The preview and the loop share
one helper so they decide an approved id the same way (the 1b-ii rule that the preview and the loop use one input builder).
The push removes the record through the existing `PushDeletions` list and `Finalize.Forget`; `Forget` also removes the id's
`held-delete` item (it is the last step that knows the removal happened). The existing `removable` test (still a local
deletion, an accepted record at `L`, no occupied path, not rejected) stands.

### D5. Restoring a held local deletion

The memory was deleted here and the record is still in the repository. `Restore` re-inserts the memory from the **row's
`base_json`** (the record as the last pull left it) as an ordinary entry:

- the entry is **non-sticky** whatever the base says, in the tier the apply's insert would choose (the record's tier, or warm
  when `SyncApply.demotesToWarm` applies), with the record's text, tags, project, scope, type, source and `created_at`, the
  same id, an embedding computed **before** the transaction from the base's text, and the `import_log` row the apply writes
  (`git-sync`, `sync:<id>`);
- the row is written exactly as the apply writes it for `InsertFromRecord`: pending stamps cleared, the scanned and `local_*`
  comparison columns taken from the stored entry, so `local_sticky` is false (Shared decision 4). **No unpin is pushed**: the
  writer patches only groups the person changed, and nothing changed;
- when the base's record is sticky, a `new-pin` item is written (state `open`, `record_hash` null, `payload_json`
  `{"origin":"restore"}`): nothing reads it in this part, and Part 3 answers it. The next pull postpones that id as today
  (`review`) until Part 3 lifts the postponement. A null hash means "not seen by a pull yet": Part 3's answer treats it as
  stale until a pull refreshes it (carried to Part 3's spec);
- the item is removed.

Inside the transaction the service re-checks the item, the row, that the entry is still absent, that the id is not a knowledge
entry, and that the base parses; any change is `stale-item` (or `failed` with the reason for an unreadable base). An id that is
in two tiers or unreadable is `unreadable-entry`. An approved item (D4) is also restorable: the restore removes it.

### D6. The stale rules

- A **local hold** carries a shown hash (the blob id the person saw). The service re-reads the item under the lock and refuses
  `stale-item` when the hash differs from the item's current one; the item is returned as it is now. Every pull refreshes the
  hash of an item it leaves open (D10), so the item's own hash is the record's blob id as of the last pull. Owner decision: the
  item's own refreshed hash is the reference, **not** the clone's tree (the parent spec's wording read the clone's HEAD, which after
  a rejected push is an unpublished local commit and not the repository's state).
- A **remote hold** has no shown hash. The only staleness is a local edit: D2's `edited-locally`.
- `Restore` of either direction carries the same rules (a restore of a remote hold needs no hash and no edit check, because it
  keeps the entry as it is).

### D7. What is shown, and the CLI

`sync review list` prints, per item (ordinal id order): the id, the direction (`remote`: removed in the repository, kept here;
`local`: deleted here, kept in the repository), the cause, the shown hash (`-` for a remote hold) and the memory's text, which
is the local entry's text for a remote hold and the row's `base_json` text for a local hold. The text is data (parent Shared
decision 5): printed between fixed delimiter lines that name it as repository content, capped at `tiers.pinned.max_content_chars`
characters with a marker when cut, with control characters other than newline and tab removed. Nothing in it is treated as an
instruction. A text that cannot be read (an unreadable entry or base) is shown as `(unreadable)` and the item stays answerable.

The commands are hidden, under `sync review` (`src/TotalRecall.Cli/Commands/Sync/ReviewCommand.cs`, registered in `CliApp`):

- `sync review list`;
- `sync review accept <id> [--shown <hash>]` and `sync review restore <id> [--shown <hash>]`; a hash of at least 8 hex digits
  that prefixes exactly the item's hash counts as the hash. Without `--shown` the CLI passes the hash it reads when it runs (a
  person typing an id has just read the list; scripts and the later MCP tool must pass the hash);
- `sync review accept-deletions --all` and `sync review restore-deletions --all` (D8).

Output goes to standard output; a refusal prints `refused: <name>: <text>` on standard error, exit 1; a usage error exits 2
(the setup commands' rules in `SyncCommandBase`).

### D8. The bulk forms

`accept-deletions --all` and `restore-deletions --all` loop over the open items (and, for restore, the approved ones), in id
order, calling the single-item operation with each item's current hash, and print one line per item (answered or the
refusal) and a count. They are **CLI-only** and **refuse unless standard input and standard output are both an interactive
terminal**: exit 2 with `needs-a-terminal`, nothing answered, so an agent's shell cannot run them unattended. The terminal test
is an injected seam (`Func<bool>`, the shape `TranscriptsHarness` uses), because a test cannot fake `Console.IsOutputRedirected`.
`--all` is required (a bare `accept-deletions` is a usage error). A refusal of one item does not stop the others.

### D9. Answering some items leaves the rest held

Each answer touches only its own item, row and entry. The rest stay `open`; W6.3 and W8.2 keep protecting them, so the
remaining items are never counted by the guard (a held item never produces a `DeleteEntry` or `PushDeletion`). Accepting a
remote hold lowers the active-row denominator only by the rows really forgotten; no behaviour depends on it beyond the limit's
usual arithmetic.

### D10. The two lifecycle fixes this part needs

1. **A refreshed hash.** An item the pull leaves open (W6.3, W8.2, or the decision keeps it) is **re-written when its record
   changed**: for a local hold, when the record's blob id differs from the item's hash the item's `record_hash` is updated to the
   current blob id (state, cause, direction and `arrived_at` kept). This is what Shared decision 3 already says, and what the stale
   check of D6 reads. A remote hold has no hash and is unchanged. The applier counts the refresh as touching the item (so the
   decision is not skipped as a no-op) and reports nothing new in the summary.
2. **The parked 1b-ii finding** (final review of PR #157, `GitSyncApplier.Issues.cs`, the unproven fallback hold in
   `HoldUnprovenCandidate`). Today it writes nothing when the id has any open item. W6.3 and W8.2 protect an item only against
   its own direction, so an open item of the **other** direction is stale (an open remote item whose memory was then deleted
   here, an open local item whose record then vanished) and the main path already closes and replaces it
   (`AnOpenRemoteItem_ThenTheEntryDeletedHere_…`). The fallback now **replaces an open item of the other direction and keeps
   skipping one of the same direction**; an approved item (D4) counts as an item of the local direction. The contributor
   guide line that says "an open item is never replaced" (`src/TotalRecall.Infrastructure/AGENTS.md`, the unproven fallback
   hold sentence) is corrected to say so.

## Tests (what proves it)

Core (`Sync` tests): `SyncDeletion`/`SyncApply` additions are pure and small (the approved-item helper if one is pure).
Infrastructure (`GitSync`): `ReviewServiceTests` over the existing rigs (`ApplyRig.SeedHeld`, `PullRig`, `PushRig`,
`CycleRig`, `TwoMachineRig`); one test per row below.

- accept, remote hold: each cause (`guard`, `unproven`, `sticky`): the entry and its vector are gone, the row is `forgotten`
  with the base kept and no pending stamps, the item is gone, the next pull decides nothing for the id; a pinned entry is
  deleted when the person accepts;
- accept, remote hold, refused: a text edited here after the scan (`edited-locally`, item still open, entry untouched); a
  pending content stamp; an id in two tiers (`unreadable-entry`);
- accept, local hold: the item becomes `approved` and nothing else changes; a following **proven** cycle removes the record from
  the repository and the item is gone and the row `forgotten`; an **unproven** pull does not re-hold it; with more approved
  items than the guard's limit **all** are removed (the guard does not count them) while an unapproved one beside them is
  still counted and held; a record that changed (a tag edit on another machine) between the answer and the pull re-opens the
  item with the new hash and deletes nothing; a record with newer text beats the approved deletion (the entry is inserted, the item
  gone); a record that vanished leaves the row forgotten and the item gone;
- restore, remote hold: every cause; the entry is untouched, the row has no base and three pending stamps, the next push writes
  a new record (check the clone's tree) and the item is gone; a pinned entry's restore publishes a sticky record; a second
  machine that never forgot it receives it, one that did applies W3 (the documented limit, shown by a test);
- restore, local hold: the entry exists non-sticky in the right tier, with the base's text, tags and id and an embedding; the
  row has `local_sticky` false and no pending stamps, so **a following push writes nothing for it and pushes no unpin** (the
  entry was pinned here before it was deleted: seed it so); a sticky base opens a `new-pin` item with a null hash and the pull
  postpones it as before; an approved item is restored too; an unparsable base is `failed` with nothing changed;
- stale: a local hold whose record changed in a later pull (hash refreshed by D10) refuses a shown hash from before; the item is
  `open` after the refusal; a remote hold never becomes `unseen`;
- some answered, the rest held: three items, answer one, the other two are `open` and still protected on the next pull;
- D10: a pull that leaves a local item open refreshes its hash when the record's blob changed and leaves `arrived_at`; the fallback
  replaces the other direction's item, keeps the same direction's item and keeps an approved item; the existing
  `AnOpenItemOfAScanSkippedCandidate_IsNotReplacedByTheFallback` stays green;
- the service holds the sync lock (a held lock gives `busy` with nothing changed), and a cycle's lock is respected;
- CLI: `list` shows the delimited, capped text and no control characters; `accept`, `restore`, `--shown` with a prefix, a short
  prefix and a wrong hash; the bulk forms refuse with exit 2 and change nothing when the terminal seam says no, and answer
  every item when it says yes; `sync review` is not listed in help (`HiddenCommandTests` gains the verb);
- `GitSyncCycleCallerTests` is unchanged and passes; no file outside the pusher and the cycle names them.

## Documentation

`2026-11-15-git-sync-acceptance-criteria.md` gains this part's draft criteria under its own heading; the contributor guides
(`AGENTS.md` rows for the review service and the verbs, `src/TotalRecall.Infrastructure/AGENTS.md` paragraphs for the service,
the approved state and the corrected fallback line, `src/TotalRecall.Cli/AGENTS.md` if it lists the sync verbs) are updated.
No user document or README names the verbs (they stay hidden; `docs/git-sync.md` is milestone 4).

## Acceptance criteria mapping (ticket #162)

The ticket's criteria are written in the ticket; the design above answers them as: accept of a remote hold (D2); accept of a
local hold and the guard (D4); restore of a remote hold (D3); restore of a local hold, non-sticky, no unpin, `new-pin` item for a
sticky record (D5); the rest stay held (D9); stale answers (D6); the list and the text rules (D7); the bulk forms (D8); the parked
fallback fix (D10); hidden verbs and unchanged callers (Tests, Non-goals).

## Departures from the earlier designs

- **An `approved` state on the `held-delete` item** (parent Shared decision 1 describes `state = 'open'` and 1b-ii's design
  rejected an approved marker on the entry row, its approach C). The marker lives on the review item, which the schema
  already allows; open-item readers are unchanged. Owner decision.
- **The stale check of a local hold reads the item's own, refreshed hash**, not the clone's current tree (parent Part 2's
  scope text). The clone's HEAD after a rejected push is an unpublished local commit. Owner decision. This needs D10.1.
- **`restore` of a local hold writes a `new-pin` item with a null hash** and the pull still postpones the id until Part 3 (the
  parent's departures already say answering it is Part 3). Part 3's spec must treat a null hash as stale until a pull refreshes it.
- **A local hold has no `sticky` cause** (`SyncDeletion.holdCause` gives a local hold `guard` or `unproven` only); the parent's
  "every cause" applies to each direction's causes.
- **A same-text restore of a remote hold does not force a machine that forgot the memory** (umbrella and W3): documented, tested.
- **The bulk forms test standard input and output both**; the existing terminal checks in the CLI test input only.

## Deviation from the parent design

The three items above that change the parent's wording (the approved state, the stale reference, the `new-pin` hash) are the
deviations; none changes the parent's Shared decisions 1 to 8, which this part follows.
