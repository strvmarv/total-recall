# total-recall for OpenCode

## Installation

1. Add the MCP server to your OpenCode config:

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "node",
      "args": ["/path/to/total-recall/dist/index.js"]
    }
  }
}
```

2. Copy the skills directory to your OpenCode plugins:

```bash
cp -r skills/ ~/.opencode/plugins/total-recall/skills/
```

3. Restart OpenCode. total-recall will auto-initialize on first session.
