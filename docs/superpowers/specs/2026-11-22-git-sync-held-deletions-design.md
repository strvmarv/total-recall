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
- Answering `new-pin`, `pin-edit` or `conflict` items (Parts 3 and 4). This part creates one `new-pin` item (D5) and nothing
  reads it yet; the pull still postpones every kind whose part has not merged (parent Shared decision 7).
- Re-deciding a held item. The guard, the proof rule and the lifecycle of 1b-ii stand (parent Shared decision 8); this part
  adds an answer, one new item state and a hash refresh.
- A new column, table or migration (`state` already admits `approved`, `declined`, `resolved`).

## Design

### D1. The review service

A new `ReviewService` (`src/TotalRecall.Infrastructure/GitSync/ReviewService*.cs`, partial files by concern) with three
operations. `Accept` and `Restore` take the sync lock (`SyncLock`, the 3 s wait of the setup service; a busy lock is the refusal `busy`), opens
the database like the setup service, runs the migrations, then works in **one immediate transaction that first re-reads the item,
the row and the local entry**. Nothing is computed from a read taken before the transaction except the embedding of a restore
(D5), which is re-validated inside it.

- `List()` (opens the database read-only like `SyncSetupService.StatusAsync`, with no migration and no lock): the open and approved `held-delete` items, by id, each with its state, direction, cause, shown hash, `arrivedAt`
  and the memory's text (D7).
- `Accept(id, shownHash?)`, `Restore(id, shownHash?)`: answer the item of `id`. **A local hold (and an approved item, which
  is one) is answered only with the shown hash**: without it the service refuses `shown-hash-required`, so the later MCP tool
  and any script cannot approve "whatever the record is now" (parent Shared decision 3 at the trust boundary). A remote hold
  has no hash and takes none.

Results are `Answered(lines)` or `Refused(name, text)`. Refusal names (added to `Core/SyncSetup.fs`): `no-held-item` (no
open or approved item for the id), `stale-item` (D6), `edited-locally` (D2), `shown-hash-required`, `unreadable-entry` (the id
is in two tiers or unreadable; reuse the name if Part 1 added it first) and the existing `busy`, `failed` (also used when this
machine has no sync identity to issue a stamp). Preconditions are checked **inside the transaction**, in this order, each a
refusal that changes nothing: the item exists in a state the operation accepts (`no-held-item`); the row is `active` and, for
the operations that need it, has a base (`failed`: "the row no longer matches the item"); the entry is read (`unreadable-entry`);
the entry's presence matches the direction (a remote hold needs the entry present, a local hold needs it absent: otherwise
`stale-item`: the next pull closes the item); for a local hold the shown hash (D6); for a remote hold `Accept`'s edit test (D2).
A hash passed for a remote hold is ignored; `Accept` of an item that is already `approved`, with the matching hash, answers
`already accepted` and changes nothing. A refusal never changes the item's state, except
that a stale answer returns the item as it is now (Shared decision 3: a `held-delete` item stays `open` and never becomes
`unseen`). The environment carries what the service needs and the setup environment lacks: a lazily created embedder
(`Func<IEmbedder>`; restore of a local hold only).

There is **no "all" in the service**; the bulk forms are CLI loops over `Accept`/`Restore` (D8). The service never names
`GitSyncPusher` or `GitSyncCycle` (`GitSyncCycleCallerTests` stays unchanged): it leaves work for the next cycle through the
rows and items it writes. The row and insert planning the apply uses (`BuildRow`, `PlanStore`, the tier choice) is **shared**:
the pieces the service needs become internal static helpers that the applier and the service both call, not a second copy.
The service reads `HotMaxContentChars`/the hot tier limit and `tiers.pinned.max_content_chars` from the loaded config the way
the cycle's settings do. The shared helpers include `IssueStamp` (taking the clock as a parameter). The environment already carries the clock
(`SyncSetupEnvironment.Clock`); the embedder is the only addition.

### D2. Accepting a held remote deletion

The record is gone from the repository and the memory is still here. `Accept`:

1. refuses `edited-locally` when the memory was edited after the hold was shown, that is when **either** the entry's current
   content hash (`SyncScan.contentHash` of its content fields: text, project, scope, type, source and `created_at`) differs from the row's `scanned_content_hash` (every hold write takes the
   comparison columns from the stored entry, so a difference is an edit made since), **or** W6.1's own condition holds: a pending
   content stamp **and** a text that differs from the base's (an edit the scan has not stamped as a change yet is covered by the
   first test, an edit it has stamped by the second). A pending content stamp alone does not refuse: a project or scope edit
   leaves one with the text unchanged, and refusing on it would make a held deletion unanswerable for ever (the push excludes a
   held id, so the stamp never clears). A text that differs from the base's with no pending stamp and an unchanged hash does not
   refuse either: it is the state a pin-edit merge leaves in a later part, where the base takes the record's text. The item stays
   open on a refusal;
2. otherwise, in the same transaction, deletes the entry and its vector (the apply's own `tx.Delete`), writes the row exactly as
   the apply writes it for a proven remote deletion (state `forgotten`, base kept, pending stamps and `repush_groups`
   cleared: the row `SyncApply.planRow` produces for `DeleteEntry`), and removes the item.

No guard is involved: the apply never counts a forgotten row (a row `forgotten` with no record and no entry is W10/W12, which
decides nothing). This holds for every cause (`guard`, `unproven`, `sticky`), pinned entries included: the person answers for
the pin. A tag, tier, project or scope edit made after the hold goes with the entry and is reported in the answer's lines as dropped
(the apply's `pending-changes-dropped` note, whose test is W6.4's: it does not look at the pin). An edit the scan has not stamped
yet to project or scope changes the content hash, so it refuses until the next scan stamps it (the answer to that is to wait a cycle).
Accept refuses `failed` when the base does not parse (the edit test needs the base's text).

### D3. Restoring a held remote deletion

`Restore` keeps the memory and publishes it again. In one transaction it:

1. leaves the entry (and its sticky flag) exactly as it is;
2. rewrites the row as a **new** one: `base_json` null, a fresh stamp for each of the three groups issued from the clock
   **above that group's stamp in the old base** (`IssueStamp` with the base's stamp as the floor, so a machine with a skewed
   clock that kept the old base does not classify the new record as older and re-push its base value over it; the scanner's
   `FreshRow` is the precedent for the shape), `pushing_stamps` and `repush_groups` cleared, the scanned and `local_*`
   comparison columns as they are;
3. removes the item.

The floors need the old base: when it does not parse the restore refuses `failed` with the reason and changes nothing (as D5
does). The next push sees a row with no base and pending stamps, so it writes a new record (the existing `isNew` path; the item no
longer excludes it). A restore of a pinned entry (cause `sticky`) publishes a sticky record, which other machines see as a
new pin and answer through pin review (Part 3). **Documented limit** (umbrella, "same-text restore"): a machine that already
forgot the memory applies W3 and does not resurrect it unless the record's text is newer than the base it kept, so a restore
reaches machines that never forgot it and does not force the others.

### D4. Accepting a held local deletion: the `approved` item state

The memory was deleted here and its record is still in the repository. Accepting must make the next push remove the record
and must not be stopped by the guard (the decision of the owner: **an approved item state**).

`Accept` sets the item's `state` to `approved` (direction `local`, hash and cause unchanged) and writes nothing else; the row and
the entry are untouched. The shown hash is required and compared after the presence checks of D1 (D6). The item stays until the push has removed the record.

How the pull treats an approved item. `SyncMerge.decide` is unchanged. The applier's input builder gains the approved item
beside the open one and, **only for the shape of W8** (row `active` with a base, entry absent, record present), calls `decide`
with `proven = true` and no held item, which is exactly W8.3 (and W8.1 first: newer text beats the deletion). For every other
shape (entry present, record absent) it decides with the pull's real proof and its open item, so an approved item can never
turn a W6 case into an uncounted deletion. The approved item is **removed** whenever the entry is present again, the record is
absent, or the decision is `InsertFromRecord`, the rule `finish` applies to an open local item. The in-transaction re-read of
`CarryOut` compares the item of any state (`ReadHeldItem`), so an approved item does not defer every pull. **Every id with a `held-delete`
item, open or approved, is added to the pull's visit set** (the applier concatenates them as it does the scan-skipped ids; no
change to `SyncApply.visitSet`), because an id whose entry was re-created under its still-`active` row with the same content, and
whose record did not change, is otherwise never visited: an open local item would stay unanswerable (`stale-item` for ever, and the
push keeps excluding the id) and an approved one would survive to pre-approve a later, unrelated deletion. A visited id whose
entry is present again closes its item through `finish` (open local) or the rule above (approved). Removing or re-opening an approved
item counts as touching the item, so the decision-13 no-op shortcut does not skip its transaction.

| the id's situation in this pull | what happens |
|---|---|
| row `active` with a base, entry absent, record present, text not newer than the base (W8.1 does not apply), and the record's blob id **equals** the item's hash | the decision is `PushDeletion` with W8.3's base update, **even in an unproven pull**, and it is **not counted** by the guard's preview and never converted by it; the item stays `approved` |
| the same, but the blob id **differs** | the item is re-opened (`open`, hash refreshed to the current blob id, cause kept) and the decision is W8.2 (`Ignore`): a record that changed after the person answered is shown again, never deleted blind |
| the record's text is newer than the base (W8.1: an edit beats the deletion) | the entry is inserted as the apply does today and the approved item is removed |
| the record is absent | the row is forgotten as today (W10) and the item is removed |
| the id is skipped by the scan (an entry in two tiers, unreadable, or a group the clock could not stamp; such an id is not decided) | the approved item stays; nothing is deleted while the id is skipped, and when the skip clears the entry is present, so the next pull that decides the id removes the item |
| the entry is present again while the row stays `active` (a memory with the id was re-created, for example by an import that keeps ids; the scanner reactivates only `forgotten` or `left` rows, so it does not remove the item) | the pull removes the item (the rule above) and decides with the real proof |

`GitSyncReviews.ReadOpenHeld` keeps reading only `state = 'open'`, so the push exclusion (`Prepare`) and the existing readers are
unchanged and an approved item never blocks anything. A new reader returns the approved items. The preview and the loop share
one helper so they decide an approved id the same way (the 1b-ii rule that the preview and the loop use one input builder).
The push removes the record through the existing `PushDeletions` list and `Finalize.Forget`; `Forget` also removes the id's
`held-delete` item (it is the last step that knows the removal happened), **before** its early return for a row that is no
longer `active`. The existing `removable` test (still a local
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
- when the base's record is sticky, a `new-pin` item is written (state `open`, `record_hash` = the held item's own hash, the
  blob id of the record the person saw, `payload_json` `{"origin":"restore"}`). The item is written as an upsert on `(entry_id, kind)` (a second restore of the same id replaces it). Nothing reads it in
  this part, and while the record's pin is unchanged no pull produces a `NewPin` request for this id (the restored entry's
  sticky group is unchanged against the base, so the pull does not postpone it; a later record with a newer sticky stamp does,
  as today, until Part 3): the entry stays an ordinary hot entry and the item waits for Part 3, which answers it (approve pins it, decline
  closes it) and keeps its hash current when the record changes and removes it when its entry is deleted or its row forgotten (a later proven remote
  deletion of the non-sticky restored entry takes W6.4 and deletes it without a hold; carried to Part 3's spec);
- the item is removed.

Inside the transaction the service re-checks the item, the row, that the entry is still absent, that the id is not a knowledge
entry, and that the base parses; any change is `stale-item` (or `failed` with the reason for an unreadable base). An id that is
in two tiers or unreadable is `unreadable-entry`. An approved item (D4) is also restorable: the restore removes it.

### D6. The stale rules

- A **local hold** (an approved item included) is answered only with a shown hash (the blob id the person saw). The service
  re-reads the item under the lock and refuses `stale-item` when the hash differs from the item's current one; the item is
  returned as it is now. Every pull refreshes the
  hash of an item it leaves open (D10), so the item's own hash is the record's blob id as of the last pull. Owner decision: the
  item's own refreshed hash is the reference, **not** the clone's tree (the parent spec's wording read the clone's HEAD, which after
  a rejected push is an unpublished local commit and not the repository's state).
- A **remote hold** has no shown hash. The only staleness is a local edit: D2's `edited-locally`.
- `Restore` of either direction carries the same rules (a restore of a remote hold needs no hash and no edit check, because it
  keeps the entry as it is).

### D7. What is shown, and the CLI

`sync review list` prints, per item (ordinal id order): the id, the state (`open` or `approved`), the direction (`remote`:
removed in the repository, kept here; `local`: deleted here, kept in the repository), the cause, the shown hash (`-` for a remote
hold) and the memory's text, which is the local entry's text for a remote hold and the row's `base_json` text for a local hold.
The text is data (parent Shared decision 5): printed between fixed delimiter lines that name it as memory text and give its
source (`local entry` or `repository record`), never as an instruction, capped at `tiers.pinned.max_content_chars`
characters with a marker when cut, with control characters other than newline and tab removed. Nothing in it is treated as an
instruction. A text that cannot be read (an unreadable entry or base) is shown as `(unreadable)` and the item stays answerable.

The commands are hidden, under `sync review` (`src/TotalRecall.Cli/Commands/Sync/ReviewCommand.cs`, registered in `CliApp`):

- `sync review list`;
- `sync review accept <id> [--shown <hash>]` and `sync review restore <id> [--shown <hash>]`; a hash of at least 8 hex digits
  that prefixes exactly the item's hash counts as the hash. Without `--shown` the **CLI** (not the service) passes the hash it
  reads when it runs, as a convenience for a person who has just read the list;
- `sync review accept-deletions --all` and `sync review restore-deletions --all` (D8).

Output goes to standard output; a refusal prints `refused: <name>: <text>` on standard error, exit 1; a usage error exits 2
(the setup commands' rules in `SyncCommandBase`).

### D8. The bulk forms

`accept-deletions --all` and `restore-deletions --all` loop over the open items (and, for restore, the approved ones), in id
order, calling the single-item operation with each item's current hash, and print one line per item (answered or the
refusal) and a count. They are **CLI-only** and **refuse unless standard input and standard output are both an interactive
terminal**: a usage-style error on standard error (`sync review: needs-a-terminal: ...`, the `UsageError` form), exit 2, nothing
answered, so an agent's shell cannot run them unattended. The terminal test
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
   skipping one of the same direction**; the fallback reads the item of any state, and an approved item (D4) counts as an item of
the local direction (without this it would overwrite one with an open item). The contributor
   guide line that says "an open item is never replaced" (`src/TotalRecall.Infrastructure/AGENTS.md`, the unproven fallback
   hold sentence) is corrected to say so.

## Tests (what proves it)

Core (`Sync` tests): `SyncDeletion`/`SyncApply` additions are pure and small (the approved-item helper if one is pure).
Infrastructure (`GitSync`): `ReviewServiceTests` over the existing rigs (`ApplyRig.SeedHeld`, `PullRig`, `PushRig`,
`CycleRig`, `TwoMachineRig`); one test per row below.

- accept, remote hold: each cause (`guard`, `unproven`, `sticky`): the entry and its vector are gone, the row is `forgotten`
  with the base kept and no pending stamps, the item is gone, the next pull decides nothing for the id; a pinned entry is
  deleted when the person accepts;
- accept, remote hold, refused: a text edited here after the hold (`edited-locally`, item still open, entry untouched); an id in
  two tiers (`unreadable-entry`); the entry already gone or the row no longer matching (`stale-item`/`failed`, nothing changed).
  Accepted, not refused: a project or scope edit that left a pending content stamp with the text unchanged (the case that would
  otherwise never clear); a base whose text differs from the entry's with no pending stamp and an unchanged hash (the state a
  later pin-edit merge leaves: seed it by hand); a tag edit, which the answer reports as dropped; refused: a text edited after
  the hold, both before the scan stamped it (hash differs) and after (pending stamp and text differs);
- accept, local hold: the item becomes `approved` and nothing else changes; a following **proven** cycle removes the record from
  the repository and the item is gone and the row `forgotten`; an **unproven** pull does not re-hold it; with more approved
  items than the guard's limit **all** are removed (the guard does not count them) while an unapproved one beside them is
  still counted and held; a record that changed (a tag edit on another machine) between the answer and the pull re-opens the
  item with the new hash and deletes nothing; a record with newer text beats the approved deletion (the entry is inserted, the item
  gone); a record that vanished leaves the row forgotten and the item gone; the entry re-created under the `active` row with the same content and an unchanged record (the id is not
  otherwise visited) removes the item and an unrelated later deletion of that id is held as usual (decided with the real proof, W6 never becomes an
  uncounted deletion); an open local item whose entry was re-created under the active row with an unchanged record is closed by one proven pull and both answers then refuse `no-held-item`; an approved item does not defer the apply (the in-transaction re-read covers it); an approved item can
  still be restored; an answer without a shown hash is refused `shown-hash-required`;
- restore, remote hold: every cause; the entry is untouched, the row has no base and three pending stamps, the next push writes
  a new record (check the clone's tree) whose stamps are above the old base's even with a clock set behind it, and the item is
  gone; a pinned entry's restore publishes a sticky record; a second
  machine that never forgot it receives it, one that did applies W3 (the documented limit, shown by a test);
- restore, local hold: the entry exists non-sticky in the right tier, with the base's text, tags and id and an embedding; the
  row has `local_sticky` false and no pending stamps, so **a following push writes nothing for it and pushes no unpin** (the
  entry was pinned here before it was deleted: seed it so); a sticky base opens a `new-pin` item carrying the held item's hash,
  the entry stays an ordinary hot entry and a following pull neither postpones the id nor changes the item; an approved item is
  restored too; an unparsable base is `failed` with nothing changed;
- stale: a local hold whose record changed in a later pull (hash refreshed by D10) refuses a shown hash from before; the item is
  `open` after the refusal; a remote hold never becomes `unseen`; the list shows the state (`open` or `approved`) and the source
  of the text;
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
- **`restore` of a local hold writes a `new-pin` item carrying the held item's hash** and the entry stays an ordinary hot entry;
  nothing postpones the id while the record's pin is unchanged (a later newer sticky stamp is postponed as today, until Part 3),
  and answering the item is Part 3, which must keep its hash
  current when the record changes (the parent's departures already say answering it is Part 3).
- **`accept` of a remote hold is refused on an edit made since the hold** (the content hash differs from the row's scanned
  hash, or a pending content stamp comes with a text that differs from the base's: W6.1's condition), not whenever a content stamp is
  pending (the parent's wording). A pending stamp can outlive the hold for ever (a project or scope edit), which would make
  the item unanswerable.
- **The service refuses a local-hold answer with no shown hash** (`shown-hash-required`); the parent's Shared decision 3 says an
  answer that carries a hash is checked, and this makes carrying one mandatory at the service boundary. The CLI may read the
  current hash for a person who has just read the list.
- **A restored entry keeps its sticky flag when the hold was a remote one.** The parent's "restore never yields a sticky entry"
  is read as its Shared decision 4 states it: every path that **inserts or re-inserts** an entry from a record yields a
  non-sticky entry (the restore of a local hold); a restore of a remote hold inserts nothing and leaves the entry as it was.
- **A local hold has no `sticky` cause** (`SyncDeletion.holdCause` gives a local hold `guard` or `unproven` only); the parent's
  "every cause" applies to each direction's causes.
- **A same-text restore of a remote hold does not force a machine that forgot the memory** (umbrella and W3): documented, tested.
- **The bulk forms test standard input and output both**; the existing terminal checks in the CLI test input only.

## Deviation from the parent design

The items above that change the parent's wording are the deviations: the `approved` state (it adjusts Shared decision 1's
`state = 'open'` and the parenthesis of decision 3 that the push exclusion and the decision input read only open items: they
still do, and an approved item is read by its own reader), the stale reference (decision 3's refusal now reads the item's own
hash, refreshed on every pull, instead of updating the item from the clone), the `new-pin` hash, the remote-hold edit rule, the
mandatory shown hash and the sticky reading of "restore". Shared decisions 1 to 9 are otherwise followed unchanged.
