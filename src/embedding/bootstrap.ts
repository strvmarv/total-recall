import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { getModelSpec, expandUrl } from "./registry.js";
import { getModelPath, getUserModelPath, isModelStructurallyValid, isModelChecksumValid, downloadModel } from "./model-manager.js";
import { ModelNotReadyError } from "./errors.js";
import * as lockfile from "proper-lockfile";
import type { ModelSpec } from "./registry.js";
import type { DownloadProgress } from "./model-manager.js";

export type BootstrapState =
  | "idle"
  | "checking"
  | "downloading"
  | "ready"
  | "failed";

export interface BootstrapStatus {
  state: BootstrapState;
  modelName: string;
  /** Resolved path to the model directory once ready */
  modelPath?: string;
  /** Most recent download progress, while downloading. Kept after ready so callers can inspect it. */
  progress?: DownloadProgress;
  /** Set when state === "failed" */
  error?: { reason: "missing" | "downloading" | "failed" | "corrupted"; message: string };
}

export interface LockHandle {
  release: () => Promise<void>;
}

export type LockAcquirer = (targetDir: string) => Promise<LockHandle>;

const defaultAcquireLock: LockAcquirer = async (targetDir: string) => {
  // Ensure directory exists before proper-lockfile tries to create the lockfile
  mkdirSync(targetDir, { recursive: true });
  const release = await lockfile.lock(targetDir, {
    lockfilePath: join(targetDir, ".bootstrap.lock"),
    retries: { retries: 60, minTimeout: 1000, maxTimeout: 1000 },
  });
  return { release: async () => { await release(); } };
};

export interface BootstrapOptions {
  /** Override for tests; defaults to real registry getModelSpec */
  getSpec?: (name: string) => ModelSpec;
  /** Override for tests; defaults to real getModelPath */
  getModelPath?: (name: string) => string;
  /** Override for tests; defaults to real isModelStructurallyValid */
  isStructurallyValid?: (modelPath: string, spec: ModelSpec) => boolean;
  /** Override for tests; defaults to real isModelChecksumValid */
  isChecksumValid?: (modelPath: string, spec: ModelSpec) => Promise<boolean>;
  /** Override for tests; defaults to real downloadModel */
  download?: (
    modelName: string,
    options: { onProgress?: (p: DownloadProgress) => void }
  ) => Promise<string>;
  /** Override for tests; defaults to proper-lockfile-based acquirer */
  acquireLock?: LockAcquirer;
  /** Override for tests; defaults to real getUserModelPath from model-manager */
  getUserModelPath?: (name: string) => string;
}

/**
 * Classify a download error into a reason code.
 * SHA-256 / checksum errors are treated as "corrupted"; everything else is "failed".
 */
function classifyDownloadError(err: unknown): "failed" | "corrupted" {
  const msg = err instanceof Error ? err.message : String(err);
  if (/sha256|checksum|hash/i.test(msg)) return "corrupted";
  return "failed";
}

/**
 * Build a short multi-line hint string that tells the user how to install the
 * model manually. Accepts the resolved modelPath as a parameter so it can be
 * tested without touching the filesystem.
 */
export function buildManualInstallHint(
  modelName: string,
  spec: ModelSpec,
  modelPath: string,
): string {
  const lines: string[] = [
    `To install ${modelName} manually, place these files in ${modelPath}:`,
  ];
  for (const [filename, urlTemplate] of Object.entries(spec.files)) {
    const url = expandUrl(urlTemplate, spec.revision);
    lines.push(`  - ${filename} : ${url}`);
  }
  lines.push("Then retry session_start.");
  return lines.join("\n");
}

export class ModelBootstrap {
  private status: BootstrapStatus;
  private inFlight: Promise<string> | null = null;

  private readonly getSpec: (name: string) => ModelSpec;
  private readonly getModelPath: (name: string) => string;
  private readonly getUserModelPath: (name: string) => string;
  private readonly isStructurallyValid: (modelPath: string, spec: ModelSpec) => boolean;
  private readonly isChecksumValid: (modelPath: string, spec: ModelSpec) => Promise<boolean>;
  private readonly download: (
    modelName: string,
    options: { onProgress?: (p: DownloadProgress) => void }
  ) => Promise<string>;
  private readonly acquireLock: LockAcquirer;

  constructor(modelName: string, options?: BootstrapOptions) {
    this.status = { state: "idle", modelName };
    this.getSpec = options?.getSpec ?? getModelSpec;
    this.getModelPath = options?.getModelPath ?? getModelPath;
    this.getUserModelPath = options?.getUserModelPath ?? getUserModelPath;
    this.isStructurallyValid = options?.isStructurallyValid ?? isModelStructurallyValid;
    this.isChecksumValid = options?.isChecksumValid ?? isModelChecksumValid;
    this.download = options?.download ?? ((name, opts) => downloadModel(name, opts));
    this.acquireLock = options?.acquireLock ?? defaultAcquireLock;
  }

  getStatus(): BootstrapStatus {
    return { ...this.status };
  }

  /**
   * Trigger or observe bootstrap.
   * - If already ready: returns immediately with the cached path.
   * - If idle/checking: runs the validate-and-maybe-download flow once.
   * - If currently downloading from a previous call: returns the in-flight promise (single-flight).
   * - retry on re-call: if state === "failed", reset to "idle" and retry from scratch.
   */
  ensureReady(): Promise<string> {
    // Short-circuit if already ready
    if (this.status.state === "ready") {
      return Promise.resolve(this.status.modelPath!);
    }

    // retry on re-call: reset failed state so the bootstrap runs again from scratch
    if (this.status.state === "failed") {
      this.status.state = "idle";
      delete this.status.error;
      this.inFlight = null;
    }

    // Single-flight: return the existing in-flight promise if downloading
    if (this.status.state === "downloading" && this.inFlight !== null) {
      return this.inFlight;
    }

    // Run the bootstrap flow
    this.inFlight = this._runBootstrap();
    return this.inFlight;
  }

  private async _runBootstrap(): Promise<string> {
    const { modelName } = this.status;

    this.status.state = "checking";

    const spec = this.getSpec(modelName);
    const modelPath = this.getModelPath(modelName);
    const userModelPath = this.getUserModelPath(modelName);

    const structuralOk = this.isStructurallyValid(modelPath, spec);
    const checksumOk = structuralOk && await this.isChecksumValid(modelPath, spec);

    if (structuralOk && checksumOk) {
      this.status.state = "ready";
      this.status.modelPath = modelPath;
      this.inFlight = null;
      return modelPath;
    }

    // Need to download — acquire the cross-process lock first
    this.status.state = "downloading";

    let lockHandle: LockHandle;
    try {
      lockHandle = await this.acquireLock(userModelPath);
    } catch (err) {
      // Lock acquisition failed (e.g. timeout after 60s waiting for another process)
      this.status.state = "failed";
      const errMsg = err instanceof Error ? err.message : String(err);
      this.status.error = { reason: "failed", message: errMsg };
      this.inFlight = null;
      const hint = "Another process may be downloading the model; retry shortly.";
      throw new ModelNotReadyError({ modelName, reason: "failed", hint, cause: err });
    }

    try {
      // Post-lock re-check: another process may have completed the download while we waited
      const postLockStructuralOk = this.isStructurallyValid(modelPath, spec);
      const postLockChecksumOk = postLockStructuralOk && await this.isChecksumValid(modelPath, spec);

      if (postLockStructuralOk && postLockChecksumOk) {
        this.status.state = "ready";
        this.status.modelPath = modelPath;
        this.inFlight = null;
        return modelPath;
      }

      // Proceed with actual download
      const resolvedPath = await this.download(modelName, {
        onProgress: (p: DownloadProgress) => {
          this.status.progress = p;
        },
      });

      this.status.state = "ready";
      this.status.modelPath = resolvedPath;
      this.inFlight = null;
      return resolvedPath;
    } catch (err) {
      const reason = classifyDownloadError(err);
      this.status.state = "failed";
      this.status.error = {
        reason,
        message: err instanceof Error ? err.message : String(err),
      };
      this.inFlight = null;

      const hint = buildManualInstallHint(modelName, spec, modelPath);
      throw new ModelNotReadyError({ modelName, reason, hint, cause: err });
    } finally {
      await lockHandle.release();
    }
  }
}
