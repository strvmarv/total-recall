import { randomUUID } from "node:crypto";
import type { ToolContext, SessionInitResult } from "./registry.js";
import { createConfigSnapshot } from "../config.js";
import { ClaudeCodeImporter } from "../importers/claude-code.js";
import { CopilotCliImporter } from "../importers/copilot-cli.js";
import { listEntries, getEntry } from "../db/entries.js";
import { searchMemory } from "../memory/search.js";
import { promoteEntry, demoteEntry } from "../memory/promote-demote.js";
import { compactHotTier } from "../compaction/compactor.js";
import { sweepWarmTier } from "../compaction/warm-sweep.js";
import { ingestProjectDocs } from "../importers/project-docs.js";
import { detectProject } from "../utils/project-detect.js";

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

  const result: SessionInitResult = {
    project,
    importSummary,
    warmSweep: warmSweepResult,
    warmPromoted,
    projectDocs,
    hotEntryCount: hotEntries.length,
    context: contextText,
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
