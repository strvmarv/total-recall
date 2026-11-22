# Git sync, milestone 1b-iii part 1: secret block and `sync unblock`

Parent: #159, Part 1 (docs/superpowers/specs/2026-11-21-git-sync-review-secrets-design.md)

Date: 2026-11-21. Status: draft for review. Part of #93 (git sync); sub-ticket #161 of the parent #159. The parent spec
(`2026-11-21-git-sync-review-secrets-design.md`) governs: this document is the engineering design of its Part 1 and follows its
"Shared decisions". The umbrella design is `2026-11-15-git-sync-design.md` (the "Failure handling" row "A secret detected before
writing", "Setup flow and the setup skill", "Observability"); the push this part changes is described in
`2026-11-17-git-sync-push-design.md` and the scan in `2026-11-17-git-sync-scan-design.md`. Where this document is more
precise it wins for this part; every departure is listed under "Departures".

## Goal

A token or a private key in a memory never leaves the machine inside a sync record, only that memory is held back, the user
can see which memories are held back, and the user can release one on purpose. The block is **per memory**: every other memory
still syncs. It holds on every path that produces the text that would be written (a local edit, a pulled change merged into the
local entry, and the answer paths of later parts), not only on the path the scan happens to see.

Success: with two machines, a local bare repository and temporary databases, a memory whose text, tags, project or scope holds
a token or key is never written to the repository by any push, is listed, and is released by `sync unblock <id>` until any of
those fields changes; `git@github.com` text is never blocked; the existing checks pass and nothing calls the cycle.

## Non-goals

- Any caller of the cycle, `sync now`, the full `sync` status block, `session_start` hints, the `sync_review` MCP tool: later
  parts and milestones. Shared decision 6 of the parent spec stands: the verbs stay hidden and `GitSyncCycleCallerTests` is
  unchanged.
- Bearer headers, `password=` assignments, URL credentials and JWTs. The detector stays "tokens and private keys" (the umbrella
  names exactly those two detectors); only the token shapes it recognises widen (D9).
- Redacting or editing a memory's text. A blocked memory is held back from the push and nothing else changes: it is still
  retrieved, still takes part in the pull and apply, and is never altered.
- A new column or a migration (the existing `blocked_hash`, `blocked_reason` and `allowed_hash` are enough).

## Design

### D1. The secret key

`SyncSecret.secretKey` (new pure module `SyncSecret.fs` in `TotalRecall.Core`, after `SyncScan.fs`) is the SHA-256, lowercase
hex, of `total-recall/scan/secret/v1\n` and then the free-text fields a record publishes, in this order: text, project, scope
(each encoded as `contentHash` encodes an optional string) and the normalised tags (encoded exactly as `tagsHash` encodes them:
count, then each tag). Entry type, source tool and `created_at` are not free text and are left out. It is **one** key for the
four fields, so a change to any of them changes the key, and `blocked_hash` and `allowed_hash` of a `secret-detected` row hold
this key (not `contentHash`, which leaves the tags out and which the writer's own marks keep using for their reasons).

### D2. What is scanned

The C# side runs `SensitiveContentScanner.ScanSecrets` (Token and PrivateKey detectors only) over the text, the project, the
scope and each tag separately; the entry is a **hit** when any returns a reason. The reasons are category labels
(`API token or secret`, `private key block`) and never the matched value; nothing in this part stores, logs or prints a value.

### D3. The decision (pure, in `SyncSecret`)

Inputs: the row's `blocked_reason`, `blocked_hash`, `allowed_hash`, and the current key. A first function says whether a scan is
needed, a second turns the scan result into the mark change:

| # | Condition | Scan? | Outcome |
|---|---|---|---|
| 1 | `allowed_hash` equals the key | no | **Allow**: clear a `secret-detected` mark if the row has one; never touch a writer's mark |
| 2 | reason is `secret-detected` and `blocked_hash` equals the key | no | **Keep** the block (the text is unchanged since it was found) |
| 3 | anything else, hit | yes | **Block**: reason `secret-detected`, `blocked_hash` = the key; replaces any writer reason |
| 4 | anything else, no hit, reason `secret-detected` | yes | **Clear** the block (a clean re-scan lifts it) |
| 5 | anything else, no hit, any other reason or none | yes | **Keep** (a writer's mark is the writer's) |

`allowed_hash` is never cleared by this part except by `FreshRow` (a new or reactivated row starts with all three columns
empty, as today); a stale allowed key is harmless because a changed field changes the key.

### D4. The scan stamps it

After the scan builds its plan, a pass evaluates D3 for **every eligible active row's entry** on the row the plan will write
(fresh, updated or unchanged), so an entry the plan otherwise calls `NoChange` is still checked when its key matches neither
`blocked_hash` nor `allowed_hash` (the parent spec's rule: a text written by an answer path is checked before it can be
pushed). One id gives one row write (the pass edits the row the plan already holds, it does not add a second write).
Ineligible, skipped (two tiers, unreadable) and `forgotten`/`left` rows without an entry are not evaluated. The scan result
(`ScanSummary`) gains `Blocked` and `Unblocked` (the ids whose block was set, and the ids whose block a clean re-scan lifted);
the positional constructor and `ScanSummary.Empty` change and their test call sites follow.

### D5. The push checks it too (defence in depth)

The scan runs before the apply, and the apply can write text or tags the scan never saw (a merged tag union today; a restore,
`swap` or `merge` in later parts). So `GitSyncPusher.PlanOne`, right after it has re-read the entry and agreed it with its row,
runs D3 on the entry it just read, inside the plan transaction:

- **Allow** or **Keep with no hit**: the entry goes on as today.
- **Block**: the row is written with the secret mark in the same transaction, the id is listed in `PushSummary.Blocked` with the
  reason `secret-detected`, and the entry is not written. No record, patch or write-back is built for it.

An entry the **preparation** already excludes because its row carries a `secret-detected` mark is reported in `Skipped` with the
reason `secret-detected` (today every non-writer reason reads `blocked-elsewhere`); other non-writer reasons keep that name.
Because the push re-checks every entry it is about to write, no record with a token or key is ever produced by the pusher, whatever
produced the text.

### D6. The writer never clears it

`secret-detected` is not a writer reason, so `SyncPush.markAction` already keeps it and the finalize's own-mark test already
leaves it. A test pins both. When a clean re-scan lifts a secret mark from a row whose text still violates a writer limit, the
row has no reason and the next push marks the writer's reason again (the existing rule); a test pins that order.

### D7. `sync unblock <id>`

A hidden command (`SyncCommandBase.Hidden`) with one argument, the entry id, backed by a new partial of `SyncSetupService`
(`SyncSetupService.Unblock.cs`, same environment, sync lock and refusal mechanism as `leave`). Under the sync lock, in one immediate
transaction:

1. Read the row. No row, or a row whose reason is not `secret-detected`: refuse ("not blocked by a secret"), exit 1, nothing written.
2. Read the entry (`LocalEntryReader.Read`). Absent: refuse, nothing written.
3. Compute the current key and run the detectors. Write the row with `allowed_hash` = the current key and `blocked_reason` and
   `blocked_hash` cleared. Print `unblocked <id>` and the categories found in the **current** content, or say that the current
   content holds no secret and the block was cleared.

It allows what is in the entry **now**, even when the text changed after the block was set (the user's explicit act, and the
categories printed tell them what they released). A later change to any of the four fields changes the key, so the entry is
checked again. The lock is held for the whole operation, so a cycle cannot interleave; the command does not name the cycle or the
pusher (`GitSyncCycleCallerTests`). New refusal codes are added to `SyncSetup` (`not-blocked`, `unknown-entry`); exit codes follow
the other sync commands (0, 1 refused, 2 usage).

### D8. `sync status` lists blocked entries

`sync status` (read-only, no network, no lock) gains a `blocked` section: each blocked entry's id and reason, at most 20 lines
and then "and N more", from one read-only query on `git_sync_entry` (guarded for an unmigrated database and tolerant of an
unknown state value). The rest of the `sync` block in `status` is Part 6.

### D9. The token detector

`ScanSecrets` recognises the token shapes the transcript archive's `Redact` already recognises (the hyphenated `sk-ant-`,
`sk-proj-`, `sk-svcacct-`, `sk-admin-` and the other `sk-` forms with a left boundary), by sharing the pattern definitions between
the two instead of copying them. Detection stays tokens and private keys only. Every pattern is matched with a timeout (the same
bound `Redact` uses); a timeout counts as a **hit** (fail closed: the entry is blocked) with the category `scan timed out`. The
stale comment on the detector array ("runs only on eval_grow accept, over short strings") is corrected. `eval_grow`'s accept
guard uses the same detector and so blocks more, which is the safe direction.

### D10. Cost and scope of the check

No new marker is stored: every cycle runs the detectors on the entries whose key matches neither stored key, which is linear in
the text (memory text is capped at the record size limit) and cheap at the sizes involved. An entry that is blocked and unchanged,
or allowed and unchanged, is skipped. The cost bound is stated in the contributor guide.

## Tests (what proves it)

Core: `SyncSecret` key (each of the four fields changes it; entry type, source and `created_at` do not; tag order does not), and
the D3 table, row by row, as table tests. Infrastructure: the scanner pass (blocks a hit; keeps a blocked
unchanged entry without scanning, observed through a counting detector seam; lifts on a clean edit; blocks again after an edit
that adds a token **in a tag**; replaces a writer reason; checks an entry the plan calls `NoChange`; one write per id; a
reactivated row starts clean); the pusher (a hit is not written and is listed by name; an allowed entry is written; an entry
whose text the apply changed to hold a token is blocked at the push; a blocked entry does not stop other entries; the mark
survives a finalize and a clean push of other entries; a writer reason is replaced and later re-marked); `unblock` (releases;
refuses an unblocked or unknown entry; the allowed key survives a later unchanged scan; a later edit is checked again; the
lock is taken; categories only are printed, never a value); `sync status` (lists, caps, no table, odd state); the detector
(`sk-ant-`, `sk-proj-`, existing shapes, a private key, `git@github.com` and `noreply@x.com` clean, a timeout fails closed);
`HiddenCommandTests` (the verb list and the phrase check include `unblock`); `GitSyncCycleCallerTests` unchanged and green.

## Documentation

The contributor guides (`src/TotalRecall.Infrastructure/AGENTS.md`, `src/TotalRecall.Core/AGENTS.md`, the root `AGENTS.md` rows for
the push and the hidden verbs) and the draft criteria file (a "1b-iii part 1" list under the 1b-iii heading) are updated: the
scan now writes the `blocked_*`/`allowed_hash` columns, the push re-checks, and `unblock` is a hidden verb.

## Acceptance criteria mapping (ticket #161)

Each criterion of the ticket is covered by the tests above: a token or key blocks only its own entry, which is listed and not
pushed (D4, D5, D8); `sync unblock` releases it and a later edit, including in a tag, is checked again (D1, D7); `git@github.com`
is never blocked (D2, D9); a restored, swapped or merged text holding a token is not pushed (D4, D5); the secret reason replaces
a writer reason and is never cleared by the writer (D3, D6); the cycle's tests still pass and nothing calls the cycle (D7).

## Departures from the earlier designs

- **The push re-checks (D5).** The umbrella says the scan blocks; here the pusher also runs the same rule on each entry it is about
  to write. This is the umbrella's own sentence ("before a record is written the scan runs ... over its content", History section) applied at the place the record is written; the scan still stamps and lists.
- **The block key covers tags, project and scope (D1).** The parent spec's decision (a departure it already lists).
- **`ScanSecrets` widens its token shapes and fails closed on a timeout (D9).** Not in the parent spec; it follows from the goal
  (a hyphenated `sk-ant-` key must not publish).
- **New refusal codes in `SyncSetup` (D7).**

## Deviation from the parent design

The parent spec names none for this part. The three additions above (the push-time check, the wider detector with a fail-closed
timeout, the refusal codes) are inside the part's scope and are listed for the reviewer.
