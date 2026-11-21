# Transcript archive

> **Status: backup only.** The archive can be enabled, filled from your Claude Code sessions with `total-recall transcripts backfill`, inspected and emptied, but it cannot be searched yet. Nothing runs on its own: a session is stored only when you run `backfill`. Searching the archive, and other agent hosts, arrive in a later release. Until then the three `transcript_*` tools only report whether the archive is on.

## What it is

The transcript archive is an opt-in store for the dialogue of your agent sessions: your prompts and the assistant's replies, kept so that a later session can look back at what was said, not only at the memories that were distilled from it. It is separate from the memory tiers and the knowledge base: nothing in the archive is injected at session start, and nothing in it is a memory.

It is **off by default**. Only you can turn it on, from a terminal or by editing `config.toml`. An assistant cannot enable, disable or redirect it through `config_set`, the web UI or `config set` (see [Changing the settings](#the-transcripts-settings)).

## What is stored

Only while the archive is enabled, and only when you run `total-recall transcripts backfill`, it keeps:

- the dialogue of your Claude Code sessions (your prompts and the assistant's replies, with one-line stubs for tool calls and the error output of failed ones), split into chunks (see [What is kept and what is dropped](#what-is-kept-and-what-is-dropped));
- a little metadata about each session: the host and its session id, the project (its key, and the repository directory the project is excluded under), the working directory, start and end times, a title (your first prompt, cut to 120 characters), whether it was `interactive` or `automated`, and the size, modification time and a fingerprint of the file it was read from, so that a second run can tell what changed.

How it is kept:

- **Locally.** The archive lives in a SQLite database on your machine: in the main database (`~/.total-recall/total-recall.db` by default) or, if you set `transcripts.db_path`, in a separate file.
- **With known secret shapes redacted.** Text is scrubbed of the secret shapes listed in [What is redacted and what is not](#what-is-redacted-and-what-is-not) before it is stored. Only those shapes are found: read that section, including its limits, before you rely on it.
- **Never synced by total-recall.** The archive is not sent to Cortex and is not part of git sync. Nothing that syncs reads or writes its tables. This is about total-recall's own sync: if the main database, or the `transcripts.db_path` file, sits in a folder that another service shares (Dropbox, iCloud, OneDrive), that service copies the archive, and `forget` cannot reach those copies.
- **Not removed when the host deletes its files.** Once a session is in the archive, deleting or losing the host's session file does not remove it. The archive keeps what the host loses; only `forget` removes it.

Enabling the archive creates empty tables and nothing else. Only `transcripts enable` creates those tables; a hand edit of `config.toml` does not. Nothing is read from a host's files until you run `backfill`.

## Privacy

- The archive is off until you run `total-recall transcripts enable` in a terminal and confirm. The confirmation says what will be stored, and that nothing is read until you run `backfill`.
- Nothing in the archive is embedded yet. When a later release embeds it, the text is embedded only by the local model, and never sent to a remote embedding service unless you set `allow_remote_embedding = true` (see [The `[transcripts]` settings](#the-transcripts-settings)).
- If the archive file is in a folder that another service shares, that service copies it; total-recall does not sync it, and deleting it with `forget` does not remove the service's copies.
- You can delete all of it, or one session or one project, at any time (see [Forgetting and unforgetting](#forgetting-and-unforgetting)), and keep it from being archived again.
- The archive is not supported with Postgres storage (`[storage] connection_string`, or `mode = "postgres"`): `enable` refuses, and the archive stays off.

See [PRIVACY.md](../PRIVACY.md) for everything total-recall stores and what can leave your machine.

## Enabling

```bash
total-recall transcripts enable
```

The command must be run in a terminal (standard input must not be redirected) and asks you to confirm. There is no flag to skip the question: a script, or a way to set it without a terminal, is a hand edit of `config.toml` (see below). Declining changes nothing.

On a yes it opens the archive (creating its tables), sets `transcripts.enabled = true` in `~/.total-recall/config.toml` and writes a marker file, `<data directory>/live/enabled`, that tells the tools (and, in a later release, the hosts' hooks) that the archive is on. The tools notice the change at once; no restart is needed. It then tells you to run `total-recall transcripts backfill` to back up your Claude Code sessions; enabling stores nothing by itself.

Editing `config.toml` by hand also works (`[transcripts]` `enabled = true`). The engine mirrors the setting into the marker file when it starts, so a hand edit takes effect at the next engine start. A hand edit creates no tables: only `transcripts enable` does, and until you run it `transcripts status` shows the archive as on with nothing stored, and `backfill` refuses (it says the tables do not exist).

To turn it off:

```bash
total-recall transcripts disable
```

`disable` needs no terminal. It sets `enabled = false` and removes the marker file. **What the archive holds is kept**; use `transcripts forget --all` to delete it.

## Backing up Claude Code sessions

```bash
total-recall transcripts backfill [--host claude-code]
```

`backfill` reads the session files Claude Code keeps on your machine and stores what the archive keeps of them. `claude-code` is the default and, today, the only host; any other `--host` value is a usage error.

- **No terminal and no confirmation.** It needs neither, so a script or `cron` can run it. It refuses unless the archive is on: while `[transcripts] enabled` is false it prints how to enable it, exits 1 and has not read anything from Claude Code's directory. It also refuses under Postgres storage, and when the archive tables do not exist (a hand edit of `config.toml` that never ran `enable`), where it says that `total-recall transcripts enable` creates them.
- **Where the files are.** `$CLAUDE_CONFIG_DIR/projects` when `CLAUDE_CONFIG_DIR` is set and not empty, else `~/.claude/projects`. Only files of the form `<projects>/<project directory>/<session id>.jsonl` are read, one directory level down. The projects directory itself may be a symbolic link; symbolic links beneath it are not followed. The files under `<session>/subagents/` (subagent runs) are not read, and neither is a file whose name, without `.jsonl`, is not made of letters, digits, `.`, `_` and `-` (at most 128 characters). When Claude Code has no such directory, it prints `no Claude Code sessions found at <path>` and exits 0.
- **Order and interruption.** Newest file first. It prints a progress line every 50 sessions and a summary at the end. Ctrl+C stops (the session being read is left for the next run) and prints the summary so far with an `interrupted: the remaining sessions were not processed` line; the exit code is that of the sessions done. A second Ctrl+C ends the process at once, with no summary.
- **Safe to repeat.** A second run stores nothing new. A session that grew since is read again from the start of its last exchange (see [How resume works](#how-resume-works)). Deleting a session file afterwards leaves what was stored.
- **What stays out.** A session you forgot, a session of a project you forgot, and everything before the cutoff of `forget --all` are not stored, even though their files exist (see [Forgetting and unforgetting](#forgetting-and-unforgetting)).

The summary:

```
backfill claude-code: 120 sessions seen
  new 118, updated 0, unchanged 0, excluded 2, empty 0, skipped 0, changed 0, retry 0, failed 0
  chunks: new 4000, changed 0, unchanged 0, removed 0
```

| Count | Meaning |
|---|---|
| `new`, `updated`, `unchanged` | Sessions stored for the first time, stored again because they grew, or the cutoff or their project key changed, and left as they were. |
| `excluded` | Left out because you forgot them (the session, its project, or everything before the cutoff). |
| `empty` | Nothing in the file is kept (only tool output, hook notices and the like); nothing is stored. It is looked at again on the next run. |
| `skipped` | No complete first line yet (a session being written), a file name that is not a valid session id, or a session id that appears in two project directories (the other copy is skipped). |
| `changed` | The host replaced or shortened the file. It is **kept as it was stored** and not read again; the summary names each one as `host:id`. |
| `retry` | The session changed while this run worked on it (another run, or a `forget`), twice in a row; left for the next run. |
| `failed` | An error on that file (logged). The other files are still stored. |

Further lines: `unreadable lines: N` when some lines could not be read (a line that is not valid JSON, not an object, or over 16 MiB is skipped and counted, and never fails the file), and `interrupted: ...` after Ctrl+C. Exit codes: 0 when no file failed, 1 when any file failed or the archive cannot be used, 2 for a usage error.

**A `changed` session.** The archive keeps what the host loses, so a file that was replaced or truncated does not overwrite what was stored. If you want the new version instead, the stored one has to go: run `total-recall transcripts forget --session claude-code:<id>`, then `total-recall transcripts unforget --session claude-code:<id>`, then `backfill` again. Both versions cannot be kept, and the old chunks are lost. (A replacement that is larger than the old file, starts with the same line and happens to leave an old offset on a line start is not detected: it is read on from the old offset. Claude Code appends to its files and was not seen to rewrite them.)

`transcripts status` shows the totals and, under them, one line per project and kind (at most 30 lines, then `and N more`).

## What is kept and what is dropped

Kept:

- **Your text**: your prompts, including a prompt you typed while the agent was still working, and a slash command as plain text (`/develop 148`).
- **The assistant's text.** Two tool inputs are the assistant's own answer and are stored as assistant text, not as stubs: `StructuredOutput` (the whole answer of an SDK run, as `key: value` lines) and `ExitPlanMode` (its plan).
- **One-line tool stubs**: `[Bash: git status]`, `[Read: /path/file]`, ... one field of the tool's input chosen by tool name (a command, a file path, a pattern, a URL, a query, a description, or the first string field), folded to one line and cut to **160** characters (the tool name is cut to 64). A command you ran in the shell mode (`!`) is a stub too. For `AskUserQuestion` the stub holds the text of its first question; **the choice you made lives in the tool's result, which is dropped, so your answer to that question is not kept.** This is a known loss.
- **The error output of a failed tool call**: `[error: ...]`, cut to **500** characters, line breaks kept (a stack trace is what you will look for).
- **Compaction summaries**, as their own `compaction-summary` chunks.

Dropped:

- the output of tool calls that succeeded (file contents, command output);
- the assistant's thinking, images and any other block that is not text or a tool call;
- records the host adds to the file for itself: meta records, the work of sidechains and subagents, the results of background tasks and subagents that the host injects as a user message, and every record type that is not a user, assistant, compaction or queued-prompt record;
- inside your text: `<system-reminder>`, `<task-notification>`, the local command output and caveat blocks (`<local-command-stdout>`, `<local-command-stderr>`, `<local-command-caveat>`), `<command-message>`, `<bash-stdout>`, `<bash-stderr>`, `<EXTREMELY_IMPORTANT>` and the IDE context blocks (`<ide_...>`), with their tags, and a message that is only a host interruption notice (`[Request interrupted by user ...]`). The assistant's text is kept as written, even when it mentions these tags.

Limits. Every text is cut at **100,000** characters after redaction, with `[... N more characters not stored]` appended, so a pasted log cannot turn one prompt into thousands of chunks. A chunk's body is at most **1,800** characters: a longer exchange is split into parts, and every part after the first starts with your question again (cut to 300 characters). Each chunk begins with one header line (host, the first 8 characters of the session id, the project key, the date, the kind, the chunk type and the exchange number). A "character" is a UTF-16 code unit.

## What is redacted and what is not

Before anything is cut or split, every text that is stored (your text, the assistant's text, stub details and tool names, error text, compaction summaries and the session title) is scanned, and what is found is replaced by a marker, `[redacted:<category>]`. The text of the secret is not stored, and no part of it is left behind. Two things hold for every marker: an overlap of two findings becomes one marker, and a cut to 160, 500 or any other limit never lands inside a marker (it goes before the marker and adds `...`, or the not-stored note at the 100,000 cap, so a token that straddles the limit leaves no prefix).

| Marker | What is found | What is replaced |
|---|---|---|
| `[redacted:token]` | GitHub tokens (`ghp_`, `gho_`, `ghu_`, `ghs_`, `ghr_`, `github_pat_`), GitLab `glpat-`, Slack `xox[baprs]-`, AWS access key ids `AKIA...`, and `sk-` keys (including `sk-ant-`, `sk-proj-`, `sk-svcacct-`, `sk-admin-`, `sk-None-` and `sk-or-v1-` forms; at least 20 characters after the prefix (16 for the plain alphanumeric form `sk-` followed by letters and digits only), and not inside a longer word) | the token |
| `[redacted:private-key]` | a `-----BEGIN ... PRIVATE KEY-----` block | from the BEGIN line to the END line, or to the end of the text when the END line is missing |
| `[redacted:bearer]` | `Authorization: <scheme> <value>` (also with `=`, quotes, and the schemes `Bearer`, `Basic`, `Token`, `Digest`), `x-api-key: <value>`, and a bare `Bearer <value>` (a value of at least 8 characters after `Authorization` or `x-api-key`, at least 16 after a bare `Bearer`) | the value |
| `[redacted:url-credentials]` | `scheme://user:password@host` | the password only; the user name and host stay |
| `[redacted:secret-assignment]` | a value assigned (`=` or `:`, quoted or not, also JSON and its backslash-escaped form inside a command string) to a name ending in `_KEY`, `_TOKEN`, `_SECRET`, `_PASSWORD` or `_PASSWD`, or to `PASSWORD`, `PASSWD`, `SECRET`, `TOKEN`, `APIKEY` or `PASSPHRASE` (any case), and the flags `--password`, `--passwd`, `--token`, `--api-key`, `--secret`, `--auth-token` | the value (a quoted value to its closing quote, or, when a quote, a backslash, a letter or a digit follows that quote, to the end of the shell word it is joined into, as in `'It'\''s-long'`; a `$(...)` or backtick command as one value; otherwise the whole run up to a space) |
| `[redacted:unprocessable]` | a text the redactor could not finish within its 5 second limit | the whole text |

A timeout **fails closed**: the whole text becomes `[redacted:unprocessable]` and nothing from it is stored. It does not happen on ordinary input.

**What is left alone.** A value is not redacted when the whole value is a reference or a placeholder: `$NAME`, `${NAME}` or `%NAME%` (unquoted or in double quotes; in single quotes a shell does not expand it, so it is redacted), `<something>`, `{{ ... }}`, `true`, `false`, `null`, `none`, `undefined`, or empty. So `GH_TOKEN="$t" gh api ...` is stored as it is. For secret assignments and URL passwords there is no exemption for a short value or by first character: `PASSWORD='$ecret!9'` is redacted. Email addresses are not redacted.

**What is over-redacted.** The rules prefer losing text to keeping a secret, so some harmless text goes too:

- a type hint or a property with a secret-sounding name: `token: string`, `apiKey: string`, `primary_key = id`, `MAX_TOKEN = 5`;
- `TOKEN= cmd`: the value after a blank is taken to be the secret and the command is lost;
- the word after `Authorization:` when it is not a scheme (`Authorization: use credentials`, `Authorization: OAuth2 authentication` lose the second word), and the names in a list such as `["Authorization", "Content-Type"]`;
- a URL whose query happens to hold `a:b@c`;
- a long `sk-...` run that is not a key.

**Residual risk: what is not detected.** Only the shapes above are found. These are stored as written:

- a password or key written in prose, or in a variable whose name is not on the list;
- a secret in a format that is not listed: cloud keys other than `AKIA`, JWTs, database passwords outside a URL or a listed variable;
- anything you paste in a different encoding (a name that is encoded twice inside a JSON string, such as `\\\"password\\\":`, is not recognised);
- a secret split across two messages;
- a multi-word value written without quotes (only the first word is replaced);
- a header value shorter than the minima in the `[redacted:bearer]` row.

Treat the archive as sensitive data. When it lives in the main database it has the permissions that file already has, which on Linux and macOS may let other local users read it; a separate `transcripts.db_path` file is created readable only by you on Linux and macOS (0600, see Unix file permissions below), and on Windows it follows your profile's permissions. Redaction is a safety net, not a guarantee. `forget` is how you remove what should not be there.

## How resume works

Each stored session remembers, as a byte position, where its last exchange started. A file that has grown is read again from there, so the last exchange is rebuilt with what was added and every earlier one is left as it is; the result equals what reading the whole file at once would give. A file that was replaced or became shorter is not read again and is reported as `changed` (see above).

## Project and kind

**Project.** The key comes from the working directory the session ran in: `owner/repo` from the repository's `origin` remote, else the folder name of the repository or, outside a repository, of the working directory, in lower case. A worktree of a repository gives the repository's key, also when the worktree was deleted afterwards (a path with a `/.claude/worktrees/<name>` or `/.zoid/worktrees/<name>` segment is cut back to the repository that held it). A session whose file records no working directory takes it from the name of Claude Code's project directory (decoded; the decoding is lossy).

**Kind.** `interactive` or `automated`, decided from the file when it is stored. A session whose `entrypoint` starts with `sdk` (the SDK runs of reviewers and subagents) is `automated`. Otherwise a session with exactly one prompt is `automated` when that prompt starts with a known automation opening (`Review this change for security`, `You previously flagged`, `Scope:`, `You are reviewing`, `Read the full review brief`, `Round <n>`) or is longer than 1,500 characters; any other session is `interactive`. When a session grows, its kind is decided again. The kind is stored so that a later release can keep automated runs out of search results by default.

## Status

```bash
total-recall transcripts status
```

Needs no terminal and never creates anything. It prints the setting (`enabled = true` or `false`), whether the archive is active (the marker file is present; a difference between the setting and the marker is said, and applies at the next engine start), where the archive is, and, when the archive tables exist, the schema version, the number of sessions (interactive and automated) and chunks, one line per project and kind with its sessions and chunks (at most 30, then `and N more`), the forgotten sessions and projects with their identifiers and times, and the cutoff. It ends with the line `search: not available in this version yet`. When the archive was never enabled it says nothing is stored. It exits 1 when the archive cannot be located or read (an invalid `db_path`, an unreadable file), and then it does not claim that nothing is stored.

## Forgetting and unforgetting

```bash
total-recall transcripts forget --session <host>:<id>
total-recall transcripts forget --project <key>
total-recall transcripts forget --all
```

Exactly one selector. The command must be run in a terminal. It prints how many sessions and chunks will be deleted and asks you to confirm ("This cannot be undone"); declining changes nothing. It works whether or not the archive is enabled, so you can disable first. With no archive tables there is nothing to forget, and it says so without creating anything.

`forget` also keeps the text from coming back:

- `--session <host>:<id>` deletes that session and records the session so it is not archived again. A session is written as the host name, a colon and the host's session id; the identifier is split at the first colon.
- `--project <key>` deletes the sessions of that project and records the project and the repository directories its sessions belong to (the `project_root` of each stored session; a project whose directory exists but holds no repository is excluded by its key only, and so is one whose root would be a filesystem root, your home directory or a directory above it), so it is not archived again. For a session that ran in a worktree outside its repository (one made with `git worktree add ../app-hotfix`, say), the directory each such session ran in (when it lies outside the repository directory) is recorded as well, so its sessions stay out after the worktree is removed; that directory is never recorded when it is a filesystem root, your home directory, a directory above your home directory or a directory above the repository directory, and a session whose project has no repository directory records no directory at all. A session that was never archived before the forget is not covered once its worktree is removed.
- `--all` deletes everything and sets a cutoff: nothing from before the moment you ran it is archived later.

The records hold identifiers, project keys and directory names, never conversation text.

The deletion is made so that the deleted text is gone from the archive file, its text index and its write-ahead log. If the write-ahead log cannot be truncated (another process holds the database), `forget` says so, exits 1 and does not print a "forgot" message: the rows are deleted, but their text may still be in the log until it is checkpointed. Run the command again when the other process is idle.

To let something be archived again:

```bash
total-recall transcripts unforget --session <host>:<id>
total-recall transcripts unforget --project <key>
total-recall transcripts unforget --cutoff
```

`unforget` needs a terminal and asks first. It removes the exclusion record (or the cutoff) and says whether there was one. **It brings nothing back**: what was forgotten stays deleted, and only later archiving is allowed again. `status` lists the forgotten sessions and projects, which are the identifiers to give `unforget`.

Exit codes of all the `transcripts` commands: 0 done (a declined confirmation also exits 0 and changes nothing), 1 failure, 2 usage error or no terminal.

## The `[transcripts]` settings

```toml
[transcripts]
enabled = false               # the archive is off unless you turn it on
db_path = ""                  # empty: the main database; else an absolute path or one starting with ~/
retention_days = 0            # 0 keeps archived text forever
allow_remote_embedding = false  # true lets a remote embedding provider embed archive text
```

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `false` | Turns the archive on. |
| `db_path` | `""` | Where the archive lives. Empty keeps it in the main database. Otherwise a separate SQLite file, written as an absolute path or one starting with `~/` (the rules of `TOTAL_RECALL_DB_PATH`). A value that is not valid makes the archive unavailable, and `status` says why; `enable` refuses it. |
| `retention_days` | `0` | Reserved for a later release; **has no effect yet**. |
| `allow_remote_embedding` | `false` | Reserved for a later release; **has no effect yet**, because nothing is embedded. Even then it only matters if you configured a remote embedder. |

**Changing the settings.** The `transcripts` settings are changed by editing `config.toml` by hand, or by the `transcripts` commands above (`enable` and `disable` write `enabled`). `config_set`, the web UI and `total-recall config set` refuse every `transcripts.*` key, so an assistant that read a planted memory cannot turn the archive on, move it or let a remote service embed it.

## The three tools

The archive adds three MCP tools in SQLite and Cortex modes (not Postgres). They are for the assistant, not the web UI.

| Tool | Purpose |
|---|---|
| `transcript_search` | Search the archive. `query` is required; optional `scope` (`session`, `project`, `all`; default `project`), `project`, `session` (`<host>:<id>`), `since`, `include_automated`, `top_k` (1 to 50, default 10). |
| `transcript_get` | Read the archive: one chunk by `id`, or a range of a `session` with `from_exchange` and `to_exchange`; `context` (0 to 10, default 1). |
| `transcript_sessions` | List archived sessions. Optional `project`, `since`, `limit` (1 to 100, default 20), `include_automated`. |

In this version they only say whether the archive is on (backing up does not change that), and read it each call, so `enable` and `disable` apply at once:

- off: `{"enabled":false,"hint":"Run `total-recall transcripts enable` in a terminal to turn the archive on."}`
- on: `{"enabled":true,"available":false,"hint":"Searching the archive is not available in this version yet."}`

See [MCP tools](tools.md) for the full tool list.

## Unix file permissions

On Linux and macOS a separate archive file (the `db_path` file) is created readable only by you (mode 0600), with any missing parent directories created 0700, and the directory holding the marker file (`<data directory>/live/`) is created 0700, with the marker file 0600. The main database file is not changed: when the archive lives in the main database, it has the permissions that file already has.

On Windows total-recall sets nothing beyond the defaults: access follows the permissions of your user profile.

## What deleting `total-recall.db` does

When the archive lives in the main database (`db_path` empty), deleting `~/.total-recall/total-recall.db` deletes the archive together with your memories. When `db_path` names a separate file, deleting the main database leaves the archive alone; delete that file too, or use `total-recall transcripts forget --all`. Deleting by hand does not leave a cutoff or exclusion records; `forget` does.
