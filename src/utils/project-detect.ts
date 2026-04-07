import { execFileSync } from "node:child_process";
import { basename, parse as parsePath } from "node:path";
import { homedir } from "node:os";

/**
 * Detect the current project name from git remote or cwd basename.
 * Returns null if cwd is HOME or a filesystem root (not a project directory).
 */
export function detectProject(cwd: string): string | null {
  if (cwd === homedir()) return null;
  // path.parse(cwd).root is "/" on POSIX and "C:\\" (etc.) on Windows,
  // so this handles both platforms without hardcoding "/".
  if (cwd === parsePath(cwd).root) return null;

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
