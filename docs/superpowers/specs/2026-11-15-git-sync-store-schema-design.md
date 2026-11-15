# Git sync, milestone 0b-3: the sync tables and the sync settings

Date: 2026-11-15. Milestone 0b-3 of the git sync feature tracked in #93 (0a is #100, 0b-1 is #105, 0b-2 is #111).
The umbrella design is `2026-11-15-git-sync-design.md` ("New state", "Configuration", "Time and progress" and
"Deletions" give the tables and the keys). Milestone 0b is split in three; this is the last: it is independent of
0b-1 and 0b-2 and nothing uses it yet, so **no user-visible behavior changes**.

## Problem

Every later milestone stores its state in a handful of tables (what was last reconciled with the repository, the
items awaiting the user's review, the problems to show, the legacy duplicates) and reads its settings from a
`[git_sync]` section of `config.toml`. Both have to exist, with exactly the shape the cycle expects, before any
code writes to them. A table with the wrong key, or a setting that accepts a value the cycle cannot run with,
would be a data-integrity bug found late. They are small and independent of the sync logic, so they are added
first, on their own, with tests that pin the shape.

## Goals

1. **Migration 18** creates the sync tables and an index on `import_log(target_entry_id)`, on SQLite stores,
   idempotently, without touching existing data.
2. `Core.Config.GitSyncConfig`: the settings record with the umbrella's keys and defaults, a pure
   normalisation that turns any input into a usable configuration plus a list of warnings, and the loader that
   reads `[git_sync]` from `config.toml`.
3. A `[git_sync]` section whose values are of the wrong type, out of range, unknown, or not a table never makes
   the configuration fail to load: each bad value becomes its default (or is clamped) and is recorded as a
   warning in the loaded configuration, naming the key; a remote or branch that cannot be used safely is
   recorded as not set.
4. No change to any existing behavior, table, key, tool or config snapshot.

## Non-goals

- Anything that reads or writes the new tables or the `_meta` keys (`git_sync_last_sha`, `git_sync_hlc`, …):
  milestones 1a to 1b-iii. Migration 18 creates tables only; `_meta` keys are created when first written.
- The `config_set` denylist for `storage.mode` and `git_sync.*`, `OpenGit`, accepting `storage.mode = "git"`,
  `GitClient`, the clone path, the URL scheme and transport checks: milestone 1a.
- The encodings of `pushing_stamps` and `repush_groups`, and of the JSON payloads: the milestone that first
  writes them (1b-i, 1b-ii, 1b-iii) fixes them. This milestone stores them as opaque TEXT.
- TOML syntax errors: a syntax error anywhere in `config.toml`, including inside `[git_sync]`, still fails the load
  exactly as it does for every other section (`ConfigLoader.ParseToml` runs before any section is projected).
- Surfacing the warnings to the user (`status`, stderr): the consumers are later milestones; this one records them.
- Serializing `[git_sync]` into config snapshots (`ConfigJsonSerializer` is unchanged) and the web UI Config page.
- Postgres: the sync tables are SQLite-only (the umbrella: "SQLite and cortex stores only"); the Postgres
  migration runner is untouched. Cortex mode uses the local SQLite store, so it gets the tables with no extra work.

## Decided here

The umbrella lists the table columns and the config keys; the spec fixes what it leaves open.

1. **Column types and constraints.** Stamps and hashes are TEXT; timestamps are INTEGER Unix milliseconds (as
   `import_log.timestamp` and `retrieval_events.timestamp`); booleans are INTEGER 0/1. The closed vocabularies
   the umbrella names (`state`, review `kind`, review `state`, `direction`, issue `kind`, alias `state`) get
   `CHECK` constraints, so a typo in a later milestone fails at the insert and not three cycles later.
   Nothing else is constrained: the other columns are written by code that does not exist yet.
2. **Primary keys.** `git_sync_entry`: `entry_id`. `git_sync_review`: `(entry_id, kind)` (the umbrella).
   `git_sync_issue`: `(entry_id, kind)`. `git_sync_alias`: `(canonical_id, other_id)`.
3. **`GitSync` on `TotalRecallConfig` is `Some` only when a `[git_sync]` table exists** (the pattern of
   `[cortex]`). `defaults.toml` ships the table commented out as documentation, not live, so a machine that
   never configures sync has `GitSync = None` and sees no change. Absent keys take the defaults below.
4. **Bad values never fail the load.** A value of the wrong TOML type, out of range, or outside the allowed set
   takes its default (or is clamped) and adds a warning `git_sync.<key>: <what was wrong>; using <value>`.
   Projecting `[git_sync]` does not throw for any value the TOML parser accepts. (A syntax error is the parser's
   and fails as before; an integer literal beyond `int64` is such a syntax error, so `RawValue` needs no case for it.) (The pinned-tier loader throws on a wrong type;
   a typo in an optional section that breaks every command is a worse failure than a warning for settings that
   are only read by sync.)
5. **A remote or branch that cannot be used safely is "not set", not defaulted.** A branch defaulted to
   `main` after a typo would push to the wrong branch, so an invalid `branch` is `None` and the consumer
   (1a: `OpenGit`) treats it as a configuration that cannot work. The same for `remote`.
6. **Ranges** are chosen to keep a bad value from hurting the user or the remote, and are stated in the table
   below; each is a constant in `GitSyncConfig` so a later milestone can read it.

## Design

### Migration 18

`Migration18_GitSync` is appended to the migrations array in `Schema.cs` (version 18; migrations 1 to 17 are
frozen). It runs inside the runner's transaction and uses `CREATE TABLE IF NOT EXISTS` and
`CREATE INDEX IF NOT EXISTS` only, so it is safe on a database that already has them. It does not use
`ALTER TABLE` and reads no existing row.

```sql
CREATE TABLE IF NOT EXISTS git_sync_entry (
    entry_id             TEXT PRIMARY KEY NOT NULL,
    state                TEXT NOT NULL CHECK (state IN ('active','forgotten','left')),
    base_json            TEXT,
    scanned_content_hash TEXT,
    scanned_tags_hash    TEXT,
    local_sticky         INTEGER,
    local_tier           TEXT,
    content_stamp        TEXT,
    sticky_stamp         TEXT,
    tier_stamp           TEXT,
    repush_groups        TEXT,
    pushing_stamps       TEXT,
    blocked_hash         TEXT,
    blocked_reason       TEXT,
    allowed_hash         TEXT
);

CREATE TABLE IF NOT EXISTS git_sync_review (
    entry_id     TEXT NOT NULL,
    kind         TEXT NOT NULL CHECK (kind IN ('new-pin','pin-edit','conflict','held-delete')),
    state        TEXT NOT NULL CHECK (state IN ('open','unseen','approved','declined','resolved')),
    record_hash  TEXT,
    direction    TEXT CHECK (direction IS NULL OR direction IN ('remote','local')),
    payload_json TEXT,
    machine      TEXT,
    arrived_at   INTEGER NOT NULL,
    PRIMARY KEY (entry_id, kind)
);

CREATE TABLE IF NOT EXISTS git_sync_issue (
    entry_id    TEXT NOT NULL,
    kind        TEXT NOT NULL CHECK (kind IN ('apply-failed','rejected-record','ineligible-id')),
    record_hash TEXT,
    attempts    INTEGER NOT NULL DEFAULT 0,
    detail      TEXT,
    PRIMARY KEY (entry_id, kind)
);

CREATE TABLE IF NOT EXISTS git_sync_alias (
    canonical_id TEXT NOT NULL,
    other_id     TEXT NOT NULL,
    origin_hash  TEXT,
    state        TEXT NOT NULL CHECK (state IN ('open','converged','resolved','abandoned')),
    PRIMARY KEY (canonical_id, other_id)
);

CREATE INDEX IF NOT EXISTS idx_import_log_target_entry_id ON import_log(target_entry_id);
```

`local_sticky` and `local_tier` are NULL until the first scan of the entry. `base_json` is NULL until the entry
has been reconciled once (the umbrella). The `git_sync_review.arrived_at` is NOT NULL (every item records when it
arrived) and has no default: the writer supplies it.

### The settings

In `Config.fs`, beside `CortexConfig`:

```fsharp
type GitSessionStart = Blocking | Background | Off

type GitSyncConfig =
    { Remote: string option              // None: not set, or not usable
      Branch: string option              // Some "main" by default; None only when a value was given and is not usable
      IntervalSeconds: int
      SessionStart: GitSessionStart
      SessionStartSoftSeconds: int
      SessionStartMaxSeconds: int
      SessionStartStallSeconds: int
      SessionEnd: bool
      SessionEndSeconds: int
      DeleteGuardPercent: int
      Warnings: string list }
```

and `TotalRecallConfig.GitSync: GitSyncConfig option` (a new last field). The pure module `GitSyncSettings`
(`Config.fs`, after the types; a distinct name, because a record type and a module of the same name in one flat
module would get an implicit `Module` suffix that C# callers see) holds the range constants as `[<Literal>]`s
and has:

- `defaults : GitSyncConfig` (remote `None`, branch `Some "main"`, no warnings);
- `normalise : RawGitSync -> GitSyncConfig`, where `RawGitSync` carries each key as a `RawValue` of
  `Absent | Bad of string | Str of string | Int of int64 | Bool of bool` (`Bad` is a value of an unsupported TOML
  type, with its type name). The loader fills it from the TOML table; `normalise` never throws and is total.

| Key | Default | Rule (anything else: the default, plus a warning) |
|---|---|---|
| `remote` | not set | a string; trimmed; non-empty; no whitespace or control characters; does not start with `-`. Else `None`. An empty string is "not set" without a warning |
| `branch` | `"main"` | a string matching `^[A-Za-z0-9][A-Za-z0-9._/-]{0,99}$`, with no `..` and no `//`, not ending in `/` or `.`, and **no `/`-separated component that begins with `.` or ends with `.lock`**. Else `None` + warning (not defaulted, Decided 5). The allowed set is deliberately narrower than git's (it excludes names git would accept, such as a leading `_` or non-ASCII letters) and never wider |
| `interval_seconds` | 300 | an integer; `0` disables the timer; otherwise 30 to 86,400. A value 1 to 29 is **clamped to 30**; a negative value or one above 86,400 takes the default |
| `session_start` | `blocking` | one of `"blocking"`, `"background"`, `"off"` (case-sensitive) |
| `session_start_soft_seconds` | 10 | an integer 1 to 3,600 |
| `session_start_max_seconds` | 20 | an integer 1 to 3,600 |
| `session_start_stall_seconds` | 8 | an integer 1 to 3,600 |
| `session_end` | `true` | a boolean |
| `session_end_seconds` | 8 | an integer 1 to 3,600 |
| `delete_guard_percent` | 20 | an integer 0 to 100 |

After the per-key rules, **if `soft > max`, `soft` is lowered to `max`** (the hard ceiling wins). The warning is given only when `session_start_soft_seconds` was written; when it was absent and `max` is below the default soft value, `soft` is lowered silently.
Warnings are one line each, in key order of the table, so the output is deterministic.

The loader, in `ConfigLoader.Project` next to the other sections, builds `GitSync` from `table["git_sync"]` when
it is a table (`None` otherwise, including when `git_sync` is present but not a table, which adds no config and
is left to the TOML layer). Floats, arrays and nested tables are `Bad`. It calls `normalise`
and wraps the result in `Some`. `ConfigJsonSerializer` is unchanged (`GitSync` is not serialized, Decided 3).

`defaults.toml` gets a commented `[git_sync]` block documenting the keys and defaults, next to `[cortex]`.

## Testing

- **Schema** (`tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncSchemaTests.cs`, not `Integration`-trait so
  they run in the standard verify): a fresh in-memory database has the four tables, every column of the
  umbrella's list with its type, the primary keys, and the `import_log` index; `_schema_version` is 18; running
  migrations again changes nothing; a database stopped at version 17 with rows in `hot_memories`, `import_log`
  and `_meta` upgrades with every row intact (the test drops the new objects and sets the version back to 17);
  each `CHECK` rejects a value outside its list and accepts each value inside it; each primary key rejects a
  duplicate; `direction` accepts NULL; `arrived_at` rejects NULL.
- **Settings, pure** (`tests/TotalRecall.Core.Tests/GitSyncConfigTests.fs`, added to `TotalRecall.Core.Tests.fsproj` before `Program.fs`): defaults; every key with a valid
  value; every rule row with a boundary below, at and above each limit; wrong types; unsafe `remote` and `branch`
  strings (leading `-`, spaces, tab, newline, NUL, `..`, `//`, a trailing `.lock`, a component such as `feature/.x` or `a.lock/b`, 101 characters); `soft > max` with and without an explicit soft value; the
  warning text and order; totality over generated `RawValue`s (never throws; every field in range).
- **Loader** (`tests/TotalRecall.Infrastructure.Tests/ConfigLoaderTests.cs`): no `[git_sync]` gives `GitSync = None`
  and the rest of the configuration unchanged; a section with only a remote gives the defaults; every key set;
  a malformed section (wrong types, unknown keys, `git_sync` as a string) loads with warnings and does not throw;
  a user file overriding a default of another section is unaffected.
- **Existing test updated:** `SchemaTests.RunMigrations_RunTwice_Idempotent` asserts 17 `_schema_version` rows and
  `MAX(version) = 17` (and a stale "initial 3" comment); both become 18. `SchemaTests` is `Integration`-trait, so the
  standard verify does not run it: the full unfiltered Infrastructure suite is run once locally for this change.
- **No regression:** the existing suites; the config snapshot JSON of a config with a `[git_sync]` section equals
  that of the same config without it; `StatusCommandTests` (positional construction) and `TotalRecall.Core.Tests/ConfigTests.fs` (a record
  literal) are updated for the new field.
- AOT: the publish shows no new warning.

## Risks

- A new field on `TotalRecallConfig` breaks every construction of it. There are three (the loader,
  `StatusCommandTests`, and a record literal in `TotalRecall.Core.Tests/ConfigTests.fs`); the compiler finds any other.
- A migration number collision with a concurrent change: migration 18 is the next free number on `main` at the
  time of writing; the implementer re-checks before the PR.
- The ranges are judgments. They are constants in one place and the warnings say what was used, so changing one
  later is a one-line edit.

## Documentation

- `src/TotalRecall.Infrastructure/AGENTS.md`: the migration table gets the missing row for migration 17 and a row
  for migration 18, and the "16 sequential migrations" count is corrected. Root `AGENTS.md` "Database Migrations":
  "currently 16" becomes 18, and the checklist's schema-test step says new schema tests go in a non-`Integration`
  test file so the standard verify runs them.
- `src/TotalRecall.Core/AGENTS.md`: the `Config.fs` row mentions `GitSyncConfig`.
- `CHANGELOG.md` is not changed: nothing user-visible.
