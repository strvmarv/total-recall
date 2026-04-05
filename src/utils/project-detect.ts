import { execFileSync } from "node:child_process";
import { basename } from "node:path";

/**
 * Detect the current project name from git remote or cwd basename.
 * Returns null if cwd is HOME or root (not a project directory).
 */
export function detectProject(cwd: string): string | null {
  const home = process.env.HOME ?? "";
  if (cwd === home || cwd === "/") return null;

  try {
    const remote = execFileSync("git", ["remote", "get-url", "origin"], {
      cwd,
      timeout: 2000,
      stdio: ["pipe", "pipe", "pipe"],
    }).toString().trim();

    if (remote) {
      const name = basename(remote).replace(/\.git$/, "");
      if (name) return name;
    }
  } catch {
    // Not a git repo or no remote
  }

  return basename(cwd) || null;
}
