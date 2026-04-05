import { randomUUID } from "node:crypto";
import type Database from "better-sqlite3";
import type { ToolContext, SessionInitResult } from "./registry.js";
import { createConfigSnapshot } from "../config.js";
import { ClaudeCodeImporter } from "../importers/claude-code.js";
import { CopilotCliImporter } from "../importers/copilot-cli.js";
import { listEntries, getEntry, countEntries, listEntriesByMetadata } from "../db/entries.js";
import { searchMemory } from "../memory/search.js";
import { promoteEntry, demoteEntry } from "../memory/promote-demote.js";
import { compactHotTier } from "../compaction/compactor.js";
import { sweepWarmTier } from "../compaction/warm-sweep.js";
import { ingestProjectDocs } from "../importers/project-docs.js";
import { detectProject } from "../utils/project-detect.js";

function truncateHint(content: string, maxLen = 120): string {
  if (content.length <= maxLen) return content;
  return content.slice(0, maxLen) + "...";
}

export function generateHints(
  db: Database.Database,
  warmPromotedIds: string[],
): string[] {
  const seen = new Set<string>();
  const hints: string[] = [];

  // Priority 1: corrections & preferences (max 2)
  const correctionsAndPrefs = [
    ...listEntriesByMetadata(db, "warm", "memory", { entry_type: "correction" }, {
      orderBy: "access_count DESC", limit: 2,
    }),
    ...listEntriesByMetadata(db, "warm", "memory", { entry_type: "preference" }, {
      orderBy: "access_count DESC", limit: 2,
    }),
  ]
    .sort((a, b) => b.access_count - a.access_count)
    .slice(0, 2);

  for (const entry of correctionsAndPrefs) {
    if (!seen.has(entry.id)) {
      seen.add(entry.id);
      hints.push(truncateHint(entry.content));
    }
  }

  // Priority 2: frequently accessed (max 2, access_count >= 3)
  const frequent = listEntries(db, "warm", "memory", {
    orderBy: "access_count DESC", limit: 10,
  }).filter((e) => e.access_count >= 3 && !seen.has(e.id));

  for (const entry of frequent.slice(0, 2)) {
    seen.add(entry.id);
    hints.push(truncateHint(entry.content));
  }

  // Priority 3: recently promoted (max 1)
  for (const id of warmPromotedIds.slice(0, 1)) {
    if (seen.has(id)) continue;
    const entry = getEntry(db, "hot", "memory", id);
    if (entry) {
      seen.add(entry.id);
      hints.push(truncateHint(entry.content));
    }
  }

  return hints.slice(0, 5);
}

export function getLastSessionAge(db: Database.Database): string | null {
  const row = db
    .prepare(`SELECT MAX(timestamp) as ts FROM compaction_log`)
    .get() as { ts: number | null } | undefined;

  const ts = row?.ts;
  if (!ts) return null;

  const diffMs = Date.now() - ts;
  const minutes = Math.floor(diffMs / (60 * 1000));
  const hours = Math.floor(diffMs / (60 * 60 * 1000));
  const days = Math.floor(diffMs / (24 * 60 * 60 * 1000));
  const weeks = Math.floor(days / 7);

  if (minutes === 0) return "just now";
  if (minutes === 1) return "1 minute ago";
  if (minutes < 60) return `${minutes} minutes ago`;
  if (hours === 1) return "1 hour ago";
  if (hours < 24) return `${hours} hours ago`;
  if (days === 1) return "1 day ago";
  if (days < 7) return `${days} days ago`;
  if (weeks === 1) return "1 week ago";
  return `${weeks} weeks ago`;
}

export const SESSION_TOOLS = [
  {
    name: "session_start",
    description: "Initialize a session: sync host tool imports and assemble hot tier context",
    inputSchema: {
      type: "object" as const,
      properties: {},
      required: [],
    },
  },
  {
    name: "session_end",
    description: "End a session: compact the hot tier and return compaction results",
    inputSchema: {
      type: "object" as const,
      properties: {},
      required: [],
    },
  },
  {
    name: "session_context",
    description: "Return current hot tier entries as formatted context text",
    inputSchema: {
      type: "object" as const,
      properties: {},
      required: [],
    },
  },
];

type ToolResult = { content: Array<{ type: "text"; text: string }> };

/**
 * Core session initialization logic — called by oninitialized (background),
 * lazy-init guard (first tool call), or the session_start tool itself.
 * Results are cached on ctx so subsequent calls are free.
 */
export async function runSessionInit(ctx: ToolContext): Promise<SessionInitResult> {
  // Return cached results if already initialized
  if (ctx.sessionInitResult) return ctx.sessionInitResult;

  await ctx.embedder.ensureLoaded();
  const embedFn = (text: string) => ctx.embedder.embed(text);

  // Detect project context
  const project = detectProject(process.cwd());

  const importers = [
    new ClaudeCodeImporter(),
    new CopilotCliImporter(),
  ];

  const importSummary: Array<{
    tool: string;
    memoriesImported: number;
    knowledgeImported: number;
  }> = [];

  for (const importer of importers) {
    if (!importer.detect()) continue;

    const memResult = await importer.importMemories(ctx.db, embedFn, project ?? undefined);
    const kbResult = await importer.importKnowledge(ctx.db, embedFn);

    importSummary.push({
      tool: importer.name,
      memoriesImported: memResult.imported,
      knowledgeImported: kbResult.imported,
    });
  }

  // Run warm sweep if stale
  let warmSweepResult: { demoted: number } | null = null;
  const sweepIntervalMs = ctx.config.compaction.warm_sweep_interval_days * 24 * 60 * 60 * 1000;
  const lastSweep = ctx.db
    .prepare(`SELECT MAX(timestamp) as ts FROM compaction_log WHERE reason = 'warm_sweep_decay'`)
    .get() as { ts: number | null } | undefined;
  const lastSweepTs = lastSweep?.ts ?? 0;

  if (Date.now() - lastSweepTs > sweepIntervalMs) {
    const sessionId = ctx.sessionId ?? randomUUID();
    const result = await sweepWarmTier(ctx.db, embedFn, {
      coldDecayDays: ctx.config.tiers.warm.cold_decay_days,
    }, sessionId);
    if (result.demoted.length > 0) {
      warmSweepResult = { demoted: result.demoted.length };
    }
  }

  // Auto-ingest project docs
  let projectDocs: { filesIngested: number; totalChunks: number } | null = null;
  const docsResult = await ingestProjectDocs(ctx.db, embedFn, process.cwd());
  if (docsResult.filesIngested > 0) {
    projectDocs = { filesIngested: docsResult.filesIngested, totalChunks: docsResult.totalChunks };
  }

  // Semantic warm search: promote relevant warm entries to hot based on project
  const warmPromotedIds: string[] = [];
  let warmPromoted = 0;
  if (project) {
    const warmResults = await searchMemory(ctx.db, embedFn, project, {
      tiers: [{ tier: "warm", content_type: "memory" }],
      topK: ctx.config.tiers.warm.retrieval_top_k,
      minScore: ctx.config.tiers.warm.similarity_threshold,
    });

    const hotCount = listEntries(ctx.db, "hot", "memory").length;
    const budget = ctx.config.tiers.hot.max_entries - hotCount;

    for (const result of warmResults.slice(0, Math.max(0, budget))) {
      const entry = getEntry(ctx.db, "warm", "memory", result.entry.id);
      if (entry && (entry.project === project || entry.project === null)) {
        await promoteEntry(ctx.db, embedFn, result.entry.id, "warm", "memory", "hot", "memory");
        warmPromotedIds.push(result.entry.id);
        warmPromoted++;
      }
    }
  }

  // Assemble hot tier context with token budget enforcement
  let hotEntries = listEntries(ctx.db, "hot", "memory");

  const tokenBudget = ctx.config.tiers.hot.token_budget;
  const estimateTokens = (text: string) => Math.ceil(text.length / 4);

  let totalTokens = hotEntries.reduce((sum, e) => sum + estimateTokens(e.content), 0);
  if (totalTokens > tokenBudget && hotEntries.length > 1) {
    const sorted = [...hotEntries].sort((a, b) => a.decay_score - b.decay_score);
    const evicted: string[] = [];

    while (totalTokens > tokenBudget && sorted.length > 1) {
      const victim = sorted.shift()!;
      totalTokens -= estimateTokens(victim.content);
      evicted.push(victim.id);
    }

    if (evicted.length > 0) {
      for (const id of evicted) {
        await demoteEntry(ctx.db, embedFn, id, "hot", "memory", "warm", "memory");
      }
      hotEntries = listEntries(ctx.db, "hot", "memory");
    }
  }

  const contextLines = hotEntries.map((e) => {
    const tags = e.tags.length > 0 ? ` [${e.tags.join(", ")}]` : "";
    return `- ${e.content}${tags}`;
  });
  const contextText = contextLines.join("\n");

  // Snapshot config for this session
  const snapshotId = createConfigSnapshot(ctx.db, ctx.config, "session-start");
  ctx.configSnapshotId = snapshotId;

  // Tier summary stats
  const tierSummary = {
    hot: hotEntries.length,
    warm: countEntries(ctx.db, "warm", "memory") + countEntries(ctx.db, "warm", "knowledge"),
    cold: countEntries(ctx.db, "cold", "memory") + countEntries(ctx.db, "cold", "knowledge"),
    kb: countEntries(ctx.db, "hot", "knowledge") + countEntries(ctx.db, "warm", "knowledge") + countEntries(ctx.db, "cold", "knowledge"),
    collections: (ctx.db.prepare(`SELECT COUNT(DISTINCT collection_id) as count FROM cold_knowledge WHERE collection_id IS NOT NULL`).get() as { count: number }).count,
  };

  // Actionable hints
  const hints = generateHints(ctx.db, warmPromotedIds);

  // Last session age
  const lastSessionAge = getLastSessionAge(ctx.db);

  const result: SessionInitResult = {
    project,
    importSummary,
    warmSweep: warmSweepResult,
    warmPromoted,
    projectDocs,
    hotEntryCount: hotEntries.length,
    context: contextText,
    tierSummary,
    hints,
    lastSessionAge,
  };

  ctx.sessionInitResult = result;
  ctx.sessionInitialized = true;
  return result;
}

export async function handleSessionTool(
  name: string,
  args: Record<string, unknown>,
  ctx: ToolContext,
): Promise<ToolResult | null> {
  if (name === "session_start") {
    // If init already ran (via oninitialized or lazy-init), return cached results.
    // Otherwise run it now.
    const result = await runSessionInit(ctx);

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            sessionId: ctx.sessionId,
            ...result,
          }),
        },
      ],
    };
  }

  if (name === "session_end") {
    await ctx.embedder.ensureLoaded();
    const embedFn = (text: string) => ctx.embedder.embed(text);

    const sessionId = ctx.sessionId ?? randomUUID();
    const result = await compactHotTier(ctx.db, embedFn, ctx.config.compaction, sessionId, ctx.configSnapshotId);

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            sessionId,
            carryForward: result.carryForward.length,
            promoted: result.promoted.length,
            discarded: result.discarded.length,
            details: result,
          }),
        },
      ],
    };
  }

  if (name === "session_context") {
    const hotMemories = listEntries(ctx.db, "hot", "memory");
    const hotKnowledge = listEntries(ctx.db, "hot", "knowledge");

    const allEntries = [...hotMemories, ...hotKnowledge];
    const lines = allEntries.map((e) => {
      const tags = e.tags.length > 0 ? ` [${e.tags.join(", ")}]` : "";
      const project = e.project ? ` (project: ${e.project})` : "";
      return `- ${e.content}${tags}${project}`;
    });

    const contextText = lines.length > 0
      ? lines.join("\n")
      : "(no hot tier entries)";

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            entryCount: allEntries.length,
            context: contextText,
          }),
        },
      ],
    };
  }

  return null;
}

export function registerSessionTools() {
  return SESSION_TOOLS;
}
