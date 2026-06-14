# TotalRecall.Web — Agent & Contributor Guide

This document describes `TotalRecall.Web`: the embedded ASP.NET Core minimal-API server and React SPA that form total-recall's built-in local web UI. It complements the root `AGENTS.md`, which covers the MCP server, CLI, and project-wide rules.

---

## Purpose

`TotalRecall.Web` exposes a third surface alongside the MCP server (AI assistant integration) and the CLI. The command:

```bash
total-recall ui [--port N] [--no-open] [--host H] [--token T] [--smoke]
```

starts a local Kestrel HTTP server serving a React SPA with six sections:

| Section | What it provides |
|---|---|
| Dashboard | Tier composition, retrieval quality, token usage, recent activity, trend sparklines |
| Memory | Browse, search, filter, promote/demote/pin/delete individual entries |
| Knowledge Base | Collections list, KB search, ingest files/directories, refresh/remove |
| Usage | Token spend by host, project, model, and time window; per-session breakdown |
| ✨ Insights | Memory-health score + suggestion cards (cost-spike, capture-mix, pinned-budget pressure, retrieval misses, empty KB) — pure client-side heuristics, no LLM |
| Config | Edit a safe subset of tuning knobs (validated, persisted via `config_set`); storage/embedding shown read-only |

The web UI is a local management tool — it dispatches to the **same `ToolRegistry` handlers** used by the MCP server, over a loopback HTTP API.

---

## Reuse Seam: ServerComposition → ToolRegistry

`WebUiServer.Run.cs` calls `ServerComposition.OpenProduction()` to obtain a `ServerCompositionHandles`, which carries the same `ToolRegistry` the MCP server uses. `BuildApp` receives this registry directly:

```
ServerComposition.OpenProduction()
    → ServerCompositionHandles { Registry, StorageMode, … }
    → WebUiServer.BuildApp(options, registry, token, …)
        → POST /api/tool/{name} dispatches to registry.TryGet(name)
```

Tool handlers are shared — there is no separate web-UI implementation for any operation. Adding a new MCP tool automatically makes it available in the web UI once its name is added to `ToolAllowlist`.

---

## API

### `POST /api/tool/{name}`

Dispatches to a registered `IToolHandler` via the `ToolRegistry`. The request body is an optional JSON object (the tool arguments). The response is the handler's raw `ToolCallResult.Content[0].Text` (always JSON), with HTTP 200 on success or 400 when the handler sets `IsError = true`. Exceptions are translated by `ErrorTranslator`.

Two-layer gate before dispatch:

1. **`ToolAllowlist.IsAllowed(name)`** — static allowlist of tool names reachable from the web UI (`Api/ToolAllowlist.cs`). Tools not in this set return 404. The allowlist is a curated safe subset: it excludes tools like `migrate_to_remote` that are irrelevant or potentially destructive from a browser.
2. **`registry.TryGet(name, out handler)`** — some allowlisted tools are only registered in certain backend modes (e.g. `usage_status` is not available in Postgres mode). A second 404 is returned when the tool is allowlisted but not registered in the active backend.

### `GET /api/health`

Open endpoint (no token required). Returns `{ "status": "ok", "backend": "...", "version": "..." }` via `HealthInfo`. Used by `--smoke` mode and integration tests.

### SPA fallback

All non-`/api` routes return the bootstrapped `index.html` so client-side routing (direct links, browser refresh) works correctly. Unmatched `/api/*` routes return 404 JSON.

---

## Security

**Loopback bind.** The server binds `localhost` by default (`--host` overrides this; the CLI warns when a non-loopback address is used). Only local processes and the local browser can reach it.

**Ephemeral per-launch token.** `LocalAuth.GenerateToken()` generates a 24-byte cryptographically random URL-safe token on each `total-recall ui` invocation. The token is:
- Injected into `index.html` as `window.__TR_BOOTSTRAP__ = { token, backend, version }` before the page is served (via `BuildIndexHtml` in `WebUiServer.cs`).
- Required on all `/api/*` requests except `/api/health` (sent as `X-Total-Recall-Token: <token>`).
- Compared with constant-time SHA-256 hashing (`LocalAuth.TokenMatches`) to avoid timing side-channels.
- Never logged, never persisted.

**Host-header allowlist.** Every request passes through `LocalAuth.IsAllowedHost`, which accepts only `localhost`, `127.0.0.1`, `::1`, or the configured bind host. This mitigates DNS-rebinding attacks against the loopback server.

**Operator-supplied token.** `--token <tok>` lets CI or scripted use provide a known token instead of the random per-launch one (e.g. for scripted API calls).

**Files:** `Security/LocalAuth.cs`, `WebUiServer.cs` (middleware), `Api/ToolAllowlist.cs`

---

## Node-Optional Build (`BuildSpa`)

The SPA build is **opt-in**. The MSBuild property `BuildSpa` (default `false`) controls whether the Vite build runs:

```xml
<BuildSpa Condition="'$(BuildSpa)' == ''">false</BuildSpa>
```

| Scenario | What happens |
|---|---|
| `dotnet build` (default) | `wwwroot/` is cleared; `build/placeholder.html` is copied to `wwwroot/index.html`; the assembly embeds only the placeholder. |
| `dotnet build -p:BuildSpa=true` | MSBuild runs `npm ci && npm run build` in `ClientApp/`; Vite writes hashed assets into `wwwroot/`; the assembly embeds the full SPA. |
| `dotnet test` | Node-free; no SPA involved. |
| Release publish (CI) | Always passes `-p:BuildSpa=true`. |

The three MSBuild targets in `TotalRecall.Web.csproj`:

1. **`BuildClientApp`** (when `BuildSpa=true`, before `EnsureWwwRoot`) — runs `npm ci` then `npm run build`; fails with a clear error if `wwwroot/index.html` is missing afterwards.
2. **`EnsureWwwRoot`** (before `EmbedWwwRoot`) — when NOT building the SPA, clears `wwwroot/` and copies the placeholder so the output is deterministic.
3. **`EmbedWwwRoot`** (before `PrepareForBuild;GenerateEmbeddedFilesManifest`) — adds all `wwwroot/**` files as `EmbeddedResource` after Vite produces hashed filenames.

The embedded files are served at runtime via `ManifestEmbeddedFileProvider` (see `WebUiServer.cs`).

**To develop the SPA locally** without rebuilding the .NET binary, run `total-recall ui` in one terminal (serves the API on port 5577) and `npm run dev` inside `ClientApp/` in another. Vite proxies `/api/*` to `http://127.0.0.1:5577` (configured in `vite.config.ts`).

---

## AOT Constraints

`TotalRecall.Web` is AOT-compatible (`<IsAotCompatible>true</IsAotCompatible>`):

- **`WebApplication.CreateSlimBuilder()`** — the AOT-safe minimal API builder. Avoids `CreateBuilder` which relies on reflection-based configuration providers incompatible with NativeAOT.
- **`EnableRequestDelegateGenerator=true`** — the Roslyn source generator that AOT-compiles route handler delegates at build time instead of emitting them via `Reflection.Emit` at runtime.
- **`WebJsonContext`** — a `JsonSerializerContext` subclass with `[JsonSerializable]` attributes for all wire types (`HealthInfo`, `ApiError`, `BootstrapInfo`). Source-generated serialization; no runtime reflection. Tool handler responses use the existing server-side serialization and are returned as raw strings, so they do not go through `WebJsonContext`.

---

## ClientApp Layout

```
ClientApp/
├── src/
│   ├── pages/          — one file per section (Dashboard, Memory, KnowledgeBase, Usage, Insights, Config)
│   ├── components/     — shared and per-section components
│   │   ├── dashboard/  — TierCompositionCard, TokenUsageCard, TrendsCard, RetrievalQualityCard, …
│   │   ├── memory/     — MemoryTable, MemoryDetail, MemoryFilters
│   │   ├── kb/         — KbCollectionsTable, KbSearch, KbIngest
│   │   ├── usage/      — UsageHeadline, UsageBreakdown, UsageModelMix, UsageTopSessions, …
│   │   ├── insights/   — InsightCard, HealthScore
│   │   └── config/     — ConfigField
│   └── lib/
│       ├── api.ts       — typed fetch wrapper (reads token from window.__TR_BOOTSTRAP__)
│       ├── bootstrap.ts — reads window.__TR_BOOTSTRAP__ (token, backend, version)
│       ├── types.ts     — shared TypeScript types (mirrors server-side shapes)
│       ├── pricing.ts   — bundled model pricing table (client-side cost estimation)
│       ├── usageCost.ts — cost calculation helpers
│       ├── insights.ts  — client-side insight signal derivation
│       ├── trendsMath.ts, usageMath.ts, time.ts, configFields.ts — pure utilities
│       └── useAsync.ts  — generic data-fetching hook
├── vite.config.ts       — Vite config; dev-server proxies /api to port 5577
└── package.json         — dependencies: React 18, Recharts (charting), Vitest (tests)
```

**Charting:** All charts use [Recharts](https://recharts.org/). No other charting library is present.

**Tests:** `npm test` (or `npm run test`) runs Vitest in `jsdom` mode. Test files live alongside the source (`*.test.tsx` / `*.test.ts`). The test setup file is `src/test/setup.ts`.

**Type checking:** `npm run typecheck` runs `tsc --noEmit`. CI runs both before publish.

---

## v1 Deferrals

These are known gaps in the v1 implementation, tracked in `docs/TODO.md`:

- **Server-side `/api/insights` engine** — near-duplicate merge, retrieval-gap detection, threshold simulation. Currently all client-side.
- **Editable/persisted pricing** — `[pricing]` config section + Config editor for custom model pricing.
- **Usage activity heatmap** — needs hourly `GroupBy` on `usage_events` (current `usage_status` returns only daily rollups).
- **Per-project trend sparklines** — per-project weekly comparison.
- **Cortex remote phase** — proxying team KB and remote usage through the web UI.
- **SSE / live push** — all sections currently poll on-demand; real-time updates require an SSE or WebSocket endpoint.
