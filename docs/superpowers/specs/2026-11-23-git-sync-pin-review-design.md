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
(the umbrella: "the `record_hash` of what they show (the text, `project` and `scope`)"). What each kind shows, and so what its hash is
taken from, always **after** the pull's decision was carried out (the stored entry and the post-decision base):

- `new-pin`: the **stored entry's** text, project and scope (that is what `approve` would pin). After an insert they equal the record's;
  with an unpushed local edit they are the entry's own, and the arrival, the reconcile and the answers all use this one definition.
- `pin-edit`: the **post-decision base's** content group (text, project, scope). The carried-out decision advances the base's content
  group to the record's and clears a pending content stamp, so the base is exactly the held record. The current pin text is the
  entry's own.

Part 2's `RestoreLocal` wrote a blob id as the `record_hash` of the `new-pin` item it opens. It writes the shown hash of the entry it
re-inserts instead, and the record's `WrittenBy` as `machine` (a small change to `ReviewService.Restore.cs` and its tests).

### D2. Item shape and state

`git_sync_review` rows of kind `new-pin` and `pin-edit` (no schema change): `direction` NULL, `record_hash` = D1, `machine` = the
record's `WrittenBy`, `arrived_at` = the applier clock, `payload_json` `{}` for a pull-made item (the texts are read live) and
`{"origin":"restore"}` for Part 2's restore (kept as is). States: `open` (shown), `unseen` (what the item shows changed since it was
shown; every reader treats `unseen` as open: it is answerable, listed, visited and blocks what an open item blocks), `approved` and
`declined` (an answered item). A new `GitSyncReviews` set of members reads, upserts and removes pin items (`ReadPinItems`,
`ReadPinItem(id, kind)`, `UpsertPinItem`, `MarkPinUnseen`, `ResolvePinItem(id, kind, state)`, `DeletePinItem`); no member reads
held-delete items. The list shows `open` and `unseen` items only.

### D3. The pull stops postponing pins

`SyncApply.postponement` returns `Review` only when the decision carries a `Conflict` request; `NewPin` and `PinEdit` no longer
postpone. The applier then carries the decision out as it does for any other (nothing in `decide` changes):

- an insert from a sticky record (W1, W3, W8.1, and Part 2's restore) inserts an ordinary entry (`InsertEntryOpts` has no sticky
  field), in the tier the apply chooses (a sticky record is hot; over `tiers.hot.max_content_chars` it goes warm, and `approve`
  moves it);
- FS4, FS7 and NS3 keep the entry unpinned and apply the other groups as usual;
- FC2 and NC2 hold the content group (no content update; tags and tier still apply; the base's content group advances to the record's
  and the pending content stamp is cleared, as today).

In the same transaction the applier then runs the reconcile of D4 and, last, the **arrival rule** for each `NewPin` or `PinEdit` request
the decision itself carried (one pure Core function, `SyncPin.arrival`): no item (the reconcile may just have removed one), or an
`approved` or `declined` one: write `open` with the D1 hash; an `open` or `unseen` one with the same hash: leave it; with a different
hash: `unseen` with the new hash. `decide` raises the request only for a genuinely new pin (a newer sticky stamp) or a changed pinned
text (FC2); after the carried-out decision the base equals the record, so a repeated pull classifies the group `Unchanged` and raises
nothing: idempotent.

### D4. Reconciling an item with the pull (the transitions)

`decide` cannot see items, so the applier reconciles them after the decision is carried out, in the entry's transaction, with one pure
Core function `SyncPin.reconcile`. Its inputs are the **post-decision** state: the item (kind, state, hash), the stored entry (absent or
its sticky flag, its content-group hash `SyncScan.contentHash`, its shown hash), the row (active or forgotten/left; has a base; the pending
content stamp; `scanned_content_hash`), the post-decision base's content group and its shown hash, and the record (absent, or its sticky
flag). Every id with a pin item in state `open`, `unseen`, `declined` or `approved` is **added to the pull's visit set** (as held-delete ids
are), so a local change that no record change accompanies still reaches it. The rules are **first match, in this order, per kind**:

`new-pin`:

1. the entry is absent or the row is forgotten or left: remove the item;
2. the entry is sticky (pinned here, by the person or by an approval): remove the item;
3. the record is absent or not sticky (a remote unpin or removal; the apply already applied it): remove the item;
4. state `open` or `unseen` and the entry's shown hash differs from the item's (the record's text arrived, or the person edited the entry):
   `unseen` with the new hash;
5. otherwise leave the item as it is (a `declined` item stays declined, a text change does not re-ask; only a new pin does, by the arrival rule).

`pin-edit`:

1. the entry is absent, or the row is forgotten, left, or has no base: remove the item;
2. the entry's content-group hash equals the base's (nothing is held any more: the apply took the record's content, the held text came to
   equal the pin text, or an earlier answer was carried out): remove the item;
3. the entry was edited locally since the item arrived (the row has a pending content stamp, or the entry's content-group hash differs from
   `scanned_content_hash`): remove the item; the local edit stands and meets the record as an ordinary pending change (this is also what
   removes a declined item, whose fresh stamp is pending until pushed);
4. the entry is not sticky (the person unpinned it, or a remote unpin was applied): **apply the base's content group to the entry**
   (D5) and remove the item;
5. state `open` or `unseen` and the base's shown hash differs from the item's (a newer remote edit was held): `unseen` with the new hash;
6. otherwise leave the item as it is (a record that is absent while the entry stays pinned is a held deletion; the item is unchanged).

An `approved` item is removed by the same rules once its effect is in place (new-pin rule 2, pin-edit rule 2). The no-op shortcut of the
apply counts every item write, update and removal as touching the item (as D10 of the held-deletion part did). The reconcile reads the stored
entry after the apply's own store operations, so a decision that already unpinned and took the record's content (FS3/FS6 with FC3/FC5)
reaches pin-edit rule 2, not rule 4.

### D5. A local unpin applies the held edit

Owner decision. When the person unpins a memory whose remote edit is held, the item closes **and the held edit is applied**, so the memory
does not silently stay on different content from every other machine: the unpinned entry is ordinary and the record's edit arrives like any
other remote edit (the same holds when the unpin came from the repository and the held edit was older than that unpin's pull). Inside the
entry's transaction the applier writes to the entry **exactly what `TakeTheirs` writes** for the base's content group (text, project or
clear, scope, entry type, source or clear, `created_at`), computes the vector from the base's text as it does for a `TakeTheirs`, applies the
tier rule `TakeTheirs` applies, and records the row's comparison columns from the stored entry (as every apply write does). No content stamp
is issued (the content equals the record's), so nothing is pushed for it; the pending unpin push, if any, is unaffected. No pin cap applies: the entry is no
longer a pin. A person who unpins to get rid of a held edit should decline it first.

### D6. `approve`

`ReviewService.ApproveAsync(id, kind, shownHash)` for a `new-pin` or `pin-edit` item of `open` or `unseen` state, in the service's usual
frame (sync lock, migrations, one immediate transaction that re-reads the item, row and entry). Checks, in this order, each a refusal that
changes nothing except as stated for `stale-item`: the item exists (`no-pin-item`); the row is `active` with a base and the entry is read
(`failed`, `unreadable-entry`); the shown hash is present (`shown-hash-required`); the entry's state fits the kind (a `new-pin` needs an entry
that is not sticky, a `pin-edit` one that is: otherwise `stale-item`, the next pull closes the item); for a `pin-edit`, the entry was not
edited locally (the pending content stamp, or `SyncScan.contentHash(entry)` against `scanned_content_hash`: `edited-locally`, the
`SyncApply.editedSinceHold` pattern), because approving would overwrite the person's newer pinned text; the shown hash equals the item's
live hash recomputed by D1 (a difference is **stale**: the answer is `stale-item` with what the item shows now, and the service commits the
item's update to that hash and `unseen`, the only write of a refusal, so the frame's commit-on-success rule has this one exception; a test
proves nothing else is written); the text to pin has no character the list would remove (`unshowable-text`: the person cannot approve what
the list cannot show) and is within the **pinned cap** (below).

- **`new-pin`:** applies the pin path inside the transaction: the entry moves to hot when it is not there (`Move`, which copies the vector),
  the sticky flag is set, `decay_score` is set to 1.0 as `memory_pin` does, and the row is written with `local_sticky` true, `local_tier`
  hot, the pending sticky stamp NULL and every other column as it was, so the approval is not a local change to push (the base already holds
  the record's pin and its stamp). The item becomes `approved`. Nothing is pushed.
- **`pin-edit`:** the base's content group is applied to the pinned entry exactly as D5 does (`TakeTheirs`' fields, a new vector), the row's
  scanned content hash is recomputed from the stored entry, the pending content stamp stays NULL, and the item becomes `approved`. The entry stays pinned.
  Nothing is pushed (the base already holds the record's content).
- **The cap.** The text that becomes the pin (the entry's, or the held text) is checked against `tiers.pinned.max_content_chars` (a value of 0
  or less means the default 500, as the list does), the limit the shown text is cut at; over it the answer is refused `over-pin-cap` with the
  cap and the length in the message (the wording is `PinnedTierLimits`') and the item stays open.
- After an approval `local_sticky` equals the entry, so the next scan stamps nothing.

### D7. `decline`

`ReviewService.DeclineAsync(id, kind, shownHash)`: the same frame and the same checks as `approve` except the cap and the unshowable-text
checks (the hash is required for both kinds, so a decline never answers something the person has not seen).

- **`new-pin`:** the entry stays an ordinary local entry; the item becomes `declined`; the record is left alone (the pin remains on other
  machines); no stamp, no push.
- **`pin-edit`:** the person chose the local text. The row gets a fresh content stamp above the base's content stamp (`IssueStamp` with the
  base's stamp as the floor; the base's content group is the record's), left **pending**, with the comparison columns as they are; the writer
  then pushes the local content group as an ordinary content change (the push's own secret re-check applies). The item becomes `declined`
  (the reconcile removes it, D4 pin-edit rule 3). The answer says "this replaces that change in the repository and on your other machines
  where the memory is not pinned; where it is pinned it is held there as an edit".

### D8. Surfacing: the list and the commands

`ReviewItem` becomes `(Kind, EntryId, State, Direction (nullable), Cause (nullable), ShownHash, ArrivedAt, Machine, Project, Scope, Text,
TextCut, TextSource, CurrentText, CurrentTextCut)`: `Kind` is `held-delete`, `new-pin` or `pin-edit`; the held-delete members are unchanged;
`Machine` and `CurrentText` are NULL where they do not apply. `List()` also returns the open and unseen pin items. A pin item shows, by the
same text rules as a held deletion (delimited, `| `-prefixed, capped at `tiers.pinned.max_content_chars`, control, line-separator and bidi
characters removed): for a `new-pin` the entry's text with the source `local entry`; for a `pin-edit` the held text with the source
`repository record` and, beside it, `CurrentText` (the text pinned here) with the source `local entry`; both: project, scope, the shown hash,
the record's machine and when it arrived, and the state (`open` or `unseen`). The hidden commands, in `ReviewCommand`:

- `sync review approve <id> --shown <hash>` and `sync review decline <id> --shown <hash>`: **`--shown` is required** for both (a usage error, exit
  2, without it). It is 8 or more hex digits, a unique prefix of the item's hash, as for `accept`/`restore`. The held-deletion verbs keep Part 2's
  rule (the CLI reads the hash for a person); a pin is an instruction the assistant will follow, so the person must pass what the list showed. The CLI
  resolves the item by `(id, kind)`: `accept`/`restore` look up held deletions, `approve`/`decline` the pin items; an id with two pin items (which a
  pull removes) is refused with "run a sync first". `decline` of a `pin-edit` prints the revert warning before it runs.
- `list` prints the kind in each header; a direction is printed only for held deletions. The bulk forms (`accept-deletions`, `restore-deletions`)
  answer held deletions only (they filter by kind); there is no bulk form for pins.
- An id can carry a held deletion and a pin item at once (a pinned entry whose record was removed, with a held edit); each verb pair answers its own
  kind and refuses `no-held-item`/`no-pin-item` for the other.

### D9. The two small Part 2 corrections

1. `RestoreLocal` writes the `new-pin` item's `record_hash` as the shown hash of the entry it re-inserts (D1) and the record's `WrittenBy` as
   `machine`; its tests change accordingly.
2. The stale text of a held-delete answer is unchanged. `ReviewService`'s item-reading members (`CheckItem`) become kind-aware helpers shared by
   the pin answers; the held-deletion behaviour and its tests stay as they are.

## Tests (what proves it)

Core (`Sync` tests, pure): `SyncPin.shownHash` (a fixed known answer computed in the test from the stated layout; `None` vs empty; field
boundaries `"a","bc"` vs `"ab","c"`), `SyncPin.arrival` (every state/hash cell), `SyncPin.reconcile` (every row of the D4 table), and the
`SyncApply.postponement` cases of the existing tests (`NewPin` and `PinEdit` no longer postpone; `Conflict`, alone or with `NewPin`, still
does).

Infrastructure (`GitSync`): `GitSyncApplier*` and a new `GitSyncApplierPinReviewTests`, `ReviewServicePinTests`, and cycle and two-machine
proofs, one test per row:

- a pin from A: on B an ordinary hot entry (`sticky` false), row `local_sticky` false, base holds the record, a `new-pin` item `open` with the
  shown hash and the record's machine, the pinned block does not list it, the pull is complete (L advances); an insert over the hot cap goes
  warm and `approve` moves it to hot (the test sets `tiers.hot.max_content_chars` below the pinned cap, since with the defaults text over the hot
  cap is also over the pinned cap); W3, W8.1 and Part 2's restore inserts of a sticky record all end non-sticky with an item;
- `approve` of a `new-pin`: the entry is sticky, hot, in the pinned block, `local_sticky` true, no pending stamp, item `approved`, and a
  following cycle pushes nothing; refused over the pinned cap with the cap in the message and the item `open`; a stale hash (the record's text
  or the entry's text changed since shown) is `stale-item`, the item `unseen` with the new hash and nothing else written; text with characters the
  list strips is `unshowable-text`; a cap of 0 or less uses 500; `decay_score` is 1.0 after the approval; no hash is `shown-hash-required`; an entry
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
  blob id and is corrected, D9). A tag, tier or stamp change does not make an answer stale.
- **A local unpin applies the held edit** (owner decision, D5). The umbrella's table says "the item is closed and the local state stands",
  which leaves this machine on content no other machine has.
- **Every closing and updating transition is done by the pull's reconcile, not the scan.** The umbrella lists events; the scan stays
  unchanged (it keeps pin items on reactivation as its tests pin), and the pull visits every pin-item id. The transitions are an ordered rule
  list per kind (D4), which adds the rules the umbrella table does not state: a held edit that came to equal the pin text, a row with no base,
  and the local edit that closes a held edit.
- **A `new-pin` item is keyed on the stored entry's shown fields, not on the record** (parent Shared decision 3 speaks of "the record"): what the
  person would pin is the entry, and the entry is what an unpushed local edit changes.
- **An insert over the hot cap goes warm** (the parent says an "ordinary hot entry"): this is the existing apply rule (`demotesToWarm`), which
  this part does not change; `approve` moves the entry to hot.
- **`approved` and `declined` items stay until a new arrival or a removal rule.** The umbrella names the states but not their later lifecycle.
- **The CLI requires `--shown` for `approve` and `decline`**, whereas Part 2's `accept`/`restore` let the CLI read the hash for a person; a
  pin is an instruction the assistant will follow, so the person passes what the list showed. The service requires the hash in every case.
- **Both answers require the shown hash at the service**, a `decline` included.
- **A stale refusal commits the item's update** (to the live hash and `unseen`), the one exception to the service frame's rule that a refused
  answer writes nothing.
- **New refusal names** `no-pin-item`, `over-pin-cap` and `unshowable-text` in `Core.SyncSetup`.

## Deviation from the parent design

The departures above that change the parent's wording are the deviations from the parent spec: the pinned cap (the parent's Part 3 says "the pin cap"
without naming which: this names it), the local-unpin rule, the item keyed on the stored entry, the over-the-cap insert going warm, the required
`--shown`, the stale-refusal commit and the reconcile-in-the-pull rule. Shared decisions 1 to 9 are otherwise followed unchanged: one item store keyed
`(entry_id, kind)` and one service (1), answers per item by id (2), stale answers (3), non-sticky inserts (4), text from the repository is data (5),
the verbs hidden and the cycle without a caller (6), conflicts still postponed (7), nothing deleted that the rules do not prove (8), tests as the
verification (9).
