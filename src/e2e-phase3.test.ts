import { describe, it, expect } from "vitest";
import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

describe("total-recall phase 3 integration", () => {
  it("has all plugin manifests", () => {
    expect(existsSync(resolve(".claude-plugin/plugin.json"))).toBe(true);
    expect(existsSync(resolve(".copilot-plugin/plugin.json"))).toBe(true);
    expect(existsSync(resolve(".cursor-plugin/plugin.json"))).toBe(true);
    expect(existsSync(resolve(".opencode/INSTALL.md"))).toBe(true);
  });

  it("has all skills", () => {
    const skills = ["commands", "using-total-recall"];
    for (const skill of skills) {
      const path = resolve(`skills/${skill}/SKILL.md`);
      expect(existsSync(path), `Missing skill: ${skill}`).toBe(true);
      const content = readFileSync(path, "utf-8");
      expect(content).toContain("---"); // Has frontmatter
      expect(content).toContain("name:"); // Has name field
      expect(content).toContain("description:"); // Has description field
    }
  });

  it("has hooks", () => {
    expect(existsSync(resolve("hooks/hooks.json"))).toBe(true);
    expect(existsSync(resolve("hooks/session-start/run.sh"))).toBe(true);
  });

  it("has compactor agent", () => {
    expect(existsSync(resolve("agents/compactor.md"))).toBe(true);
    const content = readFileSync(resolve("agents/compactor.md"), "utf-8");
    expect(content).toContain("name: compactor");
  });

  it("has README with superpowers attribution", () => {
    const readme = readFileSync(resolve("README.md"), "utf-8");
    expect(readme).toContain("superpowers");
    expect(readme).toContain("obra");
    expect(readme).toContain("total-recall");
  });

  it("has all MCP tools registered", () => {
    // Import registry to verify tool count
    // This is a type-level check more than runtime
    const registryPath = resolve("src/tools/registry.ts");
    const content = readFileSync(registryPath, "utf-8");
    expect(content).toContain("memory-tools");
    expect(content).toContain("system-tools");
    expect(content).toContain("registerKbTools");
    expect(content).toContain("registerEvalTools");
    expect(content).toContain("registerImportTools");
    expect(content).toContain("registerSessionTools");
  });

  it("has benchmark corpus and queries", () => {
    expect(existsSync(resolve("eval/corpus/memories.jsonl"))).toBe(true);
    expect(existsSync(resolve("eval/benchmarks/retrieval.jsonl"))).toBe(true);
  });
});
