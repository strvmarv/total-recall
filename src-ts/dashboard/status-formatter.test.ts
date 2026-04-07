import { describe, it, expect } from "vitest";
import { formatStatusDashboard } from "./status-formatter.js";

describe("formatStatusDashboard", () => {
  it("produces a formatted TUI dashboard string", () => {
    const output = formatStatusDashboard({
      tiers: {
        hot: { memories: 14, knowledge: 2 },
        warm: { memories: 203, knowledge: 45 },
        cold: { memories: 1847, knowledge: 1203 },
      },
      dbSizeBytes: 25497600,
      embeddingModel: "all-MiniLM-L6-v2",
      embeddingDimensions: 384,
      sessionActivity: {
        retrievals: 12,
        used: 9,
        neutral: 2,
        negative: 1,
        memoriesCaptured: 3,
        kbQueries: 4,
        avgKbScore: 0.82,
      },
    });
    expect(output).toContain("total-recall");
    expect(output).toContain("Hot:");
    expect(output).toContain("Warm:");
    expect(output).toContain("24.3 MB");
    expect(output).toContain("75%");
  });

  it("works without session activity", () => {
    const output = formatStatusDashboard({
      tiers: {
        hot: { memories: 0, knowledge: 0 },
        warm: { memories: 0, knowledge: 0 },
        cold: { memories: 0, knowledge: 0 },
      },
      dbSizeBytes: 4096,
      embeddingModel: "test",
      embeddingDimensions: 384,
    });
    expect(output).toContain("total-recall");
    expect(output).not.toContain("Session Activity");
  });
});
