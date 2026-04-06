import { getModelSpec } from "./registry.js";
import { getModelPath, isModelStructurallyValid, isModelChecksumValid, downloadModel } from "./model-manager.js";
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
}

export class ModelBootstrap {
  private status: BootstrapStatus;
  private inFlight: Promise<string> | null = null;

  private readonly getSpec: (name: string) => ModelSpec;
  private readonly getModelPath: (name: string) => string;
  private readonly isStructurallyValid: (modelPath: string, spec: ModelSpec) => boolean;
  private readonly isChecksumValid: (modelPath: string, spec: ModelSpec) => Promise<boolean>;
  private readonly download: (
    modelName: string,
    options: { onProgress?: (p: DownloadProgress) => void }
  ) => Promise<string>;

  constructor(modelName: string, options?: BootstrapOptions) {
    this.status = { state: "idle", modelName };
    this.getSpec = options?.getSpec ?? getModelSpec;
    this.getModelPath = options?.getModelPath ?? getModelPath;
    this.isStructurallyValid = options?.isStructurallyValid ?? isModelStructurallyValid;
    this.isChecksumValid = options?.isChecksumValid ?? isModelChecksumValid;
    this.download = options?.download ?? ((name, opts) => downloadModel(name, opts));
  }

  getStatus(): BootstrapStatus {
    return { ...this.status };
  }

  /**
   * Trigger or observe bootstrap.
   * - If already ready: returns immediately with the cached path.
   * - If idle/checking: runs the validate-and-maybe-download flow once.
   * - If currently downloading from a previous call: returns the in-flight promise (single-flight).
   */
  ensureReady(): Promise<string> {
    // Short-circuit if already ready
    if (this.status.state === "ready") {
      return Promise.resolve(this.status.modelPath!);
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

    const structuralOk = this.isStructurallyValid(modelPath, spec);
    const checksumOk = structuralOk && await this.isChecksumValid(modelPath, spec);

    if (structuralOk && checksumOk) {
      this.status.state = "ready";
      this.status.modelPath = modelPath;
      this.inFlight = null;
      return modelPath;
    }

    // Need to download
    this.status.state = "downloading";

    try {
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
      this.status.state = "failed";
      this.status.error = {
        reason: "failed",
        message: err instanceof Error ? err.message : String(err),
      };
      this.inFlight = null;
      throw err;
    }
  }
}
