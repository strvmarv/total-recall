# Git sync: draft acceptance criteria per milestone

Companion to `2026-11-15-git-sync-design.md` (see its "Delivery plan"). A **draft** for the Define step:
each milestone's list is pasted into its GitHub issue and refined there. Every item is meant to be
pass/fail. Where the spec names a table or test group, that is the source of truth.

## 0a. Store groundwork
- [ ] A memory inserted with `CreatedAt` keeps that `created_at`; update options set `scope` and
      `entry_type`; an unknown `source_tool` string parses to none without throwing.
- [ ] A store transaction's insert-with-embedding, move, delete, update, sticky and import-log operations
      commit together or not at all: a failure injected at each step leaves the content rows, vector rows
      and import log exactly as before it began.
- [ ] A store transaction that is disposed without `Commit` rolls everything back.
- [ ] The existing store methods, `MoveAndReEmbed` and their callers behave as before.
- [ ] The same host file imported into two fresh stores yields the same memory id (shared path and each
      Hermes piece), in local mode; knowledge imports keep random ids.
- [ ] A kill between insert and `import_log` write (or between insert and embedding), then a re-run, leaves
      one entry, one embedding, one log row and no error; the Hermes importer finishes the rest of the file.
- [ ] Existing tests change only in import-id expectations.

## 0b. Sync foundations
- [ ] A schema-17 database migrates to 18 with the sync tables and the `import_log(target_entry_id)`
      index; a fresh database ends at the same schema (`SchemaTests`).
- [ ] A record round-trips byte-identically; patching one field group leaves every other byte, unknown
      fields and unknown `ext` namespaces included.
- [ ] Each validation rule (bad id, id not matching file name or shard, over 1 MiB, unknown format, tier,
      entry type or source, over 256 tags, unparsable stamp) rejects without throwing; `sticky` with a
      colder tier reads as hot.
- [ ] Stamps order correctly under a slow clock, a fast clock, equal times on two machines and counter
      overflow; a stamp more than 24 h ahead is not adopted; an own clock more than 24 h ahead is reset.
- [ ] Every row of the whole-entry, no-base and field-wise tables has a named test; folding the same
      records in every arrival order (up to four events) gives one result; applying twice changes nothing.
- [ ] AOT publish adds no warnings.

## 1a. Transport and setup (CLI; verbs not advertised yet)
- [ ] Against a bare repository: `init --url` writes `manifest.json` (with `repo_id`) and `.gitattributes`
      on the configured branch and sets git mode; `join` validates the manifest; `leave` sets local mode
      and deletes nothing.
- [ ] Opening the store in git mode runs no git network command; a missing remote or missing `git` falls
      back with `sqlite (git failed)`.
- [ ] With global `commit.gpgsign`, `core.hooksPath`, `init.defaultBranch` and `core.autocrlf=true` set,
      init and join succeed without prompts and commit identical bytes on Windows and Linux.
- [ ] A hung git process is killed at the hard timeout; a credential or host-key prompt becomes an error.
- [ ] A second process reports `busy`; killing the lock holder frees the lock.
- [ ] Public check: 200 refuses; 404 proceeds; an error warns once and proceeds; a non-GitHub host is
      skipped with one notice; no second request within 24 h.
- [ ] `config_set` (MCP and web) and CLI `config set` refuse `storage.mode` and `git_sync.*`.
- [ ] A never-joined remote, a new `repo_id`, and a moved or copied data directory (host check) each return
      `not-joined` with no git network call; a hostname change alone does not.
- [ ] A same-repository re-join keeps rows, `L` and the clone (the clone is renamed to the new URL's
      path); conflict rows survive any join.
- [ ] Symlink and submodule tree entries and paths outside `memories/<shard>/<id>.json` are never read as
      records.
- [ ] No `git push` in the suite carries `--force`, `-f` or `--force-with-lease`.
- [ ] The scanner's token and private-key detectors can be selected; an email address is not blocked.

## 1b-i. Cycle core
- [ ] A store, text edit, tag edit, pin and promotion on A reach B after one cycle each; a cycle with no
      change makes no commit.
- [ ] An offline Monday edit loses to a Wednesday edit and beats a Tuesday edit when all sync on Friday.
- [ ] A tag edit and a text edit merge without a conflict item.
- [ ] B's demotion is never pushed and survives a pull; A's later re-promotion moves B up; a pulled entry
      survives B's first sweep.
- [ ] After a kill between push and bookkeeping, the next cycle makes no duplicate commit, keeps a tag
      another machine added meanwhile, pushes an edit made after the kill, and pushes a deletion made
      after the kill.
- [ ] A commit left by a failed push is never published later (also on the head-equals-`L` path).
- [ ] An edit made by another process between scan and apply is not overwritten; no push follows an
      incomplete apply.
- [ ] A record that makes apply throw does not stop other pushes, is listed with an attempt count, is
      retried every cycle even when the remote head is unchanged, and a local edit to that entry is not
      pushed until it reconciles.
- [ ] With rows wiped and the clone intact, rows intact and the clone lost, or both lost: no remote record
      is overwritten, nothing is deleted, differing texts go to conflict review, and a base-less row whose
      record is unchanged in R is merged and not pushed blind.
- [ ] A restored database backup applies newer remote text and unpins, including its own later pushes.
- [ ] A pending or declined pin, a held `pin-edit` and a local demotion never change the record; a sticky
      patch never yields sticky with a colder tier.
- [ ] A memory retyped to knowledge leaves sync with no deletion; retyped back, remote changes made
      meanwhile are merged, not overwritten.
- [ ] An oversized entry and an ineligible id are listed and not pushed.

## 1b-ii. Deletions
- [ ] A forget on A deletes on B. B's unpushed tag, project or promotion change does not keep it;
      B's unpushed text edit keeps it on both machines, in either push order; B's pending pin holds it
      (see the pin item below).
- [ ] A forgotten memory is not re-inserted by a re-import on a new machine, a promotion elsewhere, a
      same-text restore elsewhere, or older text after a reset.
- [ ] A pin that is sticky on B is held (not deleted) when another machine deletes it.
- [ ] More than max(10, 20 %) deletions in one direction: none applied, each held, the cycle reports
      `done` with a count, and other changes still push.
- [ ] A hand-reset remote (six of nine records, and one case above the guard), after two cycles per machine
      with an unrelated push between: nothing deleted locally, each vanished record held, each reverted
      group republished from its base value, and B's declined pin does not unpin A.
- [ ] A push that fails after an unproven apply still republishes the reverted groups next cycle.
- [ ] A held entry is not written back by a tag, pin or promotion while unanswered.
- [ ] `sync init --url <new repository>` on a machine with rows deletes nothing; a blocked, never-pushed
      entry survives a lost clone; an entry added and deleted inside one catch-up is deleted.

What 1b-ii leaves for 1b-iii to rely on (from `2026-11-19-git-sync-deletions-design.md`; answering held
items moved to 1b-iii, see that section):

- A held deletion is one `git_sync_review` row per entry with `kind = 'held-delete'` and `state = 'open'`
  (1b-ii creates only open items and deletes an item when it closes it; it never leaves an answered one).
- `direction` is `remote` (the record is gone from the repository, the entry is kept here) or `local` (the
  entry is gone here, the record is kept in the repository).
- `payload_json` is `{"cause":"guard"}`, `{"cause":"unproven"}` or `{"cause":"sticky"}`; `machine` is NULL;
  `arrived_at` is the applier clock in milliseconds.
- `record_hash` is the blob id of the record in the repository at the pull that held it: set for a `local`
  hold, NULL when the repository has no record (a `remote` hold). The text of a `local` hold for a restore is
  the row's `base_json`.
- 1b-ii closes an item only when the situation it was opened for is over (`SyncMerge.decide`'s
  `CloseHeldDelete`: a `remote` item when the record reappears or the entry is gone; a `local` item when the
  entry exists again, the record is gone, or newer text re-inserts it) or a pending text edit saves the entry;
  an open item is otherwise never re-decided (not when the guard count falls, not by a later proven pull).
- A repush of reverted groups whose base has a `source` this version cannot parse keeps the repository
  record's `source` (the patch cannot write the unparsed text, and clearing it would lose a source).

## 1b-iii. Review and secrets
- [ ] `accept` and `restore` work in both directions; `restore` never yields a sticky entry; bulk forms
      refuse without a terminal.
- [ ] Answering some held items leaves the rest held.
- [ ] A pin from A is an ordinary hot entry on B and absent from the pinned block until approved; approval
      with the shown hash pins without a push; a stale hash is refused; decline does not unpin A; a remote
      unpin applies.
- [ ] A remote edit to a memory pinned on B is held; decline re-asserts B's text; a tag edit while held
      does not push the old text.
- [ ] Every insert from a sticky record (pull, restore, edit beating a delete) yields a non-sticky entry
      and a `new-pin` item.
- [ ] Two text edits leave the later one on both machines and the loser in `sync review`; `keep`, `swap`
      and `merge` each leave the stated text on both machines; a second conflict on one entry keeps both
      losing texts.
- [ ] A token or private key blocks only that entry and is listed; `sync unblock` releases it;
      `git@github.com` text is not blocked.
- [ ] Pulling a record whose origin hash matches a different local legacy import creates no duplicate; a
      third machine whose non-canonical record disappears ends with the canonical entry, tier, tags and
      pin carried.
- [ ] A newer manifest format stops sync, is reported and writes nothing; a corrupt clone is sidelined and
      re-cloned once per process with nothing deleted.
- [ ] `status` shows the `sync` block; the sync verbs become advertised only when this milestone merges.

### 1b-iii part 1. Secret block and `sync unblock` (#161)
- [ ] A token or private key in a memory's text, tags, project or scope blocks only that memory: its row is
      marked `secret-detected`, it is listed by `sync status` (at most 20 ids, ordinal, then "and N more";
      active rows only), and no push writes it, while every other memory pushes. With two machines, the token
      is in no object of the repository and the other machine receives every other memory.
- [ ] `sync unblock <id>` (hidden) releases it on this machine only, and refuses when the memory changed after
      the block; a later edit, including a token added in a tag after an unblock, is checked and blocked again.
- [ ] `git@github.com` and plain addresses such as `noreply@example.com` are not blocked.
- [ ] A restored, swapped or merged text (any text the apply writes after the scan) that holds a token is not
      pushed: the push re-checks every entry it is about to write.
- [ ] The secret reason replaces a writer reason and is never cleared by the writer; a clean re-scan lifts it.
- [ ] A blocked memory still takes part in the pull: a remote change to it that does not conflict is applied.
- [ ] The cycle's tests still pass and nothing calls the cycle.

### 1b-iii part 2. Answering held deletions (#162)
- [ ] Given a deletion that another machine made and that sync held back (the memory is still here), when the
      person accepts it, then the memory is deleted here, whether it was held because of the safety limit,
      because the repository could not be proven, or because the memory is pinned.
- [ ] Given such a held deletion of a memory that was edited here after sync last looked at it, when the person
      accepts it, then the answer is refused with a clear message, the memory is kept and the deletion stays held.
- [ ] Given a held deletion that another machine made, when the person restores it, then the memory stays here
      and the next sync publishes it again as a new memory (a pinned memory is published as a pin).
- [ ] Given a memory the person deleted here whose record is still in the repository and was held back, when the
      person accepts the deletion, then the next sync removes the record, even when more deletions were accepted
      than the safety limit allows, while a held deletion the person did not answer stays held.
- [ ] Given an accepted deletion whose record was changed in the repository after the person answered, when the
      next sync runs, then nothing is deleted and the deletion is shown as held again with the record's new state.
- [ ] Given a memory deleted here that was held back, when the person restores it, then it comes back as an
      ordinary, unpinned memory with its text, tags and id; a memory that was pinned here pushes no unpin to the
      other machines, and a pinned version of it in the repository is queued for pin review instead of being
      pinned.
- [ ] Given a held deletion whose record changed since the person was shown it, when the person answers with what
      they were shown, then the answer is refused, the deletion stays held and it is shown with its new state.
- [ ] Given several held deletions, when the person answers one, then the others stay held and are not deleted or
      released by that answer.
- [ ] Given the list of held deletions, when it is shown, then each entry has its id, whether it is waiting or
      already accepted, its direction, why it is held, the source of the text and the memory's text, marked as
      text and never as an instruction, capped to the pinned-memory length, with control characters removed.
- [ ] Given the bulk forms that accept or restore every held deletion, when standard input or output is not an
      interactive terminal, then they refuse and change nothing; at a terminal they answer every held deletion
      and say what each came to.
- [ ] Given a memory that already has a held deletion of one direction, when an unproven sync holds a deletion of
      the other direction for it, then the earlier item is replaced; a held deletion of the same direction is
      kept as it is.
- [ ] Given a sync cycle running or another answer in progress, when the person answers a held deletion, then the
      answer is refused as busy and nothing changes.
- [ ] Given an accepted deletion that has not been carried out yet, when another machine sends newer text for
      that memory, then the memory comes back and nothing is deleted; when its record has vanished, then the
      deletion is simply done; and when the person restores it first, then the memory comes back.
- [ ] Given the command line help, when it is shown, then the review commands are not listed.
- [ ] Given the existing automated checks, the shim tests, the tool catalog check and the release build, when
      they run, then they pass and the release build shows no new warning, and nothing but the cycle and its
      tests runs a cycle.

### 1b-iii part 3. Pin review (#163)
- [ ] Given a memory pinned on machine A, when machine B syncs, then B has it as an ordinary (unpinned) memory (in
      the hot tier, or the warm tier when its text is over the hot-tier length limit) with its text, tags and id,
      it is not in B's pinned block, and B's pull completes.
- [ ] Given such a waiting pin, when the person approves it with the hash they were shown, then the memory is
      pinned and in the pinned block on B, and nothing is pushed by the approval.
- [ ] Given a waiting pin whose text, project or scope changed since the person was shown it, when the person
      approves with the old hash, then the answer is refused, the pin stays waiting and it is shown with its new
      state.
- [ ] Given a waiting pin, when the person declines it, then the memory stays an ordinary memory on B, nothing is
      pushed, and A's pin is not removed.
- [ ] Given a waiting pin, when A unpins the memory, then the unpin applies and the waiting pin disappears; when
      the memory is deleted or forgotten here, or the person pins it here, then the waiting pin disappears too.
- [ ] Given a waiting pin whose text, project or scope then changes, when B syncs again, then the waiting pin
      shows the new text and is marked as changed since it was last shown; a repeated sync of the same record
      changes nothing.
- [ ] Given pinned text longer than the pinned-memory length limit, when the person approves it, then the answer
      is refused naming the limit, and the pin stays waiting.
- [ ] Given a memory pinned on B that machine A edited, when B syncs, then B's pinned text stays in force, A's
      other changes (tags, tier) apply, the pull completes, and the edit is listed as a held pin edit with its
      text.
- [ ] Given a held pin edit, when the person approves it with the hash they were shown, then the pinned memory
      takes A's text, project and scope, stays pinned, and nothing is pushed; text over the pinned-memory length
      limit is refused naming the limit.
- [ ] Given a held pin edit, when the person declines it, then B's text is published as a new edit that replaces
      A's edit in the repository and on every machine where the memory is not pinned, and is held as an edit where
      it is pinned (the person is told this before it runs), unless the text is held back by the secret check.
- [ ] Given a held pin edit, when the person edits only the tags on B, then the next sync pushes the tags and
      neither pushes the old text nor undoes A's edit.
- [ ] Given a held pin edit, when the person unpins the memory on B, then the edit's text is applied to it as an
      ordinary memory and the held edit disappears; when the person edits the pinned text, project or scope on B
      instead, then the held edit disappears and B's pinned memory, its text included, is published as an edit, as
      a decline would do it.
- [ ] Given a pin edit that changed again since the person was shown it, or a pinned text that the person edited
      since, when the person answers with the old hash, then the answer is refused (as changed, or as edited
      here), nothing is written but the item's new state, and the next sync closes a held edit that was edited
      here.
- [ ] Given any memory inserted from a pinned record, in any case (a new pin, a memory brought back after a
      deletion, one restored by the person), when it arrives, then it is unpinned and has a waiting pin.
- [ ] Given a record that needs a pin review and also has a conflicting edit, when B syncs, then it is still held
      back whole until conflicts can be answered.
- [ ] Given the list of items, when it is shown, then each shows its kind, id, state (waiting or changed since
      shown), the memory's text marked as text and never as an instruction, capped to the pinned-memory length
      with control characters removed, and for a held edit also the text pinned here; the bulk forms touch held
      deletions only.
- [ ] Given a waiting pin that came from restoring a held deletion, when it is answered, then it behaves as any
      other waiting pin.
- [ ] Given a sync cycle running or another answer in progress, when the person answers, then the answer is
      refused as busy and nothing changes.
- [ ] Given the command line help, when it is shown, then the review commands are not listed.
- [ ] Given the existing automated checks, the shim tests, the tool catalog check and the release build, when they
      run, then they pass and the release build shows no new warning, and nothing but the cycle and its tests runs
      a cycle.

## 2. Automatic sync
- [ ] Only the MCP server runs the timer; the CLI and web UI never start one.
- [ ] A remote whose head equals `L` costs one `ls-remote` and no fetch.
- [ ] A hung remote ends the wait within the stall limit plus 1 s; a progressing remote is waited for up to
      the ceiling and not beyond it plus 1 s; a remote projected past the ceiling returns `continuing`
      (the projection itself is milestone 3; before it, the ceiling alone decides).
- [ ] Entries apply pins first, then hot, warm, cold; a cut-off pass resumes with exactly the remainder.
- [ ] `background` returns without waiting; `off` runs no cycle at start.
- [ ] A host file already received by the pull is not imported again in the same `session_start`.
- [ ] The smoke test is skipped after a wait over the soft limit and runs at the next start.
- [ ] A `session_start` within ten minutes of a network failure does not block.
- [ ] The `session_end` cycle returns within `session_end_seconds`.
- [ ] Every state returns the `sync` block with its listed fields; arrivals are reported once.
- [ ] `sync_review` is listed in every mode and in `catalog.json`, answers "not active" outside git mode,
      is unreachable from the web API, and has no bulk action.
- [ ] With no `SSH_AUTH_SOCK` and no `HOME`, a cycle reports `needs-auth` with no prompt and no hang.
- [ ] No tool call fails for any row of the failure table; `demote` says other machines keep their tier;
      every host's startup text carries the sync and pin-review wording.
- [ ] The whole `session_start` stays under 60 s in a test with a slow fake remote and an artificially slow
      smoke test.

## 3. MCP progress
- [ ] With a progress token, at least one notification per phase carries it, with non-decreasing progress,
      and none follows the response; without a token there are none.
- [ ] 1,000 interleaved responses and notifications never mix bytes on stdout; other tools' responses are
      byte-identical to before; the shim forwards the notifications.
- [ ] The rate and ETA projection decides keep-waiting versus hand-over at the soft limit.

## 4. Setup experience
- [ ] Stub `gh`: an absent default name creates a private repository; a present one exits `repo-exists`
      with visibility, emptiness and manifest facts and writes nothing.
- [ ] `--use-existing` writes only into an empty repository; nothing is written into a non-empty
      repository without a manifest.
- [ ] Each documented status has a distinct exit code and a `--json` body with `status` and `next`;
      `doctor` changes nothing on disk.
- [ ] The skill is packaged and declared in each host manifest; every command line in it runs in tests.
- [ ] `docs/git-sync.md` covers setup, the session options, `leave`, restoring from a full clone, the
      manual purge (forget, then filter the path out), and the remedy for hosts that strip the
      environment.
- [ ] Manual checklist, evidence attached to the PR: one `init` and one `join` against a private GitHub
      repository, a sync between two real machines, and a forced `repo-exists`.

## 5. Legacy alias pass
- [ ] In both id orders, after two cycles per machine: one entry with the lowest id on each machine and one
      record in the repository.
- [ ] A local pin, the warmer tier, the tag union and the import-log target are carried; a differing text
      goes to conflict review, or to `pin-edit` when the local entry is sticky.
- [ ] A kill between convergence and record deletion resumes with no duplicate and no loss.
- [ ] An edit to a withheld entry arrives in the canonical entry.
- [ ] A canonical record deleted while the alias is open leaves the other holder's entry in place and
      pushed.
- [ ] A third machine's catch-up holding both the canonical add and the non-canonical delete ends with the
      canonical entry, in either processing order.
