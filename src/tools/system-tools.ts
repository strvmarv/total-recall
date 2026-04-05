import { statSync } from "node:fs";
import { join } from "node:path";
import type { ToolContext } from "./registry.js";
import { countEntries } from "../db/entries.js";
import { listCollections } from "../ingestion/hierarchical-index.js";
import { setNestedKey, saveUserConfig, loadConfig, createConfigSnapshot } from "../config.js";
import { getRetrievalEvents } from "../eval/event-logger.js";
import { getDataDir } from "../config.js";
import { ALL_TABLE_PAIRS } from "../types.js";

export const SYSTEM_TOOLS = [
  {
    name: "status",
    description: "Get the status of the total-recall memory system",
    inputSchema: {
      type: "object" as const,
      properties: {},
      required: [],
    },
  },
  {
    name: "config_get",
    description: "Get a configuration value by dot-notation key",
    inputSchema: {
      type: "object" as const,
      properties: {
        key: { type: "string", description: "Dot-notation config key (e.g. 'tiers.hot.max_entries'). Omit for full config." },
      },
      required: [],
    },
  },
  {
    name: "config_set",
    description: "Set a configuration value and persist to ~/.total-recall/config.toml",
    inputSchema: {
      type: "object" as const,
      properties: {
        key: { type: "string", description: "Dot-notation config key" },
        value: { description: "Value to set" },
      },
      required: ["key", "value"],
    },
  },
];

type ToolResult = { content: Array<{ type: "text"; text: string }> };

export function handleSystemTool(
  name: string,
  args: Record<string, unknown>,
  ctx: ToolContext,
): ToolResult | null {
  if (name === "status") {
    // Tier sizes
    const tierSizes: Record<string, number> = {};
    for (const { tier, type } of ALL_TABLE_PAIRS) {
      const key = `${tier}_${type === "memory" ? "memories" : "knowledge"}`;
      tierSizes[key] = countEntries(ctx.db, tier, type);
    }

    // DB info
    const dataDir = getDataDir();
    const dbPath = join(dataDir, "total-recall.db");
    let dbSizeBytes: number | null = null;
    try {
      dbSizeBytes = statSync(dbPath).size;
    } catch { /* not found */ }

    // KB collections
    const collections = listCollections(ctx.db);
    const kbCollections = collections.map((c) => ({
      id: c.id,
      name: c.name,
    }));

    // Total KB chunks (all cold_knowledge entries that aren't collections)
    const totalKbEntries = countEntries(ctx.db, "cold", "knowledge");
    const totalChunks = totalKbEntries - kbCollections.length;

    // Embedding model info
    const embeddingModel = ctx.config.embedding.model;
    const embeddingDimensions = ctx.config.embedding.dimensions;

    // Session activity (last 7 days)
    const recentEvents = getRetrievalEvents(ctx.db, { days: 7 });
    const totalRetrievals = recentEvents.length;
    const avgTopScore = recentEvents.length > 0
      ? recentEvents.reduce((sum, e) => sum + (e.top_score ?? 0), 0) / recentEvents.length
      : null;
    const outcomes = recentEvents.filter((e) => e.outcome_signal !== null);
    const positiveOutcomes = outcomes.filter((e) => e.outcome_signal === "positive").length;
    const negativeOutcomes = outcomes.filter((e) => e.outcome_signal === "negative").length;

    // Last compaction
    const lastCompaction = ctx.db
      .prepare(`SELECT * FROM compaction_log ORDER BY timestamp DESC LIMIT 1`)
      .get() as { timestamp: number; source_tier: string; target_tier: string; reason: string } | undefined;

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            tierSizes,
            knowledgeBase: {
              collections: kbCollections,
              totalChunks,
            },
            db: {
              path: dbPath,
              sizeBytes: dbSizeBytes,
              sessionId: ctx.sessionId,
            },
            embedding: {
              model: embeddingModel,
              dimensions: embeddingDimensions,
            },
            activity: {
              retrievals7d: totalRetrievals,
              avgTopScore7d: avgTopScore !== null ? Math.round(avgTopScore * 1000) / 1000 : null,
              positiveOutcomes7d: positiveOutcomes,
              negativeOutcomes7d: negativeOutcomes,
            },
            lastCompaction: lastCompaction ? {
              timestamp: lastCompaction.timestamp,
              from: lastCompaction.source_tier,
              to: lastCompaction.target_tier,
              reason: lastCompaction.reason,
            } : null,
          }),
        },
      ],
    };
  }

  if (name === "config_get") {
    const key = args.key as string | undefined;
    if (!key) {
      return { content: [{ type: "text", text: JSON.stringify(ctx.config) }] };
    }
    const parts = key.split(".");
    let value: unknown = ctx.config;
    for (const part of parts) {
      if (value === null || typeof value !== "object") return { content: [{ type: "text", text: JSON.stringify({ error: "key not found" }) }] };
      if (!Object.prototype.hasOwnProperty.call(value, part)) return { content: [{ type: "text", text: JSON.stringify({ error: `key not found: ${key}` }) }] };
      value = (value as Record<string, unknown>)[part];
    }
    return { content: [{ type: "text", text: JSON.stringify({ key, value }) }] };
  }

  if (name === "config_set") {
    const key = args.key as string;
    const value = args.value;

    // Snapshot current config before changing
    createConfigSnapshot(ctx.db, ctx.config, `pre-change:${key}`);

    const overrides = setNestedKey({}, key, value);
    saveUserConfig(overrides);

    // Reload config into context
    const refreshed = loadConfig();
    Object.assign(ctx.config, refreshed);

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({ key, value, persisted: true }),
        },
      ],
    };
  }

  return null;
}
