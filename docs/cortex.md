# Connecting to Cortex

Total Recall Cortex is the shared backend platform that adds team knowledge bases, connectors (Jira, Confluence, GitHub), chat/RAG, and a React UI on top of the plugin's memory layer.

In Cortex mode, the plugin operates as a hybrid:
- **User memories** are stored locally (fast reads/writes), synced bidirectionally to Cortex every 300 seconds and at session boundaries
- **Sticky (pinned) entries are local-only** — Cortex has no sticky-pin support yet, so pins are never pushed, pulled, reconciled, or migrated (`migrate_to_remote` skips them)
- **Global knowledge** (team KB, connector-ingested data) is queried remotely from Cortex
- **Telemetry** (usage, retrieval events, compaction log) is pushed to Cortex for unified dashboards
- **Skills** are synced to Cortex so team members share the same skill library

Configure in your [`config.toml`](configuration.md) (`~/.total-recall/config.toml`):

```toml
[storage]
mode = "cortex"

[cortex]
url = "https://your-cortex-instance.example.com"
pat = "tr_your_personal_access_token"
sync_interval_seconds = 300       # Background sync interval (default: 300)
```

Or via environment variables:

```bash
export TOTAL_RECALL_CORTEX_URL="https://your-cortex-instance.example.com"
export TOTAL_RECALL_CORTEX_PAT="tr_your_personal_access_token"
```

Generate a PAT from the Cortex web UI under Settings → Personal Access Tokens.

**Offline resilience:** If Cortex is unreachable, the plugin continues working locally. A persistent sync queue buffers outbound changes and flushes automatically when connectivity is restored.
