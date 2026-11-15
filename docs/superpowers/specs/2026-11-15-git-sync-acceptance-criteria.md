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
- [ ] A forget on A deletes on B. B's unpushed tag, pin, project or promotion change does not keep it;
      B's unpushed text edit keeps it on both machines, in either push order.
- [ ] A forgotten memory is not re-inserted by a re-import on a new machine, a promotion elsewhere, a
      same-text restore elsewhere, or older text after a reset.
- [ ] A pin that is sticky on B is held (not deleted) when another machine deletes it.
- [ ] More than max(10, 20 %) deletions in one direction: none applied, each held, the cycle reports
      `done` with a count, and other changes still push.
- [ ] A hand-reset remote (six of nine records, and one case above the guard), after two cycles per machine
      with an unrelated push between: nothing deleted locally, each vanished record held, each reverted
      group republished from its base value, and B's declined pin does not unpin A.
- [ ] A push that fails after an unproven apply still republishes the reverted groups next cycle.
- [ ] A held entry is not written back by a tag, pin or promotion while unanswered; answering some items
      leaves the rest held.
- [ ] `accept` and `restore` work in both directions; `restore` never yields a sticky entry; bulk forms
      refuse without a terminal.
- [ ] `sync init --url <new repository>` on a machine with rows deletes nothing; a blocked, never-pushed
      entry survives a lost clone; an entry added and deleted inside one catch-up is deleted.

## 1b-iii. Review and secrets
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
