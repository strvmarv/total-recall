# Git sync, milestone 1b-iii part 4: conflict review

Parent: #159, Part 4 (docs/superpowers/specs/2026-11-21-git-sync-review-secrets-design.md)

Date: 2026-11-24. Status: draft for review. Part of #93 (git sync); sub-ticket #164 of the parent #159. The parent spec
(`2026-11-21-git-sync-review-secrets-design.md`) governs: this document is the engineering design of its Part 4 and follows its
"Shared decisions". The umbrella design is `2026-11-15-git-sync-design.md` (its "Conflict review" section and merge tables); the
two earlier parts this one extends are `2026-11-22-git-sync-held-deletions-design.md` (the review service) and
`2026-11-23-git-sync-pin-review-design.md` (the item mechanism: arrival rule, reconcile planned before the transaction, answers on a
generic frame). Where this document is more precise it wins for this part; every departure is listed under "Departures".

## Goal

When both machines changed the **text** of the same memory since their common base, the clock already picks a winner and the pull
applies it. Today the pull postpones that memory instead (nothing is recorded, `L` is held back, the push is skipped), so one
conflict stalls the whole cycle and the loser's text is nowhere. After this part:

- the pull applies the clock's winner at once and files the loser's text as a `conflict` item (the applied text, every losing text with
  its machine and stamp), and **no decision postpones any more**;
- a conflict item is kept in the very pull that first holds a deletion for the id (W8.4 unproven), in a pull whose guard converts a push
  deletion into a hold, and is removed in a pull that carries the deletion out (W8.3 or an approved hold); the item step's gates and the
  in-transaction re-read cover conflict items (a conflict item changed before the transaction defers the entry);
- a conflict item and a pin-edit rule 4 (a held edit applied on a local unpin) in one pull: the two plans agree, the entry is not deferred;
  a conflict item survives a held local deletion (W8.4) and is still listed after `restore`, and is removed when the deletion is carried out;
- a second conflict on one entry appends its losing text to the same item without overwriting an earlier one;
- the person answers by id with `keep` (leave the applied text), `swap` (apply a losing text) or `merge` (apply a text they wrote),
  in the review service and in the hidden `sync review keep|swap|merge`; a `swap` or `merge` is an ordinary edit with a fresh stamp, so
  it wins at the next sync on every machine where the memory is not pinned (where it is pinned the text arrives as Part 3's held `pin-edit`, as with a declined edit);
- an unanswered conflict costs nothing and blocks nothing: the cycle completes, nothing is excluded from the push, and the item is
  only listed.

Success: with two machines, a local bare repository and temporary databases, two text edits leave the later one on both machines and
the loser in `sync review`; `keep`, `swap` and `merge` each leave the stated text on both machines after the next cycles; a second
conflict on one entry keeps both losing texts; the existing checks pass with nothing calling the cycle.

## Non-goals

- Any caller of the cycle, `sync now`, the `sync` status block, `session_start` arrivals and hints, the `sync_review` MCP tool, un-hiding
  the verbs (parent Shared decision 6: milestone 2 and Part 6). The verbs added here are hidden like the others.
- The alias pass and skip-insert (Part 5). It will file a differing local text as a `conflict` item; this part builds the helper it
  will call (D3) and nothing else for it.
- Any change to `SyncMerge.decide`'s rules (W1 to W14, FC4, FC5, NC4), to the record format, to the schema or the migration. The loser
  is what `decide` already hands over (side, text, stamp).
- Tags (set merge), tier, a pin against an unpin, an edit against a deletion and clock ties: not escalated (umbrella).
- Bulk answers: there is no "all" for conflicts (parent Shared decision 2).

## Design

### D1. The item

A `git_sync_review` row of kind `conflict`, key `(entry_id, 'conflict')`, no schema change. Columns: `state` `open` or `unseen` (every
reader treats `unseen` as open: it is answerable, listed and visited), `direction` NULL, `record_hash` = the shown hash (D2), `machine`
= the machine id of the newest loser's stamp (8 hex digits, as stamps carry it; shown as is), `arrived_at` = the first conflict's time
(kept when a later one is appended), `payload_json` =

    {"losers":[{"side":"local","text":"...","stamp":"<stamp text or empty>","machine":"<8 hex>"}, ...]}

oldest first, written with `Utf8JsonWriter` like the other payloads (NativeAOT: no reflection). **Appending never overwrites an earlier
text** (umbrella): a new loser is added at the end, except that a loser whose text equals one already kept is not added again (the same
text from either side is one alternative), and the list is bounded at **8** losers: a ninth drops the oldest (stated limit, see
Departures; records are up to 256 KiB, so an unbounded list would let a hostile repository grow the database). `side` is `local` for the
text this machine wrote and `remote` for the record's. `machine` of a loser is its stamp's `Machine`; a loser with no stamp (NC4 with no
row) takes this machine's id (`_meta git_sync_machine`, as `IssueStamp` reads it). The **applied** (winning) text is not stored in the
item: it is the stored entry's text, read live.

The item shows nothing else: a conflict is about text only (the other content fields follow the winner, umbrella; a loser carries only
text).

### D2. The shown hash

A new pure Core function `SyncConflict.shownHash (applied: string option) (losers: string list)`: SHA-256 (lowercase hex) over the prefix
`total-recall/review/conflict/v1\n`, the applied text as a field (presence byte, 4-byte big-endian length, UTF-8 bytes, as
`SyncPin.shownHash` writes a field; `None`, the entry absent while a deletion is held (D4 rule 1), is presence byte 0), a 4-byte big-endian count and each loser's text as a field, in payload order. Stamps and machines are
not hashed (a change of only those shows nothing new). The item's `record_hash` is the shown hash of the **stored entry's text** (none when the entry is absent) and the
payload's losers after the carried-out decision; every writer (arrival, reconcile) and every reader (list, answers) computes it from the
same raw fields. An answer carries the hash it was shown; a difference is **stale** (D6).

### D3. The pull stops postponing conflicts

`SyncApply.postponement` is removed with `PostponeReason` and the applier's early return: no decision postpones, so `complete` no longer
fails because of a decision (it still fails for deferrals, failures and cancellation). `ApplySummary.Postponed` and `PostponedEntry` stay
as the members they are (always empty now) so the cycle's report and tests keep their shape; removing them is a follow-up. The decision
is carried out as any other (nothing in `decide` changes): FC4 keeps the local text and its pending stamp (the push writes it), FC5 and
NC4 take the record's text and clear the pending stamp; each also carries `Conflict loser` in `Reviews`, and a `NewPin` or `PinEdit`
request of the same decision is handled by Part 3's rules in the same transaction (a conflict never opens a pin item and a pin item never
suppresses a conflict).

In the same transaction, after Part 3's `pins` step and in the same step, the applier runs the conflict reconcile (D4) and then the
**conflict arrival** for each `Conflict` request: no item: write `open` with the D2 hash, `payload` = that one loser, `machine` = the
loser's, `arrived_at` = the applier clock; an `open` or `unseen` item: append the loser (D1 rules) and write the new hash, state
`unseen` when the hash changed, nothing when it did not. Because after the carried-out decision the base equals the record, a repeated
pull classifies the content group `Unchanged` and raises nothing: idempotent.

**The loser the applier files** comes from the decision's `Conflict` payload (side, text, stamp), which `decide` fixed before the store
operations: FC5 and NC4 file the local text although the apply overwrites it; FC4 files the record's text. The applier does not re-read
the local text for this.

The planning, the in-transaction recheck, the no-op shortcut and the visit set work as Part 3 built them. Concretely: `GitSyncReviews` gains
conflict members (`ReadConflictItems`, `ReadConflictItem`, `UpsertConflictItem` with the payload and hash, `DeleteConflictItem`; every
member's SQL restricted to `kind = 'conflict'`, and the class doc's "conflict rows are read and changed by none" is corrected); a pure Core
`SyncConflict.append` is the one append rule (D1: the dedupe and the bound); the applier's `PinPlan` gains the conflict writes
(`ConflictWrite`: the action `Remove`, `Upsert` with the new payload, hash and machine, or `MarkUnseen`) and `PinPlan.Matches` compares them,
payload and hash included, so a recompute that differs defers the entry; the arrival is planned through that plan (not applied behind it)
and `ConflictItems.Append` is the same append rule applied by the writer for the alias pass. Every id with a conflict item (any state) is
added to the visit set (as pin-item ids are). A pull with nothing to do for a conflict item opens no transaction and rewrites nothing.
**The entry the conflict reconcile reads is the one `PinPlanning.Plan` reads for the pin rules**: the stored entry after the decision and,
when pin-edit rule 4 will apply the held base content, the entry as that rule leaves it (its text from the post-decision base). The
pre-transaction plan and the in-transaction recompute both use it, so a conflict item and a pin-edit rule 4 in one pull do not make the
two plans differ (a test covers it).

**Gates.** Part 3's gates are widened to conflict items: the decision to plan the item step
(`pinItems.Count > 0 || PinPlanning.Requests(decision).Count > 0`), `PinPlanning.Plan`'s early return and the in-transaction re-read all
consider conflict items (the re-read compares each conflict item's state, hash and payload) and `Conflict` requests (`Requests` today
ignores them). **Early returns.** A conflict item whose id never reaches the decision in a pull (the memory moved to knowledge, the id was skipped or its
apply failed) is neither reconciled nor refused by this pull; it is reconciled by the next pull that decides the id. A known limit that costs
nothing (an unanswered conflict blocks nothing).

**Helper for Part 5.** The arrival is also exposed as `ConflictItems.Append(conn, tx, id, loser, nowMs)` (no `MergeDecision` needed), so
the alias pass can file a differing local text; this part uses it for its own arrivals and tests it directly.

### D4. Reconciling a conflict item with the pull

Like Part 3's reconcile, a pure Core function `SyncConflict.reconcile` over the item (state, hash, losers' texts), the stored entry after
the decision (absent, or its text and shown fields) and the row (none, active, forgotten or left); planned before the transaction on the
expected post-decision state and recomputed inside it (a difference defers the entry, as Part 3). Rules, first match:

1. the row is `forgotten` or `left`, or the entry is absent **and the id has no held deletion after this decision**: remove the item (the
   memory is gone). "Held after this decision" is one input, the same in the planned and the recomputed reconcile, and not a database read:
   `decision.Entry` is `HoldDeletion` (including a push deletion the guard converted, `SyncDeletion.holdInstead`), or the id's pre-read held
   item is open or approved and the decision does not close it (`CloseHeldDelete` false). The item step runs before the hold is upserted
   (`CarryOut`), so reading the database would miss the pull that creates the hold. A held deletion of this machine's memory (W8.4: the entry is already gone here, the deletion is unproven or stopped by
   the guard) leaves the item, because for a record-wins conflict it holds the only copy of the local text and a later `restore` brings the
   memory back with its alternatives; accepting or carrying out the deletion removes the entry's row, which removes the item. **A row that
   does not exist yet is not a reason to remove**: after `sync init` or `sync join` the items survive and the rows come back later (the existing survival rule
   stays under test);
2. (an entry is present) losers whose text equals the entry's text are dropped (they were applied, typed or re-published); no loser left: remove the item;
3. the shown hash differs from the item's (the applied text or the losers changed; with the entry absent under a hold rules 2 and 3 do
   nothing, the hash being the one with no applied text): `unseen` with the new hash and, when losers were
   dropped, the new payload;
4. otherwise leave the item as it is.

A local edit of the text after the conflict does not close the item: the applied text changed, so rule 3 makes it `unseen` and the person
can still `swap` to a kept alternative. A text that came back equal closes it by rule 2.

### D5. Held items, pins and secrets

A conflict item is independent of the other kinds on the same id (the key includes the kind): a `held-delete` item, a `new-pin` and a
`conflict` can coexist. A deletion that is carried out (W6.4, W8.3, an accepted held deletion) removes the item by D4 rule 1; a held deletion of either direction
leaves it (D4 rule 1). The push never excludes an entry for a conflict item (`SyncPush.exclusion` reads `held-delete` only). A secret-blocked entry
still takes part in the pull, so a conflict can arise on it; a loser holding a token stays local in the payload (it is never pushed:
payloads are not records), and a `swap`/`merge` of such text is held back by the push's own secret check (Part 1).

### D6. The answers

`ReviewService.KeepAsync(id, shownHash)`, `SwapAsync(id, shownHash, loserIndex)` and `MergeAsync(id, shownHash, text)` on the generic frame
(sync lock, migrations, one immediate transaction that re-reads the item, row and entry; a refusal writes nothing except as stated for
`stale-item`). `loserIndex` is the 1-based position in the list as shown (newest first); with exactly one loser it may be omitted (the
service takes 1), with several it is required (`unknown-loser` when absent or out of range).

Checks, in this order, each a refusal: the item exists and is `open`/`unseen` (`no-conflict-item`); the shown hash is present
(`shown-hash-required`); the entry is read for all three verbs (absent: `stale-item`, nothing written, with the text "the next sync closes the item" or, while a held deletion exists for the id,
"restore or accept the held deletion first";
duplicated or unreadable: `unreadable-entry`); `swap` and `merge` also need the row `active` with a base (`failed`, with the text "not
synced yet; run a sync first" when the row is missing, which is the state after `sync join` until the first cycle), while `keep` needs
no row; the shown hash equals the live hash recomputed by D2 from the entry's text and the payload's losers (a
difference is **stale**: the item is updated to the live hash and `unseen` and the answer is `stale-item` with what the item shows now;
this is the one write of a refusal, committed as Part 3's stale refusal is); for `swap`, the loser index; for `swap` of a **remote** loser, the chosen text must
be showable (no character the list would remove: `unshowable-text`; it came from a repository record; a local loser is this machine's own
text and is swapped as it is, whatever characters it holds); for `merge`, the text is not empty
or whitespace only and at most 262144 UTF-8 bytes (`bad-merge-text`, the record limit) and has no NUL; for `swap` and `merge` on a **pinned**
entry the resulting text must fit the pinned cap (`tiers.pinned.max_content_chars`, 0 or less meaning 500; `over-pin-cap`, as `approve`
does); a text equal to the entry's current text is accepted and writes nothing but the item's removal.

- **`keep`:** the item is removed. Nothing else changes (the applied text stays, a pending push stays pending).
- **`swap`/`merge`:** the chosen text becomes the entry's text, and only the text: project, scope, entry type, source, `created_at`, tags and
  tier are untouched, except that the hot content limit rule `TakeTheirs` uses applies (a hot, non-pinned entry whose new text is over
  `tiers.hot.max_content_chars` is stored warm). The entry is re-embedded (the vector for the chosen text is computed before the transaction; inside it the
  stale-hash check, which covers the entry's text and every loser's text, proves the chosen loser is the one that was shown). The row gets a fresh content stamp above
  the greater of the base's content stamp and the row's pending content stamp (`IssueStamp`), left **pending**, and its scanned content
  hash and the other comparison columns recomputed from the stored entry (so the scan stamps nothing more and the pull's agreement check
  holds). The item is removed. Nothing is pushed by the answer: the next cycle writes the text as an ordinary edit, which wins on every
  machine because its stamp is greater than the record's (and than the one the other machine holds; where the memory is pinned it is
  held there as a `pin-edit`, and the answer says so, as Part 3's decline does). The floor is the base's and the pending stamp: if the
  clone's current record is newer than the base (an incomplete earlier pull), a later pull meets the swapped text as an ordinary conflict,
  which is accepted; a machine that has meanwhile edited
  the text again meets it as an ordinary conflict. A text that holds a secret is held back by the push's own check.
- A refusal other than `stale-item` leaves the item and everything else untouched.

### D7. Surfacing: the list and the commands

`ReviewItem` gains `Losers: IReadOnlyList<ConflictLoser>` (side, machine, stamp text, `Text`, `TextCut`; empty for the other kinds) and a
`conflict` item fills `Text` with the **applied** text (source `local entry`), `Machine` with the item's machine, `Project`/`Scope`
null. `List()` returns open and unseen conflict items, ordinary by id then kind. The hash shown is computed live (D2). The text rules are
the existing ones (delimited, `| `-prefixed, capped at `tiers.pinned.max_content_chars`, control, line-separator and bidi characters
removed; machine and stamps through the field sanitiser). The hidden commands, in `ReviewCommand`:

- `sync review keep <id> --shown <hash>`, `sync review swap <id> --shown <hash> [--loser <n>]`, `sync review merge <id> --shown <hash>
  --text-file <path|->`: **`--shown` is required** (a usage error, exit 2, without it; 8 or more hex digits, a unique prefix of the item's
  hash, as for `approve`). `--text-file -` reads standard input; the file or input is read as UTF-8, at most 262144 bytes, and an
  unreadable, invalid or oversized input is a usage error that changes nothing. The CLI resolves the item by `(id, conflict)`.
- `list` prints, for a conflict, a header `<id>  conflict  <state>  arrived <ISO>  shown <h|->` followed by the applied text block (source
  `local entry`; with the entry absent under a held deletion the line `entry deleted here, deletion held`) and one block per loser numbered `loser 1` (newest) upward, each labelled with its side, machine and stamp and its text
  behind the delimiter. The bulk forms and `accept`, `restore`, `approve`, `decline` look at their own kinds only and ignore conflicts.
- An id can carry a conflict and another kind at once; each verb pair answers its own kind and refuses `no-conflict-item`/`no-pin-item`/
  `no-held-item` for the wrong one.

### D8. Removing the postponement: what changes in the tests

Every test that asserted that a conflict postpones, and every cycle or pull test that used a conflict only to make the pull incomplete,
changes in the task that lifts the postponement, each named in its commit message: `SyncApplyTests.fs` (the postponement cases and the
property), `GitSyncApplierEntryTests` (the NC4 and FC cases that asserted a postponement), `GitSyncApplierPinReviewTests`
(`ANewPinWithAConflict_StillPostpones...`, `AnNC4Conflict_StillPostpones`), `PinReviewCycleTests` (`ANewPinWithAConflict_StillMakesBsCycleIncomplete`),
`GitSyncApplierPullTests`, `GitSyncApplierSafetyTests`, `GitSyncCyclePullTests`, `GitSyncCyclePushTests` and `GitSyncCycleDeletionTests` (the
cases that used an NC4 conflict as an incomplete-pull device), and `ReviewServiceCycleTests` where it relies on a conflict. A different incomplete-pull device is used where a test
needs one (a deferral through the `BeforeTransaction` hook or a cancellation, which the rigs already have). The acceptance-criteria draft
file gains this part's heading and replaces its sentence that a pin with a conflicting edit is "held back whole until conflicts can be
answered".

## Tests (what proves it)

Core: `SyncConflict.shownHash` (a fixed known answer built from the stated layout; field boundaries; losers' order and count matter),
`SyncConflict.reconcile` (every rule and its order), `SyncConflict.append` (dedupe, bound, order), and the `decide` conflict cases unchanged. That nothing postpones is proved by applier and cycle
tests: the former postponement cases (FC4, FC5, NC4, `NewPin` plus `Conflict`) are carried out and the pull is complete.

Infrastructure (`GitSync`): `GitSyncReviews` conflict members (read, upsert, append with the dedupe and the bound of 8, mark unseen,
delete) with kind-restricted SQL, applier tests, service tests, and cycle and two-machine proofs, one test per row:

- FC4 (local newer), FC5 (record newer) and NC4 (no base): the winner is applied or kept at once, the pull is complete and `L` advances,
  one `conflict` item exists with the loser's text, machine and stamp, side, the D2 hash, and the push of a local winner still happens;
  a repeated pull changes nothing and opens no transaction; `NewPin` plus `Conflict` writes both items; a pinned entry whose own newer
  edit wins (FC4) files the record's text;
- a second conflict on one entry appends (both texts kept, state `unseen`), the same text twice is one loser, a ninth loser drops the oldest;
- reconcile: entry deleted or row forgotten/left removes the item, a missing row keeps it (and `sync init`/`join` keep it), a loser equal to
  the entry text is dropped and an item with no loser is removed, an applied-text change makes it `unseen`, an unchanged pull rewrites
  nothing; the visit set reaches a conflict id that nothing else visits; `ApplyDrop` removes the item;
- `keep`: the item is removed and nothing else changes (a pending local push still pushes); `swap`: the loser's text becomes the entry's
  text, re-embedded, project/scope/type/source/tags/tier unchanged, a fresh stamp above both, comparison columns current, the scan stamps
  nothing more, the next cycle pushes the text and the other machine ends with it (two machines, both texts checked after both cycles);
  `merge`: the same with the given text; each of the three answers leaves the stated text on both machines;
- refusals, each writing nothing: no item, no hash, wrong hash (`stale-item`, the item `unseen` with the live hash and the live loser
  list, nothing else written), unknown or missing loser index with several losers, unshowable loser text, empty or oversized or NUL merge
  text, over the pinned cap on a pinned entry, a row that does not exist yet (`failed`, "run a sync first"), a held lock (`busy`), a fault
  at the last write rolls everything back; a `swap` over the hot limit stores the entry warm; a `swap` with a secret in the text is held
  back by the push;
- an unanswered conflict blocks nothing: the cycle completes, nothing is excluded from the push, status and the other items are
  unaffected;
- `ConflictItems.Append` (the Part 5 helper) files a conflict without a decision and follows the same dedupe and bound rules;
- CLI: `keep`/`swap`/`merge` with `--shown` (prefix, wrong, none), `--loser`, `--text-file` file and `-`, bad inputs, list shows the applied
  text and numbered losers delimited and sanitised, the other verbs ignore conflicts, the verbs stay hidden (`HiddenCommandTests` needs no
  change), `GitSyncCycleCallerTests` unchanged and no new file outside the pusher and the cycle names them;
- the existing tests listed in D8 are changed in the task that lifts the postponement.

## Documentation

`2026-11-15-git-sync-acceptance-criteria.md` gains this part's draft criteria under its own heading and the replaced sentence (D8); the
contributor guides (`AGENTS.md` rows, `src/TotalRecall.Infrastructure/AGENTS.md`: the postponement paragraph now says nothing postpones, the
conflict item, its payload, the reconcile, the answers and the Part 5 helper) are updated. No user document or README names the verbs.

## Acceptance criteria mapping (ticket #164)

The ticket's criteria are in the ticket; the design answers them as: the later edit wins on both machines and the loser is listed (D3, D1,
D7); `keep`, `swap` and `merge` leave the stated text on both machines (D6); a second conflict keeps both texts (D1, D3); an unanswered
conflict costs nothing (D3, D5); stale answers (D6); the items survive `sync init`/`join` (D4 rule 1); the list, hidden verbs and the
existing checks (D7, Tests).

## Departures from the earlier designs

- **The item stores the losers only; the applied text is read live** (parent and umbrella: "both versions, both machines, both stamps"). The
  applied text, its machine and its stamp are the entry's own state and change with every later pull and edit, so a copy in the payload
  would go stale; the list shows the applied text, and the losers carry their own side, machine and stamp.
- **Nothing postpones any more** (parent Shared decision 7 says Part 4 lifts the postponement for `conflict`; being the last kind, the
  postponement code goes with it, while the summary members it fed stay, always empty, for a later clean-up).
- **The losers list is bounded at 8** (the umbrella says the new text is appended and no earlier text is overwritten; an unbounded list
  lets a hostile record writer grow the database, so the ninth drops the oldest; identical texts are one alternative).
- **The shown hash covers the applied text and the losers' texts**, not "the record" (parent Shared decision 3 speaks of a record hash; for
  a conflict the record may be the loser or the winner, so the item's own content is what the person was shown).
- **`keep`, `swap` and `merge` all remove the item** (the schema has a `resolved` state; the loser text is in the repository history or
  was applied, and an answered item that never closes would need its own lifecycle).
- **A local edit after the conflict does not close the item** (it makes it `unseen`); only an equal text, a deleted memory or an answer
  closes it.
- **`merge` takes its text from `--text-file <path|->`**, the only free-text input the CLI has so far.
- **`--shown` is required for all three verbs**, as for `approve` and `decline`, whereas Part 2's `accept`/`restore` let the CLI read the
  hash for a person.
- **A stale refusal commits the item's update**, as Part 3's does.
- **New refusal names** `no-conflict-item`, `unknown-loser` and `bad-merge-text` in `Core.SyncSetup`.

## Deviation from the parent design

The departures above that change the parent's wording are the deviations from the parent spec: nothing postpones (parent Shared decision 7,
last kind), the item keeping the losers only (Shared decision 1 and the umbrella's "both versions"), the shown hash on the item's own content (Shared decision 3), the required `--shown`, the bound on the losers list, and the
answer-removes-item rule. Shared decisions 1 to 9 are otherwise followed unchanged: one item store keyed `(entry_id, kind)` and one service
(1), answers per item by id (2), stale answers and `unseen` (3), non-sticky inserts (4: unchanged, no insert is added), text from the
repository is data (5: the list delimits and sanitises it and `swap` refuses unshowable text), the verbs hidden and the cycle without a
caller (6), the postponement lifted for the last kind (7), nothing deleted that the rules do not prove (8), tests as the verification (9).
