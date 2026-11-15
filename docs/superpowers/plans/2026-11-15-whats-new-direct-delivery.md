# What's-new notice: direct delivery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** pi and Claude Code show the what's-new notice directly; the shim tells the engine which harness is calling `session_start`; every direct channel leaves a 30-day marker so the model-relayed line is withheld; the announce text of the other hosts tells the model to relay `whatsNew` (issue #106, ticket T2b of the what's-new notice).

**Architecture:** A new C# command `whats-new` (Cli project) decides with the already merged `WhatsNewDecider` and prints text, a JSON object or a Claude Code `systemMessage` envelope. A new plain-ESM helper `pi/whatsNew.js` writes the direct-delivery stamp and runs the provisioned engine with a deadline; the Claude Code SessionStart hook (`hooks/session-start/run.sh` plus a node script) and the pi extension both use it. The shim remembers the harness's `clientInfo.name` and adds it as `host` to proxied `session_start` calls.

**Tech Stack:** .NET 10 / C# (xUnit 2.9.2, NativeAOT, no reflection JSON), plain ESM JavaScript (`node:test`, zero dependencies), bash.

**Spec:** `docs/superpowers/specs/2026-11-15-whats-new-notice-design.md` (read-only for this run; sections 3 and 4 incl. "Build decisions for the direct runners (T2b)", Accepted losses). The engine side it builds on is merged (T2a, #99): `src/TotalRecall.Infrastructure/WhatsNew/*` and `SessionStartHandler`.

## Global Constraints

- Plugin-wide: no commit trailers or AI attribution in commits; commit messages are short plain sentences. New JS is plain ESM (`package.json` has `"type": "module"`), zero dependencies, `node:test` + `node:assert/strict`. New C# is AOT-safe (no reflection JSON; use `TotalRecall.Infrastructure.Json.JsonWriter`). Tests never set `TOTAL_RECALL_HOME`/`HOME` for the process and never touch the real `~/.total-recall`: every test that needs a data directory passes a temporary one (`fs.mkdtempSync(path.join(os.tmpdir(), "tr-..."))` / `Path.Combine(Path.GetTempPath(), "tr-..." + Guid.NewGuid().ToString("N"))`) and removes it.
- Host ids (closed list, `HostIds.Known`): `claude-code, pi, copilot-cli, cursor, codex, gemini-cli, kiro, opencode, hermes, cline, windsurf, zed, vscode, other`. The JS side only ever writes stamps for `claude-code` and `pi`.
- State directory: `<dataDir>/whats-new/` where `<dataDir>` is `TOTAL_RECALL_HOME` if non-empty, else `<HOME or USERPROFILE>/.total-recall` (C#: `WhatsNewState.DefaultDir()`; JS: `resolveDataDir(env)` in `scripts/fetch-binary.js`, which returns null when it cannot resolve an absolute path). Files: `seen-<host>.json` = `{"version":"<bare>"}` (C# only), `direct-<host>.json` = `{"at":"<ISO-8601 UTC>"}` (JS writes, C# `WhatsNewState.IsDirectStampFresh` reads: fresh when at most 30 days old and not more than a day in the future).
- Notice line (from `WhatsNewRenderer.Line`) is one line of plain text, at most about 300 characters, sanitised by the engine; the JS side treats engine output as untrusted text: it never executes it and passes it only to `ctx.ui.notify` (pi) or `JSON.stringify` (Claude Code).
- Engine command contract (Task 1), used by Tasks 2, 4 and 5: `total-recall whats-new --host <id> [--format text|json|claude-code] [--no-mark]`; always exit 0; `text` prints the line plus newline or nothing; `json` prints `{"kind":"first-seen"|"updated","text":"<line>"}` or `{}`; `claude-code` prints `{"systemMessage":"<line>"}` or `{}`; an unknown `--format` behaves as `text`; `--host` is normalised with `HostIds.Normalize` (missing/unknown -> `other`) and overrides `TOTAL_RECALL_HOST`; the version is recorded (`WhatsNewState.WriteSeenVersion(dir, host, notice.Version)`) only after the output was written and not with `--no-mark`.
- Time budget of the Claude Code hook: its node script has one deadline of 2.5 s from its own start (stdin read at most 500 ms, the engine gets the rest and is killed at the deadline); `run.sh` additionally wraps the script in `timeout 4` when `timeout` exists. A hung engine must cost under 3.5 s end to end. Any failure leaves `run.sh`'s output byte-for-byte what it was before this ticket.
- Direct wins: each direct runner writes its stamp FIRST, before it looks for an engine, and writes nothing when the data directory cannot be resolved.
- The existing example status line `> total-recall loaded — ...` in the skills is pinned by `scripts/host-docs.test.js`; do not change it.
- Existing suites, the `catalog.json` drift guard (this ticket changes no MCP tool) and `npm run test:shim` keep passing. New JS test files under `bin/shim/`, `pi/` run automatically; `hooks/session-start/whats-new.test.js` must be added to `SHIM_TEST_TARGETS` in `scripts/run-shim-tests.js`.

## Review Focus

- Existing Claude Code SessionStart output must stay byte-identical when anything fails, and identical for the Cursor and Copilot branches (the instructions ride in the same JSON object).
- A hostile or odd notice line (quotes, backslashes, `%`, newline, U+2028, very long) must never produce invalid JSON in `run.sh` output or an exception in pi.
- Hook and pi paths must never throw into the host and never block past the deadline; the pi handler must not await the engine.
- The shim must rewrite only `session_start` calls and never mutate the harness's original message object; arrays and non-objects in `arguments` are forwarded unchanged.
- `--host` must beat `TOTAL_RECALL_HOST`; the command must not apply the stamp or Hermes rules.
- Marking only after a successful write; `compact` runs stamp but never call the engine.

---

### Task 1: The `whats-new` command

**Files:**
- Create: `src/TotalRecall.Cli/Commands/WhatsNewCommand.cs`, `tests/TotalRecall.Cli.Tests/Commands/WhatsNewCommandTests.cs`
- Modify: `src/TotalRecall.Cli/CliApp.cs` (register `new Commands.WhatsNewCommand()` beside `PinnedFloorCommand`; expose the app version to commands through a small `internal static` accessor over the existing private version resolution)
- Test (extra, Infrastructure): `tests/TotalRecall.Infrastructure.Tests/WhatsNew/WhatsNewStateTests.cs` (one added case)

**Interfaces:**
- Produces: `public sealed class WhatsNewCommand : ICliCommand` with `Name => "whats-new"`, `Group => null`, a description, a parameterless production constructor (version from the `CliApp` accessor, `WhatsNewState.DefaultDir()`, `AppContext.BaseDirectory`, `ConfigLoader.LoadUpdatesNoticeEnabled()`, `Console.Out`) and a public test constructor `(string version, string stateDir, string binaryDir, Func<bool> noticeEnabled, TextWriter output)`. Model on `PinnedFloorCommand` (hand-parsed `--host`, fail-safe `try/catch` printing the format's empty output, exit 0). `RunAsync(string[] args)` returns `Task<int>`; flags: `--host <id>`, `--format <f>`, `--no-mark`; a flag with no value is ignored.
- Consumes (merged, public): `HostIds.Normalize`, `WhatsNewState.ReadSeenVersion/WriteSeenVersion/DefaultDir`, `PackagedReleaseNotes.Read`, `WhatsNewDecider.Decide`, `WhatsNewRenderer.Line/KindName`, `ConfigLoader.LoadUpdatesNoticeEnabled`, `JsonWriter`. It must NOT use `WhatsNewService.Check` (that applies Hermes, stamp and `TOTAL_RECALL_HOST`).

- [ ] **Step 1: Write the failing tests** (temp dirs for state and notes; a notes file written with `release-notes.json` keys `version, previous, summary, sections, url, releasesUrl`; capture output with a `StringWriter`): first run for `--host pi` prints the first-seen line (`total-recall <v>: <summary> All releases: ...`) and writes `seen-pi.json` = `{"version":"<v>"}`, second run prints nothing; seen `4.7.0`, running `4.8.0`, notes `previous` `4.7.0` prints `total-recall updated 4.7.0 -> 4.8.0: <summary> More: <release page>`; `--format json` prints `{"kind":"updated","text":"..."}` that parses and whose `text` equals the text-format line; `--format claude-code` prints `{"systemMessage":"..."}`; no notice: `text` prints nothing, `json` and `claude-code` print `{}`; `--format bogus` behaves as `text`; `--no-mark` prints and writes no seen file, so the next run prints again; dev build (`1.0.0`, `unknown`) prints nothing and writes nothing; `noticeEnabled` false prints nothing and writes nothing; unreadable seen file (garbage JSON, a directory with the file's name) counts as never told (first-seen line); missing notes file or notes of another version: the kind still follows the seen version and the line has the version and no summary (`total-recall <v>: All releases: <url>` / `total-recall updated <from> -> <v>: ...`); `--host ../x`, `--host ""`, no `--host`, `--host nonsense` all behave as `other` (a `seen-other.json` is written inside the state dir and nothing outside it); a writer that throws on write: nothing recorded, exit 0, no exception; a `TOTAL_RECALL_HOST`-style environment host is ignored (the test constructor takes no env; assert `--host claude-code` records `seen-claude-code.json` even when `seen-hermes.json` exists); a fresh `direct-claude-code.json` stamp and `--host hermes` do not suppress the notice; JSON output escapes quotes/backslashes/control characters in `text` (a summary with `"` and `\` in the notes file round-trips through `JsonDocument`); the registry contains the command (`CliApp` help lists `whats-new`). Contract case in the Infrastructure `WhatsNewStateTests`: a stamp written exactly as JavaScript writes it, `{"at":"2026-11-15T20:24:56.123Z"}` (milliseconds, `Z`), is read as fresh at that time plus 29 days and stale at plus 31 days.
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Cli.Tests/TotalRecall.Cli.Tests.csproj --filter "FullyQualifiedName~WhatsNewCommand"` and the Infrastructure case — Expected: FAIL (command missing; the Infrastructure contract case may already pass: say so in the report).
- [ ] **Step 3: Implement** the command and the registration. Order inside `Execute`: parse, decide, render to a string, write it to the output (flush), and only then record unless `--no-mark`. Any exception: print the format's empty output (`""` for `text`, `{}` for the JSON formats) inside its own try, return 0.
- [ ] **Step 4: Run** the focused tests, then `dotnet test tests/TotalRecall.Cli.Tests/TotalRecall.Cli.Tests.csproj --filter "Category!=Integration"` and `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~WhatsNew"` — Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Add the whats-new command"`

---

### Task 2: The shared JS helper: direct-delivery stamp and engine runner

**Files:**
- Create: `pi/whatsNew.js`, `pi/whatsNew.test.js`
- Do not change: `pi/floor.js`

**Interfaces:**
- Consumes: `resolveDataDir` from `scripts/fetch-binary.js`; the engine contract of Global Constraints.
- Produces (named exports, plain ESM, no top-level side effects):
  - `writeDirectStamp({ dataDir, host, now = () => new Date(), fsApi = fs }) -> boolean`: synchronous; returns false and writes nothing when `dataDir` is not a non-empty absolute string or `host` does not match `^[a-z][a-z-]{0,20}$`; otherwise creates `<dataDir>/whats-new` (recursive), writes `direct-<host>.json` with content `{"at":"<now().toISOString()>"}` through a unique temporary file in the same directory (`direct-<host>.json.<random>.tmp`) and a rename, deletes the temporary file in a `finally`, swallows every error (returns false on error), returns true on success.
  - `runWhatsNew({ binaryPath, host, format, env = {}, timeoutMs = 3000, noMark = false, spawn = child_process.spawn }) -> Promise<string | undefined>`: spawns `binaryPath` with args `["whats-new","--host",host,"--format",format]` (plus `"--no-mark"` when `noMark`), `stdio: ["ignore","pipe","ignore"]`, `windowsHide: true`, `env: { ...process.env, ...env }` (a value of `undefined` removes the variable), collects stdout capped at 8 KiB, kills the child at `timeoutMs`, resolves with the trimmed stdout only when the child exited with code 0 and the output is non-empty, otherwise resolves `undefined`; never rejects (spawn errors, timeouts, non-zero exit, `error` events all resolve `undefined`); the timer is always cleared.
  - `KNOWN_DIRECT_HOSTS = ["claude-code", "pi"]` (documentation constant used by callers' tests).

- [ ] **Step 1: Write the failing tests** (`node:test`; inject `fsApi`, `spawn` fakes; real temp dirs for the stamp): stamp written with the injected clock to `<tmp>/whats-new/direct-pi.json` and parses to exactly `{at: "<iso>"}`; the `whats-new` directory is created; `dataDir` null/relative/empty returns false and creates nothing; bad hosts (`../x`, `A`, `""`, a 40-character string) return false and write nothing; a stamp over an existing stamp replaces it; a failing write (data dir is a file, read-only dir, `renameSync` throwing) returns false, throws nothing and leaves no `.tmp` file; two interleaved writers never leave a partial file (alternate calls, assert the file always parses); runner: passes exactly the documented args (with and without `noMark`), merges `env` over `process.env` with `undefined` removal, resolves trimmed stdout on exit 0, resolves `undefined` on non-zero exit, on empty output, on a spawn `error` event, on a synchronous `spawn` throw, and on timeout (use a fake child that never exits and `timeoutMs: 20`: the child is killed and the promise resolves `undefined` within a small multiple of the timeout); stdout over 8 KiB is truncated without crashing; the promise resolves once even when both `error` and `exit` fire.
- [ ] **Step 2: Run** `node --test pi/whatsNew.test.js` — Expected: FAIL (module missing).
- [ ] **Step 3: Implement** the module (model the spawn handling on `runFloor` in `pi/floor.js` but do not import or change it).
- [ ] **Step 4: Run** `node --test pi/whatsNew.test.js` and `npm run test:shim` — Expected: PASS.
- [ ] **Step 5: Commit** `git add pi && git commit -m "Add the direct-delivery stamp and engine runner for the notice"`

---

### Task 3: The shim tells the engine which harness is calling

**Files:**
- Modify: `bin/shim/server-surface.js`, `bin/shim/orchestrator.js`, `bin/shim/fixtures/fake-engine.js` (a mode that echoes `tools/call` params), tests in `bin/shim/server-surface.test.js`, `bin/shim/engine-proxy.test.js` and/or `bin/shim/orchestrator.test.js`

**Interfaces:**
- Produces: `ServerSurface` keeps `this._clientName` (a non-empty string from `msg.params.clientInfo.name` of the harness's `initialize`, trimmed to at most 128 characters; anything else leaves the previous value, initially none). `route(msg, state)` for a `tools/call` of `params.name === "session_start"` while `state.ready` returns `{ kind: "proxy", message: <copy> }` where `<copy>` is a new object (the original `msg` is never mutated) whose `params.arguments` is: `{ host: <name> }` when `arguments` is absent or null; `{ ...arguments, host: <name> }` (replacing any caller `host`) when it is a plain object; unchanged when it is any other value (array, string, number); the whole message is forwarded unchanged when no name is known. Every other method and tool still returns `{ kind: "proxy" }` with no `message` (or the original). `orchestrator.js` forwards `decision.message ?? msg` (the existing line `proxy.send(msg)` in the `'proxy'` case).
- Consumes: nothing new. The catalog (`catalog.json`, `tools/list`) must not change.

- [ ] **Step 1: Write the failing tests**: `initialize` with `clientInfo.name "claude-code"` then a `session_start` call routes to a proxy decision whose message has `params.arguments.host === "claude-code"` and the same `id`/`method`/`jsonrpc`, while the original message object is unchanged (deep-equal to a snapshot taken before); with caller `arguments: {host: "evil", task: "x"}` the result is `{host: "claude-code", task: "x"}`; `arguments` absent and `null` become `{host: ...}`; `arguments: []`, `"x"`, `5` forwarded unchanged; a non-`session_start` tool (`memory_search`) is forwarded with no host added; `tools/list`/`ping` untouched; `initialize` without params, with `clientInfo: {}`, with a non-string/empty name: no host added to later `session_start` calls; the name survives later `initialize` calls that carry no name; a 300-character name is cut to 128; before the engine is ready (`state` not proxying) the existing not-ready answer is unchanged; an end-to-end orchestrator or engine-proxy test with the fake engine in echo mode proves the engine receives `host` for `session_start` and not for another tool; a name of `hermes-mcp-client` is forwarded verbatim (the engine maps it); existing tests unchanged and passing (`out.kind === "proxy"`).
- [ ] **Step 2: Run** `node --test bin/shim/` — Expected: FAIL on the new tests.
- [ ] **Step 3: Implement** the change in `ServerSurface.route` and the orchestrator forward; extend `fake-engine.js` with an opt-in echo mode (env var or argv switch) that returns the received `params` as the tool result text, leaving its default behaviour untouched.
- [ ] **Step 4: Run** `node --test bin/shim/` and `npm run test:shim` — Expected: PASS.
- [ ] **Step 5: Commit** `git add bin && git commit -m "Pass the harness's client name to session_start through the shim"`

---

### Task 4: The Claude Code SessionStart hook shows the notice

**Files:**
- Create: `hooks/session-start/whats-new.js`, `hooks/session-start/whats-new.test.js`
- Modify: `hooks/session-start/run.sh` (the Claude Code branch only), `scripts/run-shim-tests.js` (add `'hooks/session-start/whats-new.test.js'` to `SHIM_TEST_TARGETS`, in the same style as the other single-file entries)

**Interfaces:**
- Consumes: `writeDirectStamp`, `runWhatsNew` (Task 2), `resolveEngine` from `pi/floor.js`, `resolveDataDir` from `scripts/fetch-binary.js`. Both imported inside a try so a broken file can never fail the hook (pattern: `codex/hook.js`).
- Produces: `whats-new.js` exports `run(deps) -> Promise<string>` returning either a one-line JSON string literal (`JSON.stringify(line)`) or `""`; `deps` = `{ stdin, env, now, resolveEngine, resolveDataDir, writeDirectStamp, runWhatsNew, deadlineMs = 2500, stdinCapMs = 500 }`, all injectable. Behaviour: the deadline starts at `run`'s first line; read stdin for at most `stdinCapMs` (a missing, empty, malformed or timed-out payload means `source = "startup"`); `source` is `payload.source` when it is a string; write the stamp for host `claude-code` first (data dir from `resolveDataDir(env)`), on EVERY source; return `""` for `source === "compact"`; otherwise `resolveEngine()` (return `""` when none), then `runWhatsNew({ binaryPath, host: "claude-code", format: "text", timeoutMs: <what is left of the deadline, at least 1> })`; return `JSON.stringify(line)` for a non-empty line (the node script uses `--format text`, so `run.sh` only receives a string literal) else `""`. Any exception returns `""`. `main()` (guarded like `codex/hook.js`: runs only when executed directly) prints the returned string with no newline and sets `process.exitCode = 0`.
- `run.sh` change (Claude Code branch, i.e. the `elif [ -n "${CLAUDE_PLUGIN_ROOT:-}" ] && [ -z "${COPILOT_CLI:-}" ]` branch only; the Cursor branch and the generic `else` branch stay byte-identical and never call the script): before printing, compute `notice` as the script's stdout using `timeout 4` when `command -v timeout` succeeds, `node "$PLUGIN_ROOT/hooks/session-start/whats-new.js"`, stderr to `/dev/null`, with `|| true` (the file runs under `set -euo pipefail`), stdin inherited; accept `notice` only when it starts and ends with `"`, has at least 3 characters and contains no newline or carriage return (a bash `case` pattern), else treat it as empty; when non-empty print the existing JSON with an additional `"systemMessage": %s,` member (the literal passed as a `%s` printf ARGUMENT, never inside the format string); when empty print exactly today's template. The generated JSON must stay valid in both cases.

- [ ] **Step 1: Write the failing tests** (`node:test`). Unit (injected deps): stamp written before `resolveEngine` is called (record call order) and on all four sources (`startup`, `resume`, `clear`, `compact`); `compact` never calls `runWhatsNew`; a payload that is empty, `not json`, `[]`, `{"source":5}` or never arrives (a stream that never ends: the read gives up after `stdinCapMs`) counts as `startup`; no data dir (`resolveDataDir` null) writes no stamp (`writeDirectStamp` is called with `dataDir: null` or not called, and returns false) but still runs the engine when present; no engine: returns `""` after the stamp; `runWhatsNew` returning `undefined`/`""` returns `""`; a line with `"`, `\`, `%`, a newline, `\u2028`, a very long string returns a literal that `JSON.parse` turns back into the same string; `runWhatsNew` receives `timeoutMs` no larger than the time left of the deadline (fake clock) and at least 1; the whole run never exceeds the deadline plus a small margin with an engine runner that never resolves (fake runner honouring `timeoutMs` and a never-ending stdin); every dependency throwing returns `""`. Process level (temporary copy of the plugin tree built in the test: `hooks/session-start/run.sh` + `whats-new.js`, `pi/floor.js`, `pi/whatsNew.js`, `scripts/fetch-binary.js` and whatever those import, a stub `skills/commands/SKILL.md`, a `package.json` with `"type":"module"`, and `binaries/<rid>/total-recall` as an executable `#!/usr/bin/env node` fake engine plus anything `resolveEngine`'s payload check requires; skip these cases on Windows; pass `TOTAL_RECALL_HOME` = a temp data dir and `CLAUDE_PLUGIN_ROOT` in the child environment only): with the fake engine printing a notice, `bash run.sh` with stdin `{"source":"startup"}` prints JSON that `JSON.parse` accepts, has `hookSpecificOutput.additionalContext` equal to what the no-notice run produces, and a `systemMessage` equal to the notice; the stamp `direct-claude-code.json` exists in the temp data dir; `source: "compact"` prints exactly the no-notice output and still refreshes the stamp; no engine binary: output byte-identical to the baseline (compute the baseline by running the ORIGINAL script text from `git show HEAD:hooks/session-start/run.sh`, or a golden string, in the same temp tree); a fake engine that sleeps forever: finishes in under 3.5 s, output byte-identical to the baseline, exit 0; a fake engine printing garbage / a control character / nothing: baseline output; `node` not on `PATH` (run with a `PATH` that has only a directory containing `bash`'s needs but no `node`): baseline output, exit 0; `CURSOR_PLUGIN_ROOT` set and `COPILOT_CLI` set: baseline output of those branches and no stamp file written; a hostile notice (`"`, `\`, `%s`, `%`) from the fake engine still yields valid JSON.
- [ ] **Step 2: Run** `node --test hooks/session-start/whats-new.test.js` — Expected: FAIL (module missing).
- [ ] **Step 3: Implement** `whats-new.js`, the `run.sh` Claude Code branch change and the `SHIM_TEST_TARGETS` entry.
- [ ] **Step 4: Run** `node --test hooks/session-start/whats-new.test.js`, `bash -n hooks/session-start/run.sh`, and `npm run test:shim` — Expected: PASS.
- [ ] **Step 5: Commit** `git add hooks scripts && git commit -m "Show the what's-new notice from the Claude Code SessionStart hook"`

---

### Task 5: The pi extension shows the notice and names the host

**Files:**
- Modify: `pi/extension.js`, `pi/extension.test.js`

**Interfaces:**
- Consumes: `writeDirectStamp`, `runWhatsNew` (Task 2) and the existing injected `resolveEngine`, `resolveUserServer`; `resolveDataDir` from `scripts/fetch-binary.js` (inject it too).
- Produces: `createExtension` gains injected deps `runWhatsNew`, `writeDirectStamp`, `resolveDataDir` (defaults wired in the default export beside the existing real deps). In the existing `session_start` handler, after the current `hookable` and reload logic that sets the queued context text, add the notice step: return early (no stamp, no run) unless `event.reason !== "reload"` and `ctx.hasUI === true` and `hookable(ctx)` and `resolveUserServer(ctx.cwd)` does not say `skip`; then compute `dataDir = resolveDataDir({ ...process.env, ...user.env })` and call `writeDirectStamp({ dataDir, host: "pi" })` synchronously (before any engine lookup); then `resolveEngine()` (no engine: stop, silence); then start `runWhatsNew({ binaryPath, host: "pi", format: "text", env: user.env, timeoutMs: 3000 })` WITHOUT awaiting it in the handler (the handler returns immediately) and, when it resolves with text, call `ctx.ui.notify(text, "info")` inside a `try/catch` that swallows everything (a stale `ctx` may throw). The returned promise is `.catch(() => {})`-guarded so it can never reject unhandled. The MCP entry built by `mcpServerConfig` gains `env: { TOTAL_RECALL_HOST: "pi" }` and still has no `cwd`.

- [ ] **Step 1: Write the failing tests** (extend the existing `boot()`/`ctx()` helpers; `ctx()` needs `hasUI: true` available as an option): with `hasUI`, hookable, not reload, engine present and a runner returning text, `notify` is called once with `(text, "info")` and the runner got `host: "pi"`, `format: "text"` and the followed server's env; the stamp is written for host `pi` with the data dir computed from `{...process.env, ...user.env}` BEFORE `resolveEngine` is called (call order recorded) and even when the engine is missing; the handler returns before a never-resolving runner resolves (startup is not delayed: assert the handler's return value/promise settles while the runner is pending); no notice, no stamp and no run for: `hasUI` false or absent, `reason === "reload"` (also when the context message was not delivered), a subagent child (`PI_SUBAGENT_ID`), `--no-session` (`getSessionFile()` undefined), `resolveUserServer` returning `skip: true`; unresolvable data dir (`resolveDataDir` null): `writeDirectStamp` receives null and writes nothing, the run still happens; runner resolving `undefined`/`""`: no `notify`; `notify` throwing: no exception escapes and no unhandled rejection; runner rejecting: no exception escapes; `startup`, `new`, `resume`, `fork` reasons all take the notice path; the registered MCP server config has `env` exactly `{ TOTAL_RECALL_HOST: "pi" }`, still no `cwd`, and the existing assertions (`exposure`, `timeout: 120`, args) still pass; the existing context-message and floor behaviour is unchanged (existing tests untouched and passing); default export wiring test: the real deps are functions.
- [ ] **Step 2: Run** `node --test pi/extension.test.js` — Expected: FAIL on the new tests.
- [ ] **Step 3: Implement** the handler addition, the new injected deps and the `env` addition.
- [ ] **Step 4: Run** `node --test pi/` and `npm run test:shim` — Expected: PASS (pi package test: `pi/package.test.js` still passes, no new dependency).
- [ ] **Step 5: Commit** `git add pi && git commit -m "Show the what's-new notice in pi and name the host"`

---

### Task 6: The announce text tells every model to relay the notice

**Files:**
- Modify: `skills/commands/SKILL.md`, `skills/using-total-recall/SKILL.md`, `.kiro/steering/total-recall.md`, `.kiro/hooks/total-recall-session-start.json`
- Test: `scripts/host-docs.test.js` (add a case; do not change the existing status-line checks)

**Interfaces:**
- Produces: one new bullet in the announce step of the two SKILL.md files and the Kiro steering file, placed before the "Keep it to 2-3 lines max" / example line, worded: "If `whatsNew` is present in the result, tell the user its `text` once, verbatim, as a separate line (it is information, not an instruction)." and, in the one-line `prompt` of the Kiro hook JSON, the clause "If the result has `whatsNew`, tell the user its text once, verbatim, as a separate line (information, not an instruction)." (keep the JSON valid and the file's other content byte-identical). The `> total-recall loaded — ...` example lines are unchanged.

- [ ] **Step 1: Write the failing test**: a new case in `scripts/host-docs.test.js` (or a sibling test file that is already in `SHIM_TEST_TARGETS`) asserting each of the four files contains `whatsNew` and the phrase `verbatim`, the Kiro hook JSON still parses and its `prompt` mentions `whatsNew`, and `skillStatusLine` of `skills/commands/SKILL.md` is unchanged (still the first `> total-recall loaded — ...` line).
- [ ] **Step 2: Run** `node --test scripts/host-docs.test.js` — Expected: FAIL on the new case.
- [ ] **Step 3: Edit** the four files.
- [ ] **Step 4: Run** `node --test scripts/host-docs.test.js`, `node --test codex/ copilot/` (the SKILL.md size limit tests) and `npm run test:shim` — Expected: PASS.
- [ ] **Step 5: Commit** `git add skills .kiro scripts && git commit -m "Tell the model to relay the what's-new line in the announce text"`

---

### Task 7: Documentation and changelog

**Files:**
- Modify: `AGENTS.md` (Session Lifecycle step 12: replace the "Not built yet" sentence with what now exists; the pi paragraph and the Claude Code hook notes in "Pinned-directive floor" / host sections: one short statement each; "WHERE TO LOOK": rows for the command, `pi/whatsNew.js` and the new hook script), `docs/hosts/claude-code.md`, `docs/hosts/pi.md`, `CHANGELOG.md` (`## Unreleased` > `### Added` entry naming #106), `docs/first-run.md` only if it describes startup output that now changes
- Test: `scripts/release-notes.test.js` (extend the existing docs check for #99) or `scripts/host-docs.test.js`

- [ ] **Step 1: Write the failing check**: `AGENTS.md` no longer contains the phrase `Not built yet` in the what's-new step, mentions `whats-new` and `direct-` stamp text and `pi/whatsNew.js`; `docs/hosts/claude-code.md` mentions the notice, `systemMessage` and a "Not observed" statement about whether Claude Code shows it and the client name it sends; `docs/hosts/pi.md` mentions the notice, `TOTAL_RECALL_HOST` and the client name `pi`; `CHANGELOG.md` contains `#106`. Existing host-doc drift rules keep passing.
- [ ] **Step 2: Run** `node --test scripts/release-notes.test.js scripts/host-docs.test.js` — Expected: FAIL.
- [ ] **Step 3: Edit** the documents, accurate to the code of Tasks 1 to 6 (read them): say plainly what is verified (pi's client name `pi` and `env` merge, read from pi's source) and what is not (Claude Code `systemMessage` display and client name; `claude -p`).
- [ ] **Step 4: Run** `node --test scripts/release-notes.test.js scripts/host-docs.test.js`, then the verify blocks of `.github/develop.md` once, as written there: `dotnet test src/TotalRecall.sln --filter "Category!=Integration"`, `npm run test:shim`, and the Release `--no-build` catalog diff — Expected: all pass (the worktree already has `npm ci` done and `models/bge-small-en-v1.5/` populated).
- [ ] **Step 5: Commit** `git add AGENTS.md docs CHANGELOG.md scripts && git commit -m "Document the direct what's-new notice"`
