# total-recall Installation Guide

This file is designed to be read by AI coding assistants. If you're an LLM helping a user install total-recall, follow the instructions for their platform below.

## Prerequisites

### macOS: Homebrew sqlite required

total-recall uses [`sqlite-vec`](https://github.com/asg017/sqlite-vec) for vector search, which is loaded into SQLite as a runtime extension. On macOS, `bun:sqlite` dlopens the system `/usr/lib/libsqlite3.dylib`, which Apple ships **without** `SQLITE_ENABLE_LOAD_EXTENSION` — so `sqlite-vec` cannot attach and the MCP server fails at first use with:

> This build of sqlite3 does not support dynamic extension loading

Fix (one-time):

```bash
brew install sqlite
```

total-recall automatically picks up the keg-only brew sqlite from `/opt/homebrew/opt/sqlite/lib/libsqlite3.dylib` (Apple Silicon) or `/usr/local/opt/sqlite/lib/libsqlite3.dylib` (Intel) via `Database.setCustomSQLite()` before any DB connection is opened. Nothing else to configure.

Linux and Windows users can skip this step — their system SQLite builds support extension loading out of the box.

## Quick Install (Any Platform)

The MCP server is available as an npm package:

```bash
npm install -g @strvmarv/total-recall
```

## Claude Code

### Option A: Plugin Install (Recommended)

Run in Claude Code:

```
/plugin install total-recall@strvmarv-total-recall-marketplace
```

If the marketplace isn't registered yet:

```
/plugin marketplace add strvmarv/total-recall-marketplace
/plugin install total-recall@strvmarv-total-recall-marketplace
```

### Option B: Manual Plugin Install

1. Clone the repo:
```bash
git clone https://github.com/strvmarv/total-recall.git ~/.claude/plugins/total-recall
cd ~/.claude/plugins/total-recall
npm install && npm run build
```

2. Add to your Claude Code settings (`~/.claude/settings.json`):
```json
{
  "enabledPlugins": {
    "total-recall": true
  }
}
```

### Option C: MCP Server Only

Add to your Claude Code MCP config:
```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

## GitHub Copilot CLI

Add to your Copilot CLI MCP config (`~/.copilot/mcp-config.json`):

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

## OpenCode

Add to your OpenCode config (`opencode.json` or `~/.config/opencode/config.json`):

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

## Cline (VS Code)

Add to your Cline MCP settings:

1. Open VS Code Command Palette
2. Search "Cline: MCP Settings"
3. Add:
```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

## Cursor

Add to your Cursor MCP config (`.cursor/mcp.json` or global settings):

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

For full plugin support (skills + hooks), clone the repo:
```bash
git clone https://github.com/strvmarv/total-recall.git
```

## Verification

After installation, the first session should output:

```
total-recall: initialized · X memories imported · Y docs ingested · system verified
```

You can verify it's working with:
- `/total-recall status` — shows tier sizes and health
- `/total-recall search "test"` — runs a test search

## What Happens on First Run

1. Creates `~/.total-recall/` directory
2. Creates SQLite database with schema
3. Loads bundled embedding model (no download needed)
4. Scans for existing memories from host tools (Claude Code, Copilot CLI)
5. Auto-ingests project docs (README, docs/, etc.)
6. Runs a quick smoke test to verify everything works
