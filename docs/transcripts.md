# Transcript archive

> **Status: foundation only.** The archive can be enabled, inspected and emptied, but it cannot be searched yet, and this version stores no conversation text at all. Searching the archive, and filling it, arrive in a later release. Until then the three `transcript_*` tools only report whether the archive is on.

## What it is

The transcript archive is an opt-in store for the dialogue of your agent sessions: your prompts and the assistant's replies, kept so that a later session can look back at what was said, not only at the memories that were distilled from it. It is separate from the memory tiers and the knowledge base: nothing in the archive is injected at session start, and nothing in it is a memory.

It is **off by default**. Only you can turn it on, from a terminal or by editing `config.toml`. An assistant cannot enable, disable or redirect it through `config_set`, the web UI or `config set` (see [Changing the settings](#the-transcripts-settings)).

## What will be stored

Once a later release can fill the archive, and only while it is enabled, it will keep:

- the dialogue of agent sessions (your prompts and the assistant's replies), split into chunks;
- a little metadata about each session: the host and its session id, the project, the working directory, start and end times, and a title.

How it is kept:

- **Locally.** The archive lives in a SQLite database on your machine: in the main database (`~/.total-recall/total-recall.db` by default) or, if you set `transcripts.db_path`, in a separate file.
- **With secrets redacted.** Text is meant to be scrubbed of secrets before it is stored.
- **Never synced by total-recall.** The archive is not sent to Cortex and is not part of git sync. Nothing that syncs reads or writes its tables. This is about total-recall's own sync: if the main database, or the `transcripts.db_path` file, sits in a folder that another service shares (Dropbox, iCloud, OneDrive), that service copies the archive, and `forget` cannot reach those copies.

In this version none of this happens yet: enabling the archive creates empty tables and nothing else. Only `transcripts enable` creates those tables; a hand edit of `config.toml` does not.

## Privacy

- The archive is off until you run `total-recall transcripts enable` in a terminal and confirm. The confirmation says what will be stored.
- The archive text is embedded only by the local model. It is never sent to a remote embedding service unless you set `allow_remote_embedding = true` (see [The `[transcripts]` settings](#the-transcripts-settings)).
- If the archive file is in a folder that another service shares, that service copies it; total-recall does not sync it, and deleting it with `forget` does not remove the service's copies.
- You can delete all of it, or one session or one project, at any time (see [Forgetting and unforgetting](#forgetting-and-unforgetting)), and keep it from being archived again.
- The archive is not supported with Postgres storage (`[storage] connection_string`, or `mode = "postgres"`): `enable` refuses, and the archive stays off.

See [PRIVACY.md](../PRIVACY.md) for everything total-recall stores and what can leave your machine.

## Enabling

```bash
total-recall transcripts enable
```

The command must be run in a terminal (standard input must not be redirected) and asks you to confirm. There is no flag to skip the question: a script, or a way to set it without a terminal, is a hand edit of `config.toml` (see below). Declining changes nothing.

On a yes it opens the archive (creating its tables), sets `transcripts.enabled = true` in `~/.total-recall/config.toml` and writes a marker file, `<data directory>/live/enabled`, that tells the tools (and, in a later release, the hosts' hooks) that the archive is on. The tools notice the change at once; no restart is needed.

Editing `config.toml` by hand also works (`[transcripts]` `enabled = true`). The engine mirrors the setting into the marker file when it starts, so a hand edit takes effect at the next engine start. A hand edit creates no tables: only `transcripts enable` does, and until you run it `transcripts status` shows the archive as on with nothing stored.

To turn it off:

```bash
total-recall transcripts disable
```

`disable` needs no terminal. It sets `enabled = false` and removes the marker file. **What the archive holds is kept**; use `transcripts forget --all` to delete it.

## Status

```bash
total-recall transcripts status
```

Needs no terminal and never creates anything. It prints the setting (`enabled = true` or `false`), whether the archive is active (the marker file is present; a difference between the setting and the marker is said, and applies at the next engine start), where the archive is, and, when the archive tables exist, the schema version, the number of sessions (interactive and automated) and chunks, the forgotten sessions and projects with their identifiers and times, and the cutoff. It ends with the line `search: not available in this version yet`. When the archive was never enabled it says nothing is stored. It exits 1 when the archive cannot be located or read (an invalid `db_path`, an unreadable file), and then it does not claim that nothing is stored.

## Forgetting and unforgetting

```bash
total-recall transcripts forget --session <host>:<id>
total-recall transcripts forget --project <key>
total-recall transcripts forget --all
```

Exactly one selector. The command must be run in a terminal. It prints how many sessions and chunks will be deleted and asks you to confirm ("This cannot be undone"); declining changes nothing. It works whether or not the archive is enabled, so you can disable first. With no archive tables there is nothing to forget, and it says so without creating anything.

`forget` also keeps the text from coming back:

- `--session <host>:<id>` deletes that session and records the session so it is not archived again. A session is written as the host name, a colon and the host's session id; the identifier is split at the first colon.
- `--project <key>` deletes the sessions of that project and records the project and the directories its sessions ran in, so it is not archived again.
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

In this version they only say whether the archive is on, and read it each call, so `enable` and `disable` apply at once:

- off: `{"enabled":false,"hint":"Run `total-recall transcripts enable` in a terminal to turn the archive on."}`
- on: `{"enabled":true,"available":false,"hint":"Searching the archive is not available in this version yet."}`

See [MCP tools](tools.md) for the full tool list.

## Unix file permissions

On Linux and macOS a separate archive file (the `db_path` file) is created readable only by you (mode 0600), with any missing parent directories created 0700, and the directory holding the marker file (`<data directory>/live/`) is created 0700, with the marker file 0600. The main database file is not changed: when the archive lives in the main database, it has the permissions that file already has.

On Windows total-recall sets nothing beyond the defaults: access follows the permissions of your user profile.

## What deleting `total-recall.db` does

When the archive lives in the main database (`db_path` empty), deleting `~/.total-recall/total-recall.db` deletes the archive together with your memories. When `db_path` names a separate file, deleting the main database leaves the archive alone; delete that file too, or use `total-recall transcripts forget --all`. Deleting by hand does not leave a cutoff or exclusion records; `forget` does.
