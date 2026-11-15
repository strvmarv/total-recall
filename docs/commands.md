# Commands

All commands are routed through the `/total-recall:commands` skill:

| Command | Description |
|---|---|
| `/total-recall:commands help` | Show command reference table |
| `/total-recall:commands setup` | Auto-configure permissions for the total-recall MCP tools (Claude Code only) |
| `/total-recall:commands status` | Dashboard overview |
| `/total-recall:commands search <query>` | Semantic search across all tiers |
| `/total-recall:commands store <content>` | Manually store a memory |
| `/total-recall:commands forget <query>` | Find and delete entries |
| `/total-recall:commands inspect <id>` | Deep dive on single entry with compaction history |
| `/total-recall:commands promote <id>` | Move entry to higher tier |
| `/total-recall:commands demote <id>` | Move entry to lower tier |
| `/total-recall:commands pin <id>` | Pin entry — always injected at session start, never decays |
| `/total-recall:commands unpin <id>` | Clear an entry's sticky flag (it stays in hot as an earned resident) |
| `/total-recall:commands history` | Show recent tier movements |
| `/total-recall:commands recent` | List the newest memories by timestamp |
| `/total-recall:commands lineage <id>` | Show compaction ancestry |
| `/total-recall:commands export` | Export to portable JSON format |
| `/total-recall:commands import <file>` | Import from export file |
| `/total-recall:commands ingest <path>` | Add files or directories to knowledge base |
| `/total-recall:commands kb search <query>` | Search knowledge base |
| `/total-recall:commands kb list` | List KB collections |
| `/total-recall:commands kb refresh <id>` | Re-ingest a collection |
| `/total-recall:commands kb remove <id>` | Remove KB entry |
| `/total-recall:commands compact [--fast]` | Compact the hot tier now (default: model-driven; `--fast`: heuristic sweep) |
| `/total-recall:commands eval` | Retrieval quality metrics |
| `/total-recall:commands eval --benchmark` | Run synthetic benchmark |
| `/total-recall:commands eval --compare <name>` | Compare metrics between two config snapshots |
| `/total-recall:commands eval --snapshot <name>` | Manually create a named config snapshot |
| `/total-recall:commands eval grow` | Review benchmark candidates captured from retrieval misses (`list`, `resolve`) |
| `/total-recall:commands config get <key>` | Read config value |
| `/total-recall:commands config set <key> <val>` | Update config |
| `/total-recall:commands import-host` | Re-run import sync from all host tools |
| `/total-recall:commands update` | Update the plugin to the latest version |
| `/total-recall:commands ui` | Open the built-in web UI (`total-recall ui`) |

Benchmark candidates captured from retrieval misses are reviewed (accepted or rejected) with `eval grow`, which calls the `eval_grow` tool; the CLI forms are `total-recall eval grow list` and `total-recall eval grow resolve`.

Memory capture, retrieval, and compaction run automatically in the background — see the "Automatic Behavior" section of the `/total-recall:commands` skill.

> **Note:** `/total-recall:commands` is implemented as a Claude Code skill (at `skills/commands/SKILL.md`), not as a slash-command file under `commands/`. The skill handles all `<subcommand>` arguments internally.
