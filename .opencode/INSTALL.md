# total-recall for OpenCode

Since 0.8.0 total-recall ships as a prebuilt .NET AOT binary wrapped by
a tiny Node launcher. You no longer need a TypeScript source checkout to
run it; `npm install` (or `npx`) pulls the right platform binary for you.

## Installation

### Option 1: via npm (recommended)

```bash
npm install -g @strvmarv/total-recall
```

This resolves the correct prebuilt binary for your platform
(`linux-x64`, `linux-arm64`, `osx-arm64`, `win-x64`) and installs the
`total-recall` command on your PATH. Then add the MCP server to your
OpenCode config:

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

### Option 2: via npx (no global install)

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "npx",
      "args": ["-y", "@strvmarv/total-recall"]
    }
  }
}
```

`npx` downloads the package on first run, then caches it. First launch
may take a few extra seconds while the per-platform binary is fetched
from GitHub Releases if not already cached.

### Option 3: from a source checkout

If you cloned the repo directly (e.g. for development), point OpenCode
at the Node launcher instead of the deleted TypeScript entry:

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "node",
      "args": ["/path/to/total-recall/bin/start.js"]
    }
  }
}
```

On first launch, `bin/start.js` will download the correct AOT binary
from the matching GitHub Release into `binaries/<rid>/` if it is not
already present.

## Skills

Copy the skills directory to your OpenCode plugins:

```bash
cp -r skills/ ~/.opencode/plugins/total-recall/skills/
```

## First session

Restart OpenCode. total-recall will auto-initialize on first session.
