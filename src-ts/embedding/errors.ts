export type ModelNotReadyReason =
  | "missing"        // model not present, bootstrap not yet started
  | "downloading"    // bootstrap in progress
  | "failed"         // bootstrap attempted and failed (network, hash, etc.)
  | "corrupted";     // model present but failed validation (size or checksum)

export interface ModelNotReadyDetails {
  modelName: string;
  reason: ModelNotReadyReason;
  /** Optional human-readable hint, e.g., "downloaded 45MB / 90MB" or the underlying error message. */
  hint?: string;
  /** Underlying error if this was wrapped from a thrown error. */
  cause?: unknown;
}

export class ModelNotReadyError extends Error {
  public readonly modelName: string;
  public readonly reason: ModelNotReadyReason;
  public readonly hint?: string;
  public override readonly cause?: unknown;

  constructor(details: ModelNotReadyDetails) {
    const base = `Model '${details.modelName}' not ready: ${details.reason}`;
    const msg = details.hint ? `${base} (${details.hint})` : base;
    super(msg);
    this.name = "ModelNotReadyError";
    this.modelName = details.modelName;
    this.reason = details.reason;
    this.hint = details.hint;
    this.cause = details.cause;
  }
}

export function isModelNotReadyError(err: unknown): err is ModelNotReadyError {
  return err instanceof ModelNotReadyError || (
    typeof err === "object" && err !== null && (err as { name?: string }).name === "ModelNotReadyError"
  );
}
