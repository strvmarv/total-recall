# Git sync, milestone 1b-iii part 3: pin review

Parent: #159, Part 3 (docs/superpowers/specs/2026-11-21-git-sync-review-secrets-design.md)

Date: 2026-11-23. Status: draft for review. Part of #93 (git sync); sub-ticket #163 of the parent #159. The parent spec
(`2026-11-21-git-sync-review-secrets-design.md`) governs: this document is the engineering design of its Part 3 and follows its
"Shared decisions". The umbrella design is `2026-11-15-git-sync-design.md` (its "Pin review" section and transition table); the review
service this part extends is described in `2026-11-22-git-sync-held-deletions-design.md`. Where this document is more precise it
wins for this part; every departure is listed under "Departures".

## Goal

A pin that arrives from the repository never takes effect until the person approves it, and an edit that arrives for a memory
pinned here is held until the person decides. After this part:

- the pull no longer postpones a decision that needs a pin review: a pin from another machine becomes an ordinary hot entry with a
  `new-pin` item, and a remote edit to a memory pinned here is held as a `pin-edit` item while the pin's current text stays active;
- the person answers each item by id: `approve` (the pin path, no push) or `decline` (a declined new pin stays ordinary; a declined
  edit re-asserts the local text and pushes it), in the review service and in the hidden `sync review approve|decline`;
- the transitions of the umbrella table hold: a changed record updates the item and marks it unseen, a remote unpin applies, a
  local pin, unpin, edit or deletion closes the item.

Success: with two machines, a local bare repository and temporary databases, a pin from A is an ordinary hot entry on B and absent
from the pinned block until approved; approval pins without a push; a stale answer is refused; a decline never unpins A; a held edit
never reverts the remote edit and never pushes the old text; every insert from a sticky record yields a non-sticky entry and an
item; and the existing checks pass with nothing calling the cycle.

## Non-goals

- Any caller of the cycle, `sync now`, the `sync` status block, `session_start` arrivals and hints, the `sync_review` MCP tool, un-hiding
  the verbs (parent Shared decision 6: milestone 2 and Part 6). The verbs added here are hidden like the others.
- Conflict items (Part 4): a decision that carries a `Conflict` request stays postponed exactly as today, even when it also carries
  a `NewPin` (parent Shared decision 7).
- The alias pass and skip-insert (Part 5), which will write `pin-edit` and `new-pin` items through the helper this part builds.
- Any change to `SyncMerge.decide`'s rules (W1 to W14, the field rules), to the record format, to the schema or the migration.
- Bulk answers: there is no "all" for pins (parent Shared decision 2).

## Design

### D1. The shown hash

A new pure Core function `SyncPin.shownHash (text) (project) (scope)`: SHA-256 (lowercase hex) over the prefix
`total-recall/review/pin/v1\n` and the three fields, each written as the other hashes of `SyncScan`/`SyncSecret` write a field (a
presence byte, a 4-byte big-endian length, the UTF-8 bytes; `None` and the empty string differ). The item's `record_hash` is the
shown hash of **what the item shows**, so a tag, tier or stamp change that leaves the shown fields alone never makes an answer stale
(the umbrella: "the `record_hash` of what they show (the text, `project` and `scope`)"). What each kind shows:

- `new-pin`: the entry's text, project and scope **as they are here** (that is what `approve` would pin). After an insert from the
  record they equal the record's.
- `pin-edit`: the held record's text, project and scope, which are the content group of the row's `base_json` (the carried-out
  decision advances the base's content group to the record's, and a pending content stamp is cleared, so the base is exactly the
  held record). The current pin text is the entry's own.

Part 2's `RestoreLocal` wrote a blob id as the `record_hash` of the `new-pin` item it opens. It writes the shown hash of the base's
text, project and scope instead (a small change to `ReviewService.Restore.cs` and its tests).

### D2. Item shape and state

`git_sync_review` rows of kind `new-pin` and `pin-edit` (no schema change): `direction` NULL, `record_hash` = D1, `machine` = the
record's `WrittenBy`, `arrived_at` = the applier clock, `payload_json` `{}` for a pull-made item (the texts are read live: the entry's
for a `new-pin`, the base's for a `pin-edit`) and `{"origin":"restore"}` for Part 2's restore (kept as is). States: `open` (shown),
`unseen` (the record or the entry changed since it was shown; every reader treats `unseen` as open: it is answerable and blocks what
an open item blocks), `approved`, `declined`. `approved` and `declined` items stay in the table: they carry no weight, but a new
arrival of the same kind (below) re-opens them. A new `GitSyncReviews` set of members reads, upserts and removes pin items
(`ReadPinItems` of state `open`/`unseen`, `ReadPinItem(id, kind)`, `UpsertPinItem`, `MarkPinUnseen`, `ResolvePinItem(id, kind,
state)`, `DeletePinItem`); no member reads held-delete items.

### D3. The pull stops postponing pins

`SyncApply.postponement` returns `Review` only when the decision carries a `Conflict` request; `NewPin` and `PinEdit` no longer
postpone. The applier then carries the decision out as it does for any other (nothing in `decide` changes):

- an insert from a sticky record (W1, W3, W8.1) inserts an ordinary entry (no sticky flag: `InsertEntryOpts` has none), in the tier
  the apply chooses (a sticky record is hot; over the hot content cap it goes warm, and `approve` moves it);
- FS4, FS7 and NS3 keep the entry unpinned and apply the other groups as usual;
- FC2 and NC2 hold the content group (no content update; tags and tier still apply; the base's content group advances to the record's
  and the pending content stamp is cleared, as today).

In the same transaction the applier writes the item: `UpsertPinItem(new-pin | pin-edit)`. **Arrival rule** (one pure Core
function, `SyncPin.arrival`): no item, or an `approved` or `declined` one: write `open` with the new hash; an `open` or `unseen` one
with the same hash: leave it; with a different hash: `unseen` with the new hash. The first two cases need the decision to raise the
request again, which `decide` does only for a genuinely new pin or a changed pinned text (after the carried-out decision the base
equals the record, so a repeated pull is `Unchanged`: idempotent).

### D4. Reconciling an item with the pull (the transitions)

`decide` cannot see items, so the applier reconciles them after the decision, in the entry's transaction, with one pure Core function
`SyncPin.reconcile` over: the item (kind, state, hash), whether the entry is present and sticky, whether the row has a pending
sticky or content stamp, the record (present, sticky, its shown hash) and the decision's own requests. Every id with a pin item in
state `open`, `unseen`, is **added to the pull's visit set** (as held-delete ids are), so a local change that no record change
accompanies still reaches it. Outcomes, by kind:

| event (seen at this pull) | `new-pin` | `pin-edit` |
|---|---|---|
| the entry is gone (deleted here, deleted by the apply, row forgotten or left) | item removed | item removed |
| the entry is sticky (the person pinned it here, or an approval) | item removed (an ordinary local pin) | n/a |
| the record is absent, or `theirs.sticky` is false (a remote unpin) | item removed (the apply already applied the unpin) | item removed if the entry is not sticky, else unchanged |
| the record's shown hash differs from the item's (the record changed while pending) | `unseen`, hash updated | `unseen`, hash updated |
| the entry is no longer sticky here (the person unpinned, or a remote unpin applied) | n/a | **the held text is applied** (D5) and the item removed |
| the entry's content has a pending local stamp (the person edited the pinned text) | n/a | item removed; the local edit stands and meets the record as an ordinary pending change |
| nothing changed | kept as it is | kept as it is |

A `declined` or `approved` item is reconciled by the same table, so a removal also clears it; an `approved` pin-edit or new-pin whose
entry is later unpinned is removed. `NewPin`/`PinEdit` requests raised by the decision upsert through D3's arrival rule after the
table was applied. The no-op shortcut of the apply counts every item write, update and removal as touching the item (as D10 of the
held-deletion part did).

### D5. A local unpin applies the held edit

Owner decision. When the person unpins a memory whose remote edit is held, the item closes **and the held text is applied**, so the
memory does not silently stay on different text from every other machine: the unpinned entry is ordinary, and the record's edit
arrives like any other remote edit. Inside the entry's transaction the applier updates the entry's text, project and scope to the
base's content group, replaces the embedding, records the row's comparison columns from the stored entry (as every apply write
does), clears nothing else, and removes the item. The text goes through the same checks any arriving text does (the hot content
limit tier rule of the apply; no pin cap, the entry is no longer a pin). A person who unpins to get rid of a held edit should decline
it first.

### D6. `approve`

`ReviewService.ApproveAsync(id, shownHash)` for a `new-pin` or `pin-edit` item of `open` or `unseen` state, in the service's
usual frame (sync lock, migrations, one immediate transaction that re-reads the item, row and entry; refusals leave everything as it
is, except the stale rule below). Checks, in this order, each a refusal that changes nothing: the item exists (`no-pin-item`); the
row is `active` with a base and the entry is read (`failed`, `unreadable-entry`); the shown hash is present (`shown-hash-required`);
the entry's state fits the kind (a `new-pin` needs an entry that is not sticky, a `pin-edit` one that is: otherwise `stale-item`,
the next pull closes the item); the shown hash equals the item's current shown hash recomputed live from what the item shows
(D1); a difference is **stale**: the item is updated to the live hash and set `unseen`, the answer is `stale-item` with what it
shows now (parent Shared decision 3); the pin cap (below).

- **`new-pin`:** applies the pin path inside the transaction: the entry moves to hot when it is not there (`Move`, which copies the
  vector), the sticky flag is set, and the row is written with `local_sticky` true, `local_tier` hot, the pending sticky stamp NULL
  and every other column as it was, so the approval is not a local change to push (the base already holds the record's pin and its
  stamp). The item becomes `approved`. Nothing is pushed.
- **`pin-edit`:** the held text, project and scope (the base's content group) are applied to the pinned entry (`Update`, new embedding),
  the row's scanned content hash is recomputed from the stored entry, the pending content stamp stays NULL, and the item becomes
  `approved`. The entry stays pinned. Nothing is pushed (the base already holds the record's text).
- **The cap.** The text that becomes the pin (the entry's, or the held text) is checked against the **pinned cap**
  (`tiers.pinned.max_content_chars`, default 500, the limit the shown text is cut at, so the person approves what they saw); over it
  the answer is refused `over-pin-cap` with the cap and the length in the message, and the item stays open. The wording is
  `PinnedTierLimits`' own.
- The scan's `local_sticky` rule: after an approval `local_sticky` equals the entry, so the next scan stamps nothing.

### D7. `decline`

`ReviewService.DeclineAsync(id, shownHash)`, same frame and the same checks (the hash is required for both kinds so a decline never
answers an edit the person has not seen).

- **`new-pin`:** the entry stays an ordinary local entry; the item becomes `declined`; the record is left alone (the pin remains on
  other machines); no stamp, no push.
- **`pin-edit`:** the person chose the local text. The row gets a fresh content stamp above the base's content stamp (`IssueStamp`
  with the base's stamp as the floor; the base's content group is the record's), left **pending**, with the comparison columns as they
  are; the writer then pushes the local text, project and scope as an ordinary content change (the push's own secret re-check applies).
  The item becomes `declined`. The answer says: "this reverts that change on your other machines". The stamp is the only write besides
  the item.

### D8. Surfacing: the list and the commands

`ReviewItem` gains `Kind` (`held-delete`, `new-pin`, `pin-edit`); `List()` also returns the open/unseen pin items. A pin item shows,
by the same text rules as a held deletion (delimited, `| `-prefixed, capped at `tiers.pinned.max_content_chars`, control, line-separator
and bidi characters removed): for a `new-pin` the entry's text with the source `local entry`; for a `pin-edit` the held text with the
source `repository record` and, beside it, the current pin text with the source `local entry`; both: project, scope, the shown hash,
the record's machine and when it arrived, and the state (`open` or `unseen`). The hidden commands, in `ReviewCommand`:

- `sync review approve <id> [--shown <hash>]` and `sync review decline <id> [--shown <hash>]`: the `--shown` rules are those of
  `accept`/`restore` (8 or more hex digits, a unique prefix of the item's hash; without it the **CLI** passes the hash it reads, the
  service never does). The service refuses an answer with no hash. A `decline` of a `pin-edit` prints the revert warning before it runs.
- `list` prints the kind in each header. The bulk forms (`accept-deletions`, `restore-deletions`) answer held deletions only; there is
  no bulk form for pins.
- An id can carry a held deletion and a pin item at once (a pinned entry whose record was removed, with a held edit); `accept`/`restore`
  answer the former and `approve`/`decline` the latter, each refusing `no-held-item`/`no-pin-item` for the wrong kind.

### D9. The two small Part 2 corrections

1. `RestoreLocal` writes the `new-pin` item's `record_hash` as the shown hash of the base's text, project and scope (D1) and the
   record's `WrittenBy` as `machine`; its tests change accordingly.
2. The stale text of a held-delete answer is unchanged. `ReviewService`'s item-reading members (`CheckItem`) become kind-aware helpers
   shared by the pin answers; the held-deletion behaviour and its tests stay as they are.

## Tests (what proves it)

Core (`Sync` tests, pure): `SyncPin.shownHash` (a fixed known answer computed in the test from the stated layout; `None` vs empty; field
boundaries `"a","bc"` vs `"ab","c"`), `SyncPin.arrival` (every state/hash cell), `SyncPin.reconcile` (every row of the D4 table), and the
`SyncApply.postponement` cases of the existing tests (`NewPin` and `PinEdit` no longer postpone; `Conflict`, alone or with `NewPin`, still
does).

Infrastructure (`GitSync`): `GitSyncApplier*` and a new `GitSyncApplierPinReviewTests`, `ReviewServicePinTests`, and cycle and two-machine
proofs, one test per row:

- a pin from A: on B an ordinary hot entry (`sticky` false), row `local_sticky` false, base holds the record, a `new-pin` item `open` with the
  shown hash and the record's machine, the pinned block does not list it, the pull is complete (L advances); an insert over the hot cap goes
  warm and `approve` moves it to hot; W3, W8.1 and Part 2's restore inserts of a sticky record all end non-sticky with an item;
- `approve` of a `new-pin`: the entry is sticky, hot, in the pinned block, `local_sticky` true, no pending stamp, item `approved`, and a
  following cycle pushes nothing; refused over the pinned cap with the cap in the message and the item `open`; a stale hash (the record's text
  or the entry's text changed since shown) is `stale-item`, the item `unseen` with the new hash; no hash is `shown-hash-required`; an entry
  already pinned is `stale-item`;
- `decline` of a `new-pin`: the entry stays ordinary, item `declined`, no stamp, a cycle pushes nothing and A's record is unchanged (A is not
  unpinned: `TwoMachineRig`); a later genuinely new pin from A re-opens the item; a repeated pull of the same record changes nothing;
- the transitions: record changes while pending → `unseen`, hash updated; `theirs.sticky` false → item removed and an entry pinned here is
  unpinned by the apply as today; the entry deleted by the apply or here or forgotten → item removed; the person pins it here → item removed
  and the pin pushes as an ordinary local pin; a remote unpin of an entry pinned here applies;
- a remote edit to a memory pinned on B: the pin text stays active, tags and tier applied, a `pin-edit` item with the held text's hash; a tag
  edit on B while held pushes only the tags (the old text is never pushed, the remote edit is not reverted); a second remote edit → `unseen`;
- `approve` of a `pin-edit`: the entry text, project and scope are the held ones, re-embedded, still sticky, scanned content hash current, no
  pending stamp, item `approved`, nothing pushed; over the pinned cap refused, item open;
- `decline` of a `pin-edit`: a content stamp above the base's is pending, a cycle pushes B's text and A's record then holds B's text (the
  remote edit is reverted on A: shown with `TwoMachineRig`), item `declined`, the answer carries the revert warning; a text with a token is
  held back by the push's secret check;
- local unpin while an edit is held → the held text is applied (text, project, scope, embedding), the entry is ordinary, the item removed,
  a following cycle pushes the unpin only; a local edit while held → item removed and the edit pushes as an ordinary change; both at once →
  the edit stands;
- decisions with a `Conflict` still postpone (`NewPin` + `Conflict` and an NC4 case);
- the item readers treat `unseen` as open (reconcile, answer, list, the visit set); the visit set contains every pin-item id; a no-op pull
  does not rewrite an unchanged item;
- the service holds the sync lock (busy), the answers are one transaction (a fault seam at the last write rolls everything back);
- Part 2 restore: the `new-pin` item carries the shown hash and the record's machine;
- CLI: `approve`/`decline` with `--shown` (prefix, short prefix, wrong hash, none), list shows kind, delimited capped text and the current
  pin text for an edit, the revert warning, bulk forms ignore pin items, the verbs stay hidden (`HiddenCommandTests` needs no change);
- the existing tests that asserted the old postponement (`SyncApplyTests.fs`, `GitSyncApplierEntryTests`, `GitSyncApplierSafetyTests`,
  `GitSyncApplierRepushTests.ReviewStillPostpones`, `GitSyncApplierPullTests` count, and the cycle tests that used a pin record only to make
  the pull incomplete) are changed in the task that lifts the postponement, each named in the commit message; `GitSyncCycleCallerTests` is
  unchanged and no new file outside the pusher and the cycle names them.

## Documentation

`2026-11-15-git-sync-acceptance-criteria.md` gains this part's draft criteria under its own heading; the contributor guides (`AGENTS.md`
rows for pin review, `src/TotalRecall.Infrastructure/AGENTS.md` paragraphs for the pin items, the arrival and reconcile rules, `approve`/`decline`
and the corrected postponement sentence) are updated. No user document or README names the verbs.

## Acceptance criteria mapping (ticket #163)

The ticket's criteria are in the ticket; the design answers them as: a pin from another machine and the pinned block (D3); approve and the
shown hash (D6, D1); stale answers (D6); decline of a new pin (D7); a remote unpin and the other transitions (D4); the pin cap (D6); a held
edit and no revert (D3, D7, the writer's own patching); approve and decline of an edit (D6, D7); a local unpin or edit (D4, D5); every insert
from a sticky record (D3); a decision with a conflict still waits (D3); the list, no bulk and hidden verbs (D8); the existing checks (Tests).

## Departures from the earlier designs

- **The pin cap is `tiers.pinned.max_content_chars`.** The umbrella says "the same hot content cap as `memory_pin`"; the MCP `memory_pin`
  uses the hot cap (default 1200) and the CLI `memory pin` the pinned cap (default 500). The parent spec's Part 3 says "the pin cap", the
  draft criteria say "over the pin cap with the cap named", and the text shown to the person is cut at the pinned cap, so approval uses
  it: the person approves what they were shown.
- **The shown hash covers the shown fields (text, project, scope), not the record's blob id** (the umbrella says so; Part 2's restore wrote a
  blob id and is corrected, D9). A tag-only change does not make an answer stale.
- **A local unpin applies the held edit** (owner decision, D5). The umbrella's table says "the item is closed and the local state stands",
  which leaves this machine on text no other machine has.
- **Every closing and updating transition is done by the pull's reconcile, not the scan.** The umbrella lists events; the scan stays
  unchanged (it keeps pin items on reactivation as its tests pin), and the pull visits every pin-item id.
- **`approved` and `declined` items stay until a new arrival or a removal event.** The umbrella names the states but not their later
  lifecycle.
- **Both answers require the shown hash at the service**, a `decline` included (parent Shared decision 3 speaks of an answer that "carries a
  `record_hash`").

## Deviation from the parent design

The items above that change the parent's wording are the deviations: the pinned cap (parent Part 3 says "the pin cap": the same), the
mandatory hash for `decline`, the unpin rule and the reconcile-in-the-pull rule. Shared decisions 1 to 9 are otherwise followed unchanged:
one item store keyed `(entry_id, kind)` and one service (1), answers per item by id (2), stale answers (3), non-sticky inserts (4), text from
the repository is data (5), the verbs hidden and the cycle without a caller (6), conflicts still postponed (7), nothing deleted that the rules
do not prove (8), tests as the verification (9).
