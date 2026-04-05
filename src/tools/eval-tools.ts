import { resolve } from "node:path";
import { fileURLToPath } from "node:url";
import type { ToolContext } from "./registry.js";
import { runBenchmark } from "../eval/benchmark-runner.js";
import { getRetrievalEvents } from "../eval/event-logger.js";
import { computeMetrics } from "../eval/metrics.js";

const __dirname = fileURLToPath(new URL(".", import.meta.url));
// Package root is two levels up from src/tools/
const PACKAGE_ROOT = resolve(__dirname, "..", "..");

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
];

type ToolResult = { content: Array<{ type: "text"; text: string }> };

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

    const similarityThreshold = ctx.config.tiers.warm.similarity_threshold ?? 0.5;
    const metrics = computeMetrics(events, similarityThreshold);

    return { content: [{ type: "text", text: JSON.stringify({ days, events: events.length, metrics }) }] };
  }

  return null;
}

export function registerEvalTools() {
  return EVAL_TOOLS;
}
