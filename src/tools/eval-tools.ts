import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import type Database from "better-sqlite3";
import type { ToolContext } from "./registry.js";
import { runBenchmark } from "../eval/benchmark-runner.js";
import { getRetrievalEvents } from "../eval/event-logger.js";
import { computeMetrics, computeComparisonMetrics } from "../eval/metrics.js";
import { createConfigSnapshot } from "../config.js";

const __dirname = fileURLToPath(new URL(".", import.meta.url));
// In dev: __dirname = src/tools/ (2 levels up to root)
// In dist: __dirname = dist/ (1 level up to root)
const PACKAGE_ROOT = __dirname.endsWith("dist/") || __dirname.endsWith("dist")
  ? resolve(__dirname, "..")
  : resolve(__dirname, "..", "..");

export const EVAL_TOOLS = [
  {
    name: "eval_benchmark",
    description: "Run a retrieval benchmark against the eval corpus and benchmark queries",
    inputSchema: {
      type: "object" as const,
      properties: {
        compare_to: { type: "string", description: "Optional baseline snapshot ID to compare against" },
        snapshot: { type: "string", description: "Optional config snapshot ID to tag this run" },
      },
      required: [],
    },
  },
  {
    name: "eval_report",
    description: "Generate a retrieval quality report from logged events",
    inputSchema: {
      type: "object" as const,
      properties: {
        days: { type: "number", description: "Number of days of history to include (default: 7)" },
        config_snapshot: { type: "string", description: "Optional config snapshot ID to filter by" },
      },
      required: [],
    },
  },
  {
    name: "eval_compare",
    description: "Compare retrieval metrics between two config snapshots",
    inputSchema: {
      type: "object" as const,
      properties: {
        before: { type: "string", description: "Snapshot name or ID for the 'before' config" },
        after: { type: "string", description: "Snapshot name or ID for the 'after' config (default: latest)" },
        days: { type: "number", description: "Days of events to include (default: 30)" },
      },
      required: ["before"],
    },
  },
  {
    name: "eval_snapshot",
    description: "Manually create a named config snapshot",
    inputSchema: {
      type: "object" as const,
      properties: {
        name: { type: "string", description: "Name for the snapshot" },
      },
      required: ["name"],
    },
  },
];

type ToolResult = { content: Array<{ type: "text"; text: string }>; isError?: true };

function resolveSnapshotId(db: Database.Database, nameOrId: string): string | null {
  const byId = db.prepare("SELECT id FROM config_snapshots WHERE id = ?").get(nameOrId) as { id: string } | undefined;
  if (byId) return byId.id;

  if (nameOrId === "latest") {
    const latest = db.prepare("SELECT id FROM config_snapshots ORDER BY timestamp DESC LIMIT 1").get() as { id: string } | undefined;
    return latest?.id ?? null;
  }

  const byName = db.prepare("SELECT id FROM config_snapshots WHERE name = ? ORDER BY timestamp DESC LIMIT 1").get(nameOrId) as { id: string } | undefined;
  return byName?.id ?? null;
}

export async function handleEvalTool(
  name: string,
  args: Record<string, unknown>,
  ctx: ToolContext,
): Promise<ToolResult | null> {
  if (name === "eval_benchmark") {
    await ctx.embedder.ensureLoaded();
    const embedFn = (text: string) => ctx.embedder.embed(text);

    const corpusPath = resolve(PACKAGE_ROOT, "eval", "corpus", "memories.jsonl");
    const benchmarkPath = resolve(PACKAGE_ROOT, "eval", "benchmarks", "retrieval.jsonl");

    const result = await runBenchmark(ctx.db, embedFn, {
      corpusPath,
      benchmarkPath,
    });

    return { content: [{ type: "text", text: JSON.stringify(result) }] };
  }

  if (name === "eval_report") {
    const days = (args.days as number | undefined) ?? 7;
    const configSnapshot = args.config_snapshot as string | undefined;

    const events = getRetrievalEvents(ctx.db, {
      days,
      configSnapshotId: configSnapshot,
    });

    const cutoff = Date.now() - days * 24 * 60 * 60 * 1000;
    const compactionRows = ctx.db
      .prepare(`SELECT * FROM compaction_log WHERE timestamp >= ? ORDER BY timestamp DESC`)
      .all(cutoff) as import("../types.js").CompactionLogRow[];

    const similarityThreshold = ctx.config.tiers.warm.similarity_threshold ?? 0.5;
    const metrics = computeMetrics(events, similarityThreshold, compactionRows, ctx.db);

    return { content: [{ type: "text", text: JSON.stringify({ days, events: events.length, metrics }) }] };
  }

  if (name === "eval_compare") {
    const beforeRef = args.before as string;
    const afterRef = (args.after as string) ?? "latest";
    const days = (args.days as number) ?? 30;

    const beforeId = resolveSnapshotId(ctx.db, beforeRef);
    if (!beforeId) {
      const available = ctx.db.prepare("SELECT name, id FROM config_snapshots ORDER BY timestamp DESC LIMIT 10").all();
      return {
        content: [{
          type: "text",
          text: JSON.stringify({ error: `Snapshot "${beforeRef}" not found`, available }),
        }],
        isError: true,
      };
    }

    const afterId = resolveSnapshotId(ctx.db, afterRef);
    if (!afterId) {
      return {
        content: [{
          type: "text",
          text: JSON.stringify({ error: `Snapshot "${afterRef}" not found` }),
        }],
        isError: true,
      };
    }

    const eventsBefore = getRetrievalEvents(ctx.db, { configSnapshotId: beforeId, days });
    const eventsAfter = getRetrievalEvents(ctx.db, { configSnapshotId: afterId, days });

    const threshold = ctx.config.tiers.warm.similarity_threshold;
    const comparison = computeComparisonMetrics(eventsBefore, eventsAfter, threshold);

    const response: Record<string, unknown> = {
      beforeSnapshot: beforeId,
      afterSnapshot: afterId,
      days,
      beforeEventCount: eventsBefore.length,
      afterEventCount: eventsAfter.length,
      ...comparison,
    };
    if (comparison.warning) {
      response.warning = comparison.warning;
    }

    return {
      content: [{
        type: "text",
        text: JSON.stringify(response),
      }],
    };
  }

  if (name === "eval_snapshot") {
    const snapshotName = args.name as string;
    const id = createConfigSnapshot(ctx.db, ctx.config, snapshotName);
    return {
      content: [{
        type: "text",
        text: JSON.stringify({ id, name: snapshotName, created: true }),
      }],
    };
  }

  return null;
}

export function registerEvalTools() {
  return EVAL_TOOLS;
}
