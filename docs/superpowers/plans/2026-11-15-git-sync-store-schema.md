# Git sync 0b-3: the sync tables and the sync settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add migration 18 (the four `git_sync_*` tables and an `import_log(target_entry_id)` index) and the `[git_sync]` settings (`GitSyncConfig`, a pure normaliser, the loader), with tests. Nothing uses either yet.

**Architecture:** `Schema.cs` gets `Migration18_GitSync` (plain DDL, `IF NOT EXISTS`). `TotalRecall.Core/Config.fs` gets the settings types, a raw-value type, and a pure module `GitSyncSettings.normalise : RawGitSync -> GitSyncConfig` that never throws and returns warnings; `TotalRecallConfig` gains a last field `GitSync: GitSyncConfig option`. `ConfigLoader.cs` turns the TOML table into a `RawGitSync` and calls `normalise`.

**Tech Stack:** F# (Expecto + FsCheck 2.16.6) for the pure part; C# net10 NativeAOT (xUnit, Tomlyn 0.19 values `string`, `long`, `bool`, `double`, `TomlTable`, `TomlArray`) for the rest.

**Spec:** `docs/superpowers/specs/2026-11-15-git-sync-store-schema-design.md` (read it first; this plan gives the code and the test lists). The umbrella is `docs/superpowers/specs/2026-11-15-git-sync-design.md`. Issue #113.

## Global Constraints

- The spec file is read-only for the whole run. Commits carry no trailer and no AI attribution; messages are plain imperative sentences.
- Migrations 1 to 17 are frozen. Migration 18 uses `CREATE TABLE IF NOT EXISTS` and `CREATE INDEX IF NOT EXISTS` only; no `ALTER`, no read of an existing row.
- Core has no I/O and no mutable state; C# is AOT-safe (no reflection serialization). No new package.
- A warning never echoes the value of `remote` (it may carry credentials in an `https://user:token@host` URL); other warnings name the key and the problem, and may name a rejected number or a fixed word, never a free string.
- New F# source goes in `Config.fs` (no new file); the new F# test file `GitSyncConfigTests.fs` goes right before `Program.fs` in `TotalRecall.Core.Tests.fsproj`. New C# tests: `tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncSchemaTests.cs` (no `Integration` trait, namespace `TotalRecall.Infrastructure.Tests.GitSync`) and additions to `ConfigLoaderTests.cs`.
- Test commands: `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj`; `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GitSync|FullyQualifiedName~ConfigLoader"`. A fresh worktree needs `npm ci --ignore-scripts` (sqlite-vec) and `sh scripts/fetch-bge-small.sh` once (the Core Tokenizer tests need the vocab).
- A test name quotes the spec's rule or the ticket's wording so a reviewer compares one to one.

## Interpretations (recorded here because the spec is read-only)

1. **Names.** The raw-value type is `GitRawValue` with cases `RawAbsent | RawBad of string | RawStr of string | RawInt of int64 | RawBool of bool`, and `RawGitSync` has fields `RemoteRaw`, `BranchRaw`, `IntervalRaw`, `SessionStartRaw`, `SoftRaw`, `MaxRaw`, `StallRaw`, `SessionEndRaw`, `SessionEndSecondsRaw`, `DeleteGuardRaw` (distinct labels, so no record-inference clash with `GitSyncConfig`). The `Raw` prefix on the cases avoids a clash with `SyncValidation.RawField`'s `Absent`.
2. **Warning wording** (pinned by the pure tests): `git_sync.<key>: expected <an integer|a string|a boolean>, got <a type>; using <value>` for a wrong type; `git_sync.<key>: <n> is outside <lo> to <hi>; using <value>` for a range; `git_sync.interval_seconds: <n> is below the minimum of 30; using 30`; `git_sync.session_start: not one of "blocking", "background", "off"; using "blocking"`; `git_sync.remote: not a usable repository address; treated as not set` and `git_sync.branch: not a usable branch name; treated as not set`, with `expected a string, got <type>; treated as not set` for the wrong-type variants of those two; `git_sync.session_start_soft_seconds: <s> is above session_start_max_seconds; using <max>`. `described` receives the full phrase from the loader for an unsupported type ("a float", "a table", "an array", "a value of another type"), so `RawBad` carries that phrase.
3. **Order of warnings** is the table's key order; the soft-above-max warning sits at the `session_start_soft_seconds` slot (it can never co-occur with that key's own warning, because it needs a valid explicit soft). When a bad soft is then lowered to a smaller max, its own warning names the value finally used.
4. **`DefaultSoft` lowering without warning** applies when `SoftRaw` was absent or itself bad (it already got its own warning), exactly as the spec says; only a valid explicit soft above max warns.

## Review Focus

1. A `remote` containing credentials never appears in a warning [Task 2].
2. `interval_seconds = 0` disables the timer without a warning, `1` to `29` is raised to 30 with one [Task 2].
3. A bad `branch` is `None`, never `Some "main"` [Task 2, 3].
4. A malformed `[git_sync]` loads (wrong types, `git_sync = "x"`), while a TOML syntax error and an overflowing integer literal still fail the load [Task 3].
5. The upgrade-from-17 test keeps every existing row [Task 1].

---

### Task 1: Migration 18 and the schema tests

**Files:** Modify `src/TotalRecall.Infrastructure/Storage/Schema.cs`, `tests/TotalRecall.Infrastructure.Tests/SchemaTests.cs`. Create `tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncSchemaTests.cs`.

- [ ] **Step 1: Write the failing tests** (`GitSyncSchemaTests`, a plain xUnit class; each test opens `SqliteConnection.Open(":memory:")` and runs `MigrationRunner.RunMigrations`, as `ConfigSnapshotStoreNamingTests` does):
  - `FreshDatabase_HasTheFourTablesWithTheDesignsColumns`: for each table, `pragma_table_info` returns exactly these (name, type, notnull, pk):
    - `git_sync_entry`: `entry_id TEXT notnull pk` (`PRIMARY KEY NOT NULL` reports notnull), `state TEXT notnull`, and TEXT nullable `base_json scanned_content_hash scanned_tags_hash local_tier content_stamp sticky_stamp tier_stamp repush_groups pushing_stamps blocked_hash blocked_reason allowed_hash`, INTEGER nullable `local_sticky`.
    - `git_sync_review`: `entry_id`, `kind`, `state` TEXT notnull; `record_hash`, `direction`, `payload_json`, `machine` TEXT nullable; `arrived_at INTEGER notnull`; primary key columns `entry_id`, `kind`.
    - `git_sync_issue`: `entry_id`, `kind` TEXT notnull (pk); `record_hash`, `detail` nullable TEXT; `attempts INTEGER notnull` default `0`.
    - `git_sync_alias`: `canonical_id`, `other_id` TEXT notnull (pk); `origin_hash` nullable; `state` TEXT notnull.
  - `FreshDatabase_HasTheImportLogIndex`: `idx_import_log_target_entry_id` exists on `import_log(target_entry_id)` (`pragma_index_info`).
  - `SchemaVersion_IsEighteen`: `MAX(version) = 18`, `COUNT(*) = 18`.
  - `RunningTwice_ChangesNothing`: tables and indexes (names from `sqlite_master`) and `_schema_version` rows equal after a second run.
  - `UpgradeFromSeventeen_KeepsEveryExistingRow`: run migrations, then insert a row into `_meta` (`x`/`y`), `import_log` (all eight columns), and `hot_memories` (`INSERT INTO hot_memories (id, content, created_at, updated_at, last_accessed_at)` with fixed values; the other columns have defaults), and snapshot every column of each row (`SELECT *`) before and after, then `DROP TABLE` the four sync tables, `DROP INDEX idx_import_log_target_entry_id`, `DELETE FROM _schema_version WHERE version = 18`; run migrations again; assert the four tables and the index are back, `MAX(version) = 18`, and the three inserted rows are byte-for-byte unchanged.
  - `IfNotExists_IsExercised`: after a normal run insert one `git_sync_entry` row, `DELETE FROM _schema_version WHERE version = 18` (leave the tables and the index in place) and run migrations again: no exception, the row is intact, and `MAX(version) = 18` (this is the only test that fails if `IF NOT EXISTS` is dropped).
  - `Constraints`: for each of the six closed vocabularies insert every allowed value (accepted) and one value outside (a `SqliteException` whose message contains `CHECK constraint failed`): entry `state` (`active`, `forgotten`, `left`; `deleted`); review `kind` (`new-pin`, `pin-edit`, `conflict`, `held-delete`; `new_pin`); review `state` (`open`, `unseen`, `approved`, `declined`, `resolved`; `closed`); review `direction` (`remote`, `local`, NULL; `both`); issue `kind` (`apply-failed`, `rejected-record`, `ineligible-id`; `other`); alias `state` (`open`, `converged`, `resolved`, `abandoned`; `done`). Insert helpers use a distinct `entry_id` per row so only the checked column can fail.
  - `PrimaryKeys`: a second `git_sync_entry` row with the same `entry_id`; a second `git_sync_review` with the same `(entry_id, kind)` (but a third with the same `entry_id` and another kind is accepted); a second `git_sync_issue` with the same `(entry_id, kind)`; a second `git_sync_alias` with the same `(canonical_id, other_id)` (the reversed pair is accepted) are each refused with `UNIQUE constraint failed`.
  - `NullabilityAndDefaults`: `arrived_at = NULL` is refused (`NOT NULL constraint failed`); `attempts` omitted gives 0; `git_sync_entry` with only `entry_id` and `state` is accepted.
  - Update `SchemaTests.RunMigrations_RunTwice_Idempotent`: both `17L` become `18L`, and the stale comment "beyond the initial 3" becomes "beyond the migrations already applied".

- [ ] **Step 2: Run, confirm the new tests fail** (no tables) and `RunMigrations_RunTwice_Idempotent` (Integration trait, run with `--filter "FullyQualifiedName~SchemaTests"`) fails on 18.

- [ ] **Step 3: Implement.** In `Schema.cs` append to the migrations array `// Migration 18: git sync state tables (sync foundations) and an index on import_log(target_entry_id)` / `Migration18_GitSync,` and add, after `Migration17_HotStickyColumn`:

```csharp
    private static readonly string[] GitSyncDdl =
    {
        """
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
        )
        """,
        """
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
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS git_sync_issue (
            entry_id    TEXT NOT NULL,
            kind        TEXT NOT NULL CHECK (kind IN ('apply-failed','rejected-record','ineligible-id')),
            record_hash TEXT,
            attempts    INTEGER NOT NULL DEFAULT 0,
            detail      TEXT,
            PRIMARY KEY (entry_id, kind)
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS git_sync_alias (
            canonical_id TEXT NOT NULL,
            other_id     TEXT NOT NULL,
            origin_hash  TEXT,
            state        TEXT NOT NULL CHECK (state IN ('open','converged','resolved','abandoned')),
            PRIMARY KEY (canonical_id, other_id)
        )
        """,
        "CREATE INDEX IF NOT EXISTS idx_import_log_target_entry_id ON import_log(target_entry_id)",
    };

    /// <summary>
    /// Migration 18 — the state tables of git sync and an index so an import can be found by the entry it
    /// created. Tables and index only; nothing reads or writes them yet. SQLite only (the Postgres runner is
    /// untouched).
    /// </summary>
    private static void Migration18_GitSync(
        MsSqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        foreach (var sql in GitSyncDdl)
            Exec(conn, tx, sql);
    }
```

- [ ] **Step 4: Run until green** (both the focused GitSync tests and `SchemaTests`). Reviewer gate. Commit: `Add migration 18 with the git sync state tables`.

---

### Task 2: The settings types, the pure normaliser and the field on `TotalRecallConfig`

**Files:** Modify `src/TotalRecall.Core/Config.fs`, `src/TotalRecall.Infrastructure/Config/ConfigLoader.cs` (pass `None`), `tests/TotalRecall.Core.Tests/ConfigTests.fs` (add `GitSync = None`), `tests/TotalRecall.Cli.Tests/Commands/StatusCommandTests.cs` (add `FSharpOption<Core.Config.GitSyncConfig>.None`), `tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj`. Create `tests/TotalRecall.Core.Tests/GitSyncConfigTests.fs`.

- [ ] **Step 1: Write the failing tests** (`testList "GitSyncSettings"`). Helper `absent : RawGitSync` has every field `RawAbsent`; tests change one field with `{ absent with ... }` (type-annotated).
  - **Defaults:** `normalise absent` equals `{ Remote = None; Branch = Some "main"; IntervalSeconds = 300; SessionStart = Blocking; SessionStartSoftSeconds = 10; SessionStartMaxSeconds = 20; SessionStartStallSeconds = 8; SessionEnd = true; SessionEndSeconds = 8; DeleteGuardPercent = 20; Warnings = [] }`, and equals `GitSyncSettings.defaults`.
  - **Valid values:** every key set to a valid non-default value is returned as written (remote `"git@github.com:alice/total-recall-sync.git"` and an `https://` URL; branch `"dev/sync-1.x"`; interval 60; `background`; 5/15/3; `false`; 12; 50) with no warnings; remote is trimmed (`"  git@h:o/r.git "` → `"git@h:o/r.git"`).
  - **remote:** `RawStr ""` and `RawStr "   "` → `None`, no warning; `"-oProxyCommand=x"`, `"a b"`, `"git@h:o/r.git\n"` is trimmed so valid, `"a\tb"`, `"a\u0000b"`, `"\u007fx"` → `None` + the remote warning; `RawInt 5` and `RawBad "an array"` → `None` + the wrong-type remote warning (the text reads `got an array`); **no warning text contains any character of a credential-bearing value**: `RawStr "https://user:secret-token@host/x y"` gives a warning that does not contain `secret-token` or `user`.
  - **branch:** valid: `"main"`, `"dev/sync-1.x"`, `"a"`, 100-character `"a…"`; invalid → `None` + warning: `""`, `"-x"`, `"a b"`, `"a..b"`, `"a//b"`, `"a/"`, `"a."`, `"x.lock"`, `"a.lock/b"`, `"feature/.x"`, `".x"`, `"_x"`, `"é"`, 101 characters, `RawInt 1`. A bad branch is never `Some "main"`.
  - **interval:** `RawInt 0L` → 0 without a warning; `1L` and `29L` → 30 with the minimum warning; `30L`, `300L`, `86400L` → as written; `-1L`, `86401L` → 300 with the range warning; `RawStr "5"`/`RawBool true`/`RawBad "a float"` → 300 with the wrong-type warning.
  - **session_start:** the three words (case-sensitive: `"Blocking"` is invalid → `Blocking` + warning); a non-string → default + wrong-type warning.
  - **soft, max, stall, session_end_seconds:** `0L` and `3601L` → default + range warning; `1L`, `3600L` → as written; wrong types.
  - **delete_guard_percent:** `-1L` and `101L` → 20 + warning; `0L`, `100L` → as written.
  - **session_end:** a bool is kept; any other type → `true` + wrong-type warning.
  - **soft above max:** explicit soft 30 with max 20 → soft 20 and the soft-above-max warning (last); soft absent with max 5 → soft 5, **no** warning; soft `RawStr "x"` with max 5 → soft 5 and **only** the wrong-type warning (no second warning); soft 5 and max 5 → no change.
  - **Warning order and count:** several bad keys at once yield the warnings in the table's key order with the soft-above-max warning last, each a single line (no newline).
  - **Property (FsCheck: `Gen.oneof` over the listed cases, `Arb.fromGen`, and `testPropertyWithConfig` as in `SyncMergeTests.fs`):** for any generated `RawGitSync` (each field one of `RawAbsent`, `RawBad "a float"`, `RawStr` of a generated string (including control characters and long strings), `RawInt` over `int64` extremes and the boundaries, `RawBool`), `normalise` returns without throwing and every field is in range (`IntervalSeconds = 0 || 30..86400`; the three limits and `SessionEndSeconds` in `1..3600`; `SessionStartSoftSeconds <= SessionStartMaxSeconds`; `DeleteGuardPercent` in `0..100`; `Remote` is `None` or satisfies the remote rule; `Branch` is `None` or satisfies the branch rule), and `Warnings` has no element containing a newline.

- [ ] **Step 2: Run, confirm they fail to compile** (no types).

- [ ] **Step 3: Implement.** In `Config.fs`, after `CortexConfig` add the types (and the field `GitSync: GitSyncConfig option` last in `TotalRecallConfig`; the module `GitSyncSettings` goes right after `TotalRecallConfig`):

```fsharp
type GitSessionStart =
    | Blocking
    | Background
    | Off

/// The `[git_sync]` settings after normalisation. `Remote` is `None` when not set or not usable; `Branch` is
/// `Some "main"` when absent and `None` only when a value was given that cannot be used. `Warnings` lists what
/// was corrected, one line each.
type GitSyncConfig = {
    Remote: string option
    Branch: string option
    IntervalSeconds: int
    SessionStart: GitSessionStart
    SessionStartSoftSeconds: int
    SessionStartMaxSeconds: int
    SessionStartStallSeconds: int
    SessionEnd: bool
    SessionEndSeconds: int
    DeleteGuardPercent: int
    Warnings: string list
}

/// One TOML value as the loader saw it. `RawBad` carries a phrase naming an unsupported type ("a float", "an array").
type GitRawValue =
    | RawAbsent
    | RawBad of string
    | RawStr of string
    | RawInt of int64
    | RawBool of bool

type RawGitSync = {
    RemoteRaw: GitRawValue
    BranchRaw: GitRawValue
    IntervalRaw: GitRawValue
    SessionStartRaw: GitRawValue
    SoftRaw: GitRawValue
    MaxRaw: GitRawValue
    StallRaw: GitRawValue
    SessionEndRaw: GitRawValue
    SessionEndSecondsRaw: GitRawValue
    DeleteGuardRaw: GitRawValue
}
```

then add `GitSync: GitSyncConfig option` after `ToolCache` in `TotalRecallConfig`, and below it:

```fsharp
module GitSyncSettings =

    [<Literal>]
    let DefaultBranch = "main"
    [<Literal>]
    let DefaultIntervalSeconds = 300
    [<Literal>]
    let MinIntervalSeconds = 30
    [<Literal>]
    let MaxIntervalSeconds = 86400
    [<Literal>]
    let MaxSeconds = 3600
    [<Literal>]
    let DefaultSoftSeconds = 10
    [<Literal>]
    let DefaultMaxSeconds = 20
    [<Literal>]
    let DefaultStallSeconds = 8
    [<Literal>]
    let DefaultSessionEndSeconds = 8
    [<Literal>]
    let DefaultDeleteGuardPercent = 20

    let defaults : GitSyncConfig =
        { Remote = None; Branch = Some DefaultBranch; IntervalSeconds = DefaultIntervalSeconds; SessionStart = Blocking
          SessionStartSoftSeconds = DefaultSoftSeconds; SessionStartMaxSeconds = DefaultMaxSeconds
          SessionStartStallSeconds = DefaultStallSeconds; SessionEnd = true; SessionEndSeconds = DefaultSessionEndSeconds
          DeleteGuardPercent = DefaultDeleteGuardPercent; Warnings = [] }

    let private described (v: GitRawValue) : string =
        match v with
        | RawAbsent -> "nothing"
        | RawBad phrase -> phrase
        | RawStr _ -> "a string"
        | RawInt _ -> "an integer"
        | RawBool _ -> "a boolean"

    let private isAscii (c: char) = c < '\u0080'
    let private isAlnum (c: char) = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')

    /// A repository address that cannot be an option to `git` or carry whitespace or control characters.
    /// Scheme and transport checks are milestone 1a's.
    let isUsableRemote (s: string) : bool =
        s.Length > 0 && s.[0] <> '-' && not (s |> Seq.exists (fun c -> System.Char.IsWhiteSpace c || System.Char.IsControl c))

    /// `^[A-Za-z0-9][A-Za-z0-9._/-]{0,99}$` with no `..` and no `//`, not ending in `/` or `.`, and no
    /// `/`-separated component that begins with `.` or ends with `.lock`.
    let isUsableBranch (s: string) : bool =
        s.Length >= 1 && s.Length <= 100
        && isAlnum s.[0]
        && (s |> Seq.forall (fun c -> isAlnum c || c = '.' || c = '_' || c = '/' || c = '-'))
        && not (s.Contains "..") && not (s.Contains "//")
        && not (s.EndsWith "/") && not (s.EndsWith ".")
        && (s.Split('/') |> Array.forall (fun part -> not (part.StartsWith ".") && not (part.EndsWith ".lock")))

    /// An integer key in `lo..hi`: the value, a description of the problem (without the "using" part) and
    /// whether the value was explicit and valid.
    let private intKey (lo: int) (hi: int) (dflt: int) (raw: GitRawValue) : int * string option * bool =
        match raw with
        | RawAbsent -> dflt, None, false
        | RawInt n when n >= int64 lo && n <= int64 hi -> int n, None, true
        | RawInt n -> dflt, Some (sprintf "%d is outside %d to %d" n lo hi), false
        | other -> dflt, Some (sprintf "expected an integer, got %s" (described other)), false

    let private warn (key: string) (problem: string option) (used: int) : string option =
        problem |> Option.map (fun p -> sprintf "git_sync.%s: %s; using %d" key p used)

    let normalise (raw: RawGitSync) : GitSyncConfig =
        // each step returns (value, warning option); the warnings are collected in key order
        let remote, remoteWarn =
            match raw.RemoteRaw with
            | RawAbsent -> None, None
            | RawStr s ->
                let t = s.Trim()
                if t = "" then None, None
                elif isUsableRemote t then Some t, None
                else None, Some "git_sync.remote: not a usable repository address; treated as not set"
            | other -> None, Some (sprintf "git_sync.remote: expected a string, got %s; treated as not set" (described other))
        let branch, branchWarn =
            match raw.BranchRaw with
            | RawAbsent -> Some DefaultBranch, None
            | RawStr s when isUsableBranch s -> Some s, None
            | RawStr _ -> None, Some "git_sync.branch: not a usable branch name; treated as not set"
            | other -> None, Some (sprintf "git_sync.branch: expected a string, got %s; treated as not set" (described other))
        let interval, intervalWarn =
            match raw.IntervalRaw with
            | RawAbsent -> DefaultIntervalSeconds, None
            | RawInt 0L -> 0, None
            | RawInt n when n >= 1L && n < int64 MinIntervalSeconds ->
                MinIntervalSeconds, Some (sprintf "git_sync.interval_seconds: %d is below the minimum of %d; using %d" n MinIntervalSeconds MinIntervalSeconds)
            | RawInt n when n >= int64 MinIntervalSeconds && n <= int64 MaxIntervalSeconds -> int n, None
            | RawInt n ->
                DefaultIntervalSeconds, Some (sprintf "git_sync.interval_seconds: %d is outside 0 or %d to %d; using %d" n MinIntervalSeconds MaxIntervalSeconds DefaultIntervalSeconds)
            | other ->
                DefaultIntervalSeconds, Some (sprintf "git_sync.interval_seconds: expected an integer, got %s; using %d" (described other) DefaultIntervalSeconds)
        let sessionStart, sessionStartWarn =
            match raw.SessionStartRaw with
            | RawAbsent -> Blocking, None
            | RawStr "blocking" -> Blocking, None
            | RawStr "background" -> Background, None
            | RawStr "off" -> Off, None
            | RawStr _ -> Blocking, Some "git_sync.session_start: not one of \"blocking\", \"background\", \"off\"; using \"blocking\""
            | other -> Blocking, Some (sprintf "git_sync.session_start: expected a string, got %s; using \"blocking\"" (described other))
        let soft, softProblem, softValid = intKey 1 MaxSeconds DefaultSoftSeconds raw.SoftRaw
        let maxSeconds, maxProblem, _ = intKey 1 MaxSeconds DefaultMaxSeconds raw.MaxRaw
        let stall, stallProblem, _ = intKey 1 MaxSeconds DefaultStallSeconds raw.StallRaw
        let sessionEnd, sessionEndWarn =
            match raw.SessionEndRaw with
            | RawAbsent -> true, None
            | RawBool b -> b, None
            | other -> true, Some (sprintf "git_sync.session_end: expected a boolean, got %s; using true" (described other))
        let endSeconds, endProblem, _ = intKey 1 MaxSeconds DefaultSessionEndSeconds raw.SessionEndSecondsRaw
        let guard, guardProblem, _ = intKey 0 100 DefaultDeleteGuardPercent raw.DeleteGuardRaw
        // the soft limit never exceeds the hard one; the warning is given only for a valid explicit soft, and a
        // bad soft that is then lowered names the value finally used
        let finalSoft = if soft > maxSeconds then maxSeconds else soft
        let softWarn =
            if softValid && soft > maxSeconds then
                Some (sprintf "git_sync.session_start_soft_seconds: %d is above session_start_max_seconds; using %d" soft maxSeconds)
            else warn "session_start_soft_seconds" softProblem finalSoft
        let warnings =
            [ remoteWarn; branchWarn; intervalWarn; sessionStartWarn; softWarn
              warn "session_start_max_seconds" maxProblem maxSeconds
              warn "session_start_stall_seconds" stallProblem stall
              sessionEndWarn
              warn "session_end_seconds" endProblem endSeconds
              warn "delete_guard_percent" guardProblem guard ]
            |> List.choose id
        { Remote = remote; Branch = branch; IntervalSeconds = interval; SessionStart = sessionStart
          SessionStartSoftSeconds = finalSoft; SessionStartMaxSeconds = maxSeconds; SessionStartStallSeconds = stall
          SessionEnd = sessionEnd; SessionEndSeconds = endSeconds; DeleteGuardPercent = guard
          Warnings = warnings }
```


Then wire the three construction sites: the loader passes `FSharpOption<Core.Config.GitSyncConfig>.None` for now (Task 3 replaces it), the two tests add the new last field. Add `GitSyncConfigTests.fs` to the test fsproj before `Program.fs`.

- [ ] **Step 4: Run until green** (the whole Core project, then `dotnet build src/TotalRecall.sln` for the three construction sites). Reviewer gate. Commit: `Add the git sync settings and their normalisation`.

---

### Task 3: The loader and the shipped example

**Files:** Modify `src/TotalRecall.Infrastructure/Config/ConfigLoader.cs`, `src/TotalRecall.Infrastructure/Config/defaults.toml`, `tests/TotalRecall.Infrastructure.Tests/ConfigLoaderTests.cs`.

- [ ] **Step 1: Write the failing tests** in `ConfigLoaderTests` (they write a user `config.toml` to the per-test temp dir and call `LoadEffectiveConfig(path)`):
  - no `[git_sync]` → `cfg.GitSync` is `None` (and `LoadDefaults().GitSync` is `None`: the shipped block is commented);
  - only `remote = "git@github.com:alice/x.git"` → `Some`, `Remote = Some …`, `Branch = Some "main"`, `IntervalSeconds = 300`, `SessionStart = Blocking`, 10/20/8, `SessionEnd = true`, 8, 20, `Warnings` empty;
  - every key set (`branch = "dev"`, `interval_seconds = 60`, `session_start = "off"`, `session_start_soft_seconds = 5`, `session_start_max_seconds = 15`, `session_start_stall_seconds = 3`, `session_end = false`, `session_end_seconds = 12`, `delete_guard_percent = 50`) → each as written;
  - wrong types and bad values load with warnings, no throw: `interval_seconds = "five"`, `session_start = 3`, `session_end = "yes"`, `delete_guard_percent = 1.5` (float), `branch = ["a"]`, `remote = 7`, `session_start_soft_seconds = [1]` → defaults / `None` and a warning naming each key (assert on `Warnings` containing the key text); an unknown key is ignored with no warning;
  - `git_sync = "x"` (not a table) → `GitSync = None`, load succeeds;
  - `interval_seconds = 0` → 0 and no warning; `interval_seconds = 10` → 30 and one warning;
  - a TOML syntax error inside `[git_sync]` (`remote = "unterminated`) still throws `InvalidDataException` (as for any section); an overflowing literal (`interval_seconds = 99999999999999999999`) throws too (pins Decided 4);
  - a user file that overrides `[tiers.hot] max_entries` and also has `[git_sync]` loads both;
  - the user's `[git_sync]` table survives `LoadEffectiveTable` untouched (the merge keeps unknown tables);
  - **snapshot unchanged:** `ConfigJsonSerializer`'s JSON of `LoadEffectiveConfig` for a file with a `[git_sync]` section equals that of the same file without it (use the public serializer entry the existing snapshot tests use).

- [ ] **Step 2: Run, confirm they fail.**

- [ ] **Step 3: Implement.** In `ConfigLoader.Project`, before the final `return`:

```csharp
        var gitSync = ProjectGitSync(table);
```

pass `gitSync` as the new last constructor argument, and add:

```csharp
    // [git_sync] is optional: Some only when the table exists. Every value is handed to the pure normaliser
    // as a raw value, so nothing inside the section can make the load fail (a TOML syntax error already did, in
    // ParseToml, before this runs).
    private static FSharpOption<Core.Config.GitSyncConfig> ProjectGitSync(TomlTable table)
    {
        if (!table.TryGetValue("git_sync", out var obj) || obj is not TomlTable t)
            return FSharpOption<Core.Config.GitSyncConfig>.None;

        var raw = new Core.Config.RawGitSync(
            GitRaw(t, "remote"),
            GitRaw(t, "branch"),
            GitRaw(t, "interval_seconds"),
            GitRaw(t, "session_start"),
            GitRaw(t, "session_start_soft_seconds"),
            GitRaw(t, "session_start_max_seconds"),
            GitRaw(t, "session_start_stall_seconds"),
            GitRaw(t, "session_end"),
            GitRaw(t, "session_end_seconds"),
            GitRaw(t, "delete_guard_percent"));
        return FSharpOption<Core.Config.GitSyncConfig>.Some(Core.Config.GitSyncSettings.normalise(raw));
    }

    private static Core.Config.GitRawValue GitRaw(TomlTable t, string key)
    {
        if (!t.TryGetValue(key, out var v))
            return Core.Config.GitRawValue.RawAbsent;
        return v switch
        {
            string s => Core.Config.GitRawValue.NewRawStr(s),
            long l => Core.Config.GitRawValue.NewRawInt(l),
            bool b => Core.Config.GitRawValue.NewRawBool(b),
            double => Core.Config.GitRawValue.NewRawBad("a float"),
            TomlTable => Core.Config.GitRawValue.NewRawBad("a table"),
            TomlArray or TomlTableArray => Core.Config.GitRawValue.NewRawBad("an array"),
            _ => Core.Config.GitRawValue.NewRawBad("a value of another type"),
        };
    }
```

  and `defaults.toml`: next to the `[cortex]` block add a commented documentation block:

```toml
# --- Git sync (optional; sync verbs are not available yet) ---
# Syncs memories across your machines through a private git repository you own.
# [git_sync]
# remote = "git@github.com:you/total-recall-sync.git"
# branch = "main"
# interval_seconds = 300            # 0 disables the background timer; otherwise 30 to 86400
# session_start = "blocking"        # "blocking" | "background" | "off"
# session_start_soft_seconds = 10
# session_start_max_seconds = 20
# session_start_stall_seconds = 8
# session_end = true
# session_end_seconds = 8
# delete_guard_percent = 20
```

- [ ] **Step 4: Run until green;** the whole Infrastructure project (unfiltered, including `Integration`-trait tests, once). Reviewer gate. Commit: `Read the git sync settings from config.toml`.

---

### Task 4: Documentation, AOT and the full verification

**Files:** Modify `src/TotalRecall.Infrastructure/AGENTS.md`, `src/TotalRecall.Core/AGENTS.md`, `AGENTS.md`.

- [ ] **Step 1: Docs.** Infrastructure `AGENTS.md`: fix "16 sequential migrations" and add the missing row for migration 17 (`Migration17_HotStickyColumn`: the `sticky` column on the hot tables, tier model v2) and a row for migration 18 (the four `git_sync_*` tables and the `import_log(target_entry_id)` index; tables only, nothing uses them until 1b). Core `AGENTS.md`: the `Config.fs` row mentions `GitSyncConfig` and `GitSyncSettings` (the `[git_sync]` settings, normalised by a pure function with warnings). Root `AGENTS.md` "Database Migrations": "currently 16" → 18, and the schema-test step says new schema tests go in a non-`Integration` test file so the standard verify runs them. No CHANGELOG change.
- [ ] **Step 2: AOT.** `dotnet publish src/TotalRecall.Host/TotalRecall.Host.csproj -c Release -r linux-x64 -p:PublishAot=true` shows no new warning.
- [ ] **Step 3: Verify.** `node skills/develop/scripts/profile.js verify-files .github/develop.md <dir>`, then each block with `sh -e`; additionally run the full unfiltered Infrastructure suite once (`dotnet test tests/TotalRecall.Infrastructure.Tests`), since `SchemaTests` is `Integration`-trait.
- [ ] **Step 4:** record each acceptance criterion's result for the PR body. Commit: `Document the git sync tables and settings`.
