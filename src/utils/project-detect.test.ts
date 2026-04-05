import { describe, it, expect } from "vitest";
import { mkdtempSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { execFileSync } from "node:child_process";
import { detectProject } from "./project-detect.js";

describe("detectProject", () => {
  it("returns null for home directory", () => {
    expect(detectProject(process.env.HOME ?? "/home/user")).toBeNull();
  });

  it("returns null for root", () => {
    expect(detectProject("/")).toBeNull();
  });

  it("returns basename for non-git directory", () => {
    const dir = mkdtempSync(join(tmpdir(), "tr-project-"));
    try {
      const result = detectProject(dir);
      expect(result).toBeTruthy();
      expect(typeof result).toBe("string");
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });

  it("returns git remote name for git repos", () => {
    const dir = mkdtempSync(join(tmpdir(), "tr-git-project-"));
    try {
      execFileSync("git", ["init"], { cwd: dir, stdio: "pipe" });
      execFileSync("git", ["remote", "add", "origin", "https://github.com/test/my-project.git"], { cwd: dir, stdio: "pipe" });
      expect(detectProject(dir)).toBe("my-project");
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});
