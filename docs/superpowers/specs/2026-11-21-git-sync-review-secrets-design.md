# Git sync, milestone 1b-iii: review and secrets (parent spec)

Date: 2026-11-21. Status: draft for review. Part of #93 (git sync). Milestones 0a to 1b-ii are merged (the scan, the pull, the
push, the cycle and deletions). The umbrella design is `2026-11-15-git-sync-design.md` (the sections "Pin review", "Conflict
review", "Deletions: a missing file is never proof", "Legacy alias resolution", "Failure handling", "Setup flow and the setup
skill" and "Delivery plan"); the deletion milestone's contract for what it leaves behind is `2026-11-19-git-sync-deletions-design.md`
and the "What 1b-ii leaves for 1b-iii to rely on" list in `2026-11-15-git-sync-acceptance-criteria.md`. Where this document is
more precise it wins for this milestone; every departure is listed under "Departures from the earlier designs".

This is a **parent spec**: the milestone is too large for one ticket (the umbrella estimated 3 to 4k lines; the smaller
milestone before it came out at about 6.5k). It is delivered as a docs-only pull request that merges this spec; one sub-ticket
per part is then created, and each part is defined and delivered on its own, in dependency order.

## Goal

Finish the cycle core, so that the sync cycle can be run, and run safely, by a person. After this milestone:

- a held deletion can be answered (`accept` or `restore`), per item, and nothing is ever bulk-approved by an agent;
- a pin that arrives from the repository never takes effect until the user approves it, and an edit that arrives for a memory
  pinned here is held until the user decides;
- a conflicting text edit is applied by the clock at once and its loser is kept for the user to keep, swap or merge;
- a token or a private key never leaves the machine in a record, and the user can release one entry on purpose;
- a legacy entry imported on two machines before sync is never inserted twice;
- the verbs `sync now`, `sync review`, `sync unblock` and `sync status` work, the `status` tool and `sync status` report a
  `sync` block, the verbs are advertised, and the cycle has its first caller.

Success: two machines, a local bare repository and temporary databases are enough to show every rule of the umbrella's "Pin
review", "Conflict review", "Deletions" (answers) and "Failure handling" (secret, newer format, corrupt clone) rows; nothing is
deleted, pinned, overwritten or published that a rule says needs the user or proof; and the verbs are visible in help only when
the last part merges.

## Non-goals

Left to later milestones and **not** built here:

- The scheduler and any automatic run of the cycle, `session_start` and `session_end` integration, the arrivals text and hints,
  the `sync_review` **MCP tool** and the startup-instruction wording that tells an agent how to use it (milestone 2). This
  milestone builds the engine service and the CLI; the MCP tool is a thin later layer over the same service.
- MCP progress notifications and the rate and ETA projection (milestone 3).
- `gh` repository creation, `sync doctor`, `--json` on every command and the full exit-code table, the `sync-setup` skill and
  `docs/git-sync.md` (milestone 4). The verbs print human text here; the exit codes this milestone needs are listed in Part 6.
- The legacy alias **pass** (the background convergence, the withhold rule, the `converged`/`resolved` steps) (milestone 5).
  This milestone detects aliases and skips the insert; it does not converge them.
- Any change to the record format, to `SyncMerge.decide`'s rules (W1 to W14 and the field rules) or to the migration (a new
  column or table is a deviation and must be listed in the part's own spec).
- Purging history, encryption, cross-machine duplicates of text typed separately (umbrella follow-ups).

## Shared decisions

Constraints every part follows. The owner decided them in the umbrella and they are not reopened.

1. **One item store, keyed `(entry_id, kind)`.** `git_sync_review` rows, kinds `new-pin`, `pin-edit`, `conflict`,
   `held-delete`, with `record_hash`, `direction`, `payload_json`, `machine` and `arrived_at` as 1b-ii left them. A `held-delete`
   item is one row per entry, `state = 'open'`, with the contract of the draft-criteria file (cause in `payload_json`, `remote`
   or `local` direction, `record_hash` the blob id for a `local` hold and NULL for a `remote` hold, the text for a restore
   from the row's `base_json`). An answer is made through **one engine service** (created by Part 2 and extended by Parts 3
   and 4) that the CLI calls and the later MCP tool will call; no verb implements an answer on its own. An answer runs
   under the existing sync lock and re-reads the item and its entry inside one immediate transaction, so it cannot race a
   cycle in another process. Each of Parts 2 to 4 adds its own kind to `sync review` list.
2. **Answers are per item, by id.** There is no "all" in the service API. The only bulk forms are the CLI-only
   `accept-deletions --all` and `restore-deletions --all` (Part 2), which refuse unless standard input and output are an
   interactive terminal, so an agent's shell cannot run them unattended.
3. **An answer that carries a `record_hash` is refused when the record has changed since it was shown.** The answer is not
   applied and the item is updated to what the record says now. A `new-pin`, `pin-edit` or `conflict` item is then marked
   `unseen`, and every reader of review items treats `unseen` as open (it is shown again, it still blocks what an open item
   blocks). A `held-delete` item **never becomes `unseen`**: it stays `open`, with its `record_hash` refreshed, so a refused
   answer can never release the hold on a deletion (the push exclusion and the decision input read only open items).
4. **Every path that inserts or re-inserts an entry from a record yields a non-sticky entry.** A pull's insert, a `restore`,
   an edit that beats a deletion and (later) the alias pass all do. A record whose sticky flag is true opens a `new-pin` item;
   a pin is never carried out without the user's approval. Pending, held and declined states are not local actions: the scan
   compares against `local_sticky`, which a pending, held or declined review never changes (an approval sets it to true, with the record's own sticky stamp; every insert or re-insert path, `restore` included, records the inserted non-sticky state as `local_sticky = false`, as the applier's comparison columns already do), and the writer patches the record without touching groups the
   user did not change, so a pending or declined review can never push an unpin or revert a remote edit.
5. **Text that came from the repository is data.** What a review item shows is delimited and capped at the pin limit
   (`tiers.pinned.max_content_chars`), and nothing in a record is ever treated as an instruction.
6. **The cycle has no caller, and no sync verb is advertised, until Part 6.** The `sync` commands stay hidden
   (`SyncCommandBase.Hidden`) and `GitSyncCycleCallerTests` keeps passing unchanged until Part 6, which is the only part that
   adds a caller of the cycle (`sync now`), unhides the verbs and updates that test. Parts 1 to 5 are exercised by automated
   tests that drive the scanner, applier, pusher and the review service directly, as 1b-i and 1b-ii did. **The secret block
   (Part 1) must merge before the cycle has any caller**, because the push has no secret scan today.
7. **The pull still postpones what a later part has not built.** A decision that opens a review request of a kind whose part
   has not merged is postponed exactly as today (records nothing, `L` held back, reported). Part 3 lifts it for `new-pin` and
   `pin-edit` and Part 4 for `conflict`; each part removes its kind from the postponement and leaves the others.
8. **Nothing here deletes anything the rules do not prove.** The delete guard, the proof rule and the held-item lifecycle of
   1b-ii stand; this milestone answers items, it does not re-decide them.
9. **Tests are the verification.** Every criterion is checked by automated tests against local bare repositories and temporary
   databases (`TwoMachineRig` and the existing rigs), and each part adds its draft criteria to
   `2026-11-15-git-sync-acceptance-criteria.md` under its own heading and updates the contributor guides it touches.

## Breakdown

### Part 1: Secret block and `sync unblock`
Scope: the scan stamps `blocked_hash` and `blocked_reason` (`secret-detected`, which replaces any reason the writer set, is never cleared by the writer, and is lifted only by a clean re-scan or `sync unblock`) on an entry whose text, tags, project or scope (every free-text field the record publishes) hold a token or a private key (the `SensitiveContentScanner` token and private-key detectors only; an email address is ordinary text, so `git@github.com` is not blocked), and re-evaluates it when any of those fields changes (the hash that keys the block and the release covers the tags as well as the content group, which hash separately today). The push keeps excluding an entry blocked for a reason it did not set itself, and reports the secret reason by name, while the entry still takes part in the apply. `allowed_hash` lets a user release the current content; any change to those fields is evaluated again. The engine operation behind `sync unblock <id>` and the hidden command itself, which records the entry's current content hash as allowed. The list of blocked entries is available to `status` (the status block itself is Part 6).
Depends on: none
Done when: a token or key blocks only its own entry, which is listed and not pushed while every other entry pushes; `sync unblock <id>` releases it and a later edit is checked again; `git@github.com` text is never blocked; a token added in a tag after an unblock is blocked again; the cycle's tests still pass and nothing calls the cycle.

### Part 2: Answering held deletions
Scope: the review service and its first answers, `accept` and `restore`, in both directions of a `held-delete` item (`accept` deletes the entry here or removes its record; `restore` of a remote hold keeps the entry and re-pushes it as a new record with the base cleared, and of a local hold re-inserts the entry from the row's `base_json`, non-sticky, opening a `new-pin` item when the record is sticky), `sync review` list, accept and restore, and the CLI-only `accept-deletions --all` and `restore-deletions --all` with the interactive-terminal refusal. An accepted deletion bypasses the guard; answering some items leaves the rest held and does not let the others fall under the guard. Also the parked 1b-ii finding (recorded in the final review of PR #157 and in the project memory): the unproven fallback hold replaces an open held item of the other direction instead of skipping it, and the contributor guide line that says it never replaces one is corrected. A stale answer to a held deletion is refused and leaves the item open (Shared decision 3): a local hold compares the shown hash with the record's blob id at `L` in the clone, and a remote hold has no stale check.
Depends on: none
Done when: `accept` and `restore` work for both directions and every cause (`guard`, `unproven`, `sticky`); `restore` never yields a sticky entry; answering some items leaves the rest open; bulk forms refuse without a terminal; a restore of a local hold whose entry was pinned here pushes no unpin; an accepted deletion is not stopped by the guard; a stale answer to a held local deletion is refused and the item stays open; the parked fallback-hold finding is fixed and tested.

### Part 3: Pin review
Scope: the pull opens `new-pin` and `pin-edit` items instead of postponing them (the umbrella's two cases and the transition table), the entry is inserted or kept as an ordinary hot entry, `approve` (with the shown `record_hash`, through the pin path and the pin cap, without a push) and `decline` (a `new-pin` stays an ordinary entry; a `pin-edit` re-asserts the local text with a fresh content stamp and pushes it) in the review service and in `sync review approve|decline`, and the transitions, which also cover the `new-pin` items Part 2's restore leaves (a record that changes while an item is pending updates it and marks it unseen; a remote unpin applies; the item is removed when the entry is deleted or the sticky flag goes away; a local pin or edit closes the item).
Depends on: Part 2
Done when: a pin from A is an ordinary hot entry on B and absent from the pinned block until approved; approval with the shown hash pins without a push; a stale hash is refused; decline does not unpin A; a remote unpin applies; a remote edit to a memory pinned on B is held, decline re-asserts B's text and a tag edit while held does not push the old text; every insert from a sticky record yields a non-sticky entry and a `new-pin` item.

### Part 4: Conflict review
Scope: the pull applies the clock's winner at once and files the loser's text as a `conflict` item (both versions, both machines, both stamps), a second conflict on one entry appends its losing text without overwriting an earlier one, and `keep`, `swap` and `merge` (with the merged `text`) in the review service and in `sync review keep|swap|merge`; a `swap` or `merge` is an ordinary edit with a fresh stamp so it wins everywhere at the next sync. The survival of `conflict` items across `sync init` and `sync join` already exists and stays under test.
Depends on: Part 2
Done when: two text edits leave the later one on both machines and the loser in `sync review`; `keep`, `swap` and `merge` each leave the stated text on both machines; a second conflict on one entry keeps both losing texts; an unanswered conflict costs nothing and blocks nothing.

### Part 5: Alias detection and skip-insert
Scope: in the pull, a record whose `origin.hash` maps in the local `import_log` to a different, existing local id adds a `git_sync_alias` row (canonical id = the lowest id) and the insert is skipped, so no duplicate appears; aliases are detected before the deletion checks, so a catch-up that holds both the canonical add and the removal of the other record is read correctly; a machine that holds neither id treats the disappearance of the non-canonical record under an open alias as convergence (it replaces its copy with the canonical record in one transaction, carrying over tier, tags and sticky; when the content (text, project or scope) differs, the umbrella's rule: the canonical text is applied and the local text is filed as a `conflict` item, or, when the local entry is pinned, the canonical text is held as a `pin-edit` and the local pin text stays active; a canonical record that is pinned goes to pin approval) and never reads it as a deletion. A skipped insert does not hold `L` back and is decided the same way on every pull. No pass, no withhold rule and no background task (milestone 5).
Depends on: Part 3, Part 4
Done when: pulling a record whose origin hash matches a different local legacy import creates no duplicate; a third machine whose non-canonical record disappears ends with the canonical entry, tier, tags and pin carried, and a differing local text is kept in a conflict item (or a held pin edit under a local pin) rather than dropped; an open alias is never read as a user deletion.

### Part 6: `sync now`, the `sync` status block and advertising the verbs
Scope: `sync now` (one full cycle in the foreground, printing what changed, with the exit code for `deletions-held`), the `sync` block in the `status` tool and in `sync status` (remote, branch, last success, last error, blocked entries, rejected records, ineligible ids, apply failures, pins awaiting review, conflicts, held deletions, clock warnings), reporting the existing stop on a newer repository format and the existing sideline of a corrupt clone through `sync now` and `status` (the behaviour and its tests exist already and are kept), un-hiding the `sync` verbs, updating `GitSyncCycleCallerTests` to allow exactly the `sync now` caller, and the help text and contributor guides that name the verbs (`docs/git-sync.md` is milestone 4).
Depends on: Part 1, Part 2, Part 3, Part 4, Part 5
Done when: `status` and `sync status` show the `sync` block; `sync now` runs a cycle and reports it; a newer manifest format stops sync, is reported by `sync now` and writes nothing; a corrupt clone is sidelined and re-cloned once per process with nothing deleted; the sync verbs appear in help; `GitSyncCycleCallerTests` allows only the `sync now` caller; the existing checks, the shim tests, the catalog check and the release build pass with no new warning.

## Departures from the earlier designs

- **`sync_review` MCP tool is milestone 2's.** The umbrella's milestone table puts the tool in milestone 2 and the criteria
  draft here lists only the engine behaviours; this milestone builds the shared service and the CLI.
- **The secret block comes first, and before any caller.** The umbrella lists it inside one milestone; here it is its own part
  and gates Part 6, because the cycle's push has no secret scan until it merges.
- **`restore` of a sticky record opens a `new-pin` item in Part 2, and answering it is Part 3.** The item shape is created
  with the service; its answer arrives with pin review. A restored entry is non-sticky meanwhile.
- **The secret block's key covers tags, project and scope.** The umbrella's Failure handling row keys it on the content hash alone, which leaves the tags out and would let a token added in a tag publish after an unblock.
- **Alias work here stops at detection and skip-insert plus the convergence rule**, as the umbrella's delivery table already
  says; the withhold rule and the pass are milestone 5.
