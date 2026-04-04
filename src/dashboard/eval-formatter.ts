import type { Metrics } from "../eval/metrics.js";

const LINE_WIDTH = 52;
const DIVIDER = "-".repeat(LINE_WIDTH);

function fmt(n: number, decimals = 2): string {
  return n.toFixed(decimals);
}

function padRight(s: string, width: number): string {
  return s.padEnd(width);
}

function padLeft(s: string | number, width: number): string {
  return String(s).padStart(width);
}

export function formatEvalReport(
  metrics: Metrics,
  _config?: { similarityThreshold: number },
): string {
  const lines: string[] = [];

  lines.push(`--- total-recall · Evaluation Report ${"-".repeat(LINE_WIDTH - 37)}`);
  lines.push("");

  // Overall metrics
  lines.push("  Retrieval Quality (rolling)");
  lines.push("  ---------------------------");
  lines.push(`  Precision@3:       ${fmt(metrics.precision)}`);
  lines.push(`  Hit rate:          ${fmt(metrics.hitRate)}`);
  lines.push(`  Miss rate:         ${fmt(metrics.missRate)}`);
  lines.push(`  MRR:               ${fmt(metrics.mrr)}`);
  lines.push(`  Avg latency:       ${Math.round(metrics.avgLatencyMs)}ms`);

  // By Tier
  if (Object.keys(metrics.byTier).length > 0) {
    lines.push("");
    lines.push("  By Tier");
    lines.push(
      `  ${padRight("", 10)}${padRight("Precision", 12)}${padRight("Hit Rate", 11)}${padRight("Avg Score", 12)}Queries`,
    );
    for (const [tier, tm] of Object.entries(metrics.byTier)) {
      const avgScoreStr = tm.avgScore > 0 ? fmt(tm.avgScore) : "N/A";
      lines.push(
        `  ${padRight(tier, 10)}${padRight(fmt(tm.precision), 12)}${padRight(fmt(tm.hitRate), 11)}${padRight(avgScoreStr, 12)}${tm.count}`,
      );
    }
  }

  // By Content Type
  if (Object.keys(metrics.byContentType).length > 0) {
    lines.push("");
    lines.push("  By Content Type");
    lines.push(
      `  ${padRight("", 16)}${padRight("Precision", 12)}${padRight("Hit Rate", 11)}Queries`,
    );
    for (const [ct, ctm] of Object.entries(metrics.byContentType)) {
      lines.push(
        `  ${padRight(ct, 16)}${padRight(fmt(ctm.precision), 12)}${padRight(fmt(ctm.hitRate), 11)}${ctm.count}`,
      );
    }
  }

  lines.push("");
  lines.push(`  Total events: ${metrics.totalEvents}`);
  lines.push("");
  lines.push(DIVIDER);

  return lines.join("\n");
}
