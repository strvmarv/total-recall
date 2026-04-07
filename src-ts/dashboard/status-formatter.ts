export interface StatusData {
  tiers: {
    hot: { memories: number; knowledge: number };
    warm: { memories: number; knowledge: number };
    cold: { memories: number; knowledge: number };
  };
  dbSizeBytes: number;
  embeddingModel: string;
  embeddingDimensions: number;
  sessionActivity?: {
    retrievals: number;
    used: number;
    neutral: number;
    negative: number;
    memoriesCaptured: number;
    kbQueries: number;
    avgKbScore: number;
  };
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function padLeft(value: string | number, width: number): string {
  const s = String(value);
  return s.padStart(width);
}

const LINE_WIDTH = 52;
const DIVIDER = "-".repeat(LINE_WIDTH);

export function formatStatusDashboard(data: StatusData): string {
  const lines: string[] = [];

  lines.push(`--- total-recall ${"-".repeat(LINE_WIDTH - 18)}`);
  lines.push("");

  // Tiers and Knowledge Base side by side
  const hotTotal = data.tiers.hot.memories + data.tiers.hot.knowledge;
  const warmTotal = data.tiers.warm.memories + data.tiers.warm.knowledge;
  const coldTotal = data.tiers.cold.memories + data.tiers.cold.knowledge;
  const totalMemories =
    data.tiers.hot.memories + data.tiers.warm.memories + data.tiers.cold.memories;
  const totalKnowledge =
    data.tiers.hot.knowledge + data.tiers.warm.knowledge + data.tiers.cold.knowledge;

  lines.push("  Tiers                          Knowledge Base");
  lines.push("  -----                          --------------");
  lines.push(
    `  Hot:  ${padLeft(hotTotal + " entries", 12)}           Memories:  ${totalMemories}`,
  );
  lines.push(
    `  Warm: ${padLeft(warmTotal + " entries", 12)}           Knowledge: ${totalKnowledge}`,
  );
  lines.push(`  Cold: ${padLeft(coldTotal + " entries", 12)}`);
  lines.push("");

  const dbStr = formatBytes(data.dbSizeBytes);
  const modelStr = `${data.embeddingModel} (${data.embeddingDimensions}d)`;
  lines.push(`  DB: ${dbStr}  |  Model: ${modelStr}`);

  if (data.sessionActivity) {
    const sa = data.sessionActivity;
    lines.push("");
    lines.push("  Session Activity");
    lines.push("  ----------------");

    const usedPct =
      sa.retrievals > 0 ? Math.round((sa.used / sa.retrievals) * 100) : 0;

    lines.push(`  Retrievals this session:  ${sa.retrievals}`);
    lines.push(`  |- Used by LLM:           ${padLeft(sa.used, 2)}  (${usedPct}%)`);
    lines.push(`  |- Neutral:               ${padLeft(sa.neutral, 2)}`);
    lines.push(
      `  \\- Negative signal:       ${padLeft(sa.negative, 2)}${sa.negative > 0 ? "  !" : ""}`,
    );
    lines.push("");

    const avgScoreStr = sa.avgKbScore.toFixed(2);
    lines.push(`  Memories captured:          ${sa.memoriesCaptured}`);
    lines.push(
      `  KB queries:                 ${sa.kbQueries}  (avg score: ${avgScoreStr})`,
    );
  }

  lines.push("");
  lines.push(DIVIDER);

  return lines.join("\n");
}
