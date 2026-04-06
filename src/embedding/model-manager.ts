import { existsSync, mkdirSync, readdirSync } from "node:fs";
import { statSync, createReadStream } from "node:fs";
import { writeFile, rename, unlink, readFile } from "node:fs/promises";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";
import { createWriteStream } from "node:fs";
import { createHash } from "node:crypto";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { getDataDir } from "../config.js";
import { getModelSpec, expandUrl } from "./registry.js";
import type { ModelSpec } from "./registry.js";

export interface DownloadProgress {
  /** Current filename being downloaded */
  file: string;
  /** Bytes downloaded so far for the current file */
  bytesDone: number;
  /** Total bytes for the current file (0 if unknown) */
  bytesTotal: number;
  /** 0-based index of the current file */
  fileIndex: number;
  /** Total number of files */
  fileCount: number;
}

export interface DownloadOptions {
  onProgress?: (p: DownloadProgress) => void;
  signal?: AbortSignal;
  /** Default: 3 */
  maxRetries?: number;
  /**
   * @internal — injectable sleep function for tests to skip backoff delays.
   * Defaults to a real setTimeout-based sleep.
   */
  _sleep?: (ms: number) => Promise<void>;
}

/** Bundled model path (shipped with the package) */
function getBundledModelPath(modelName: string): string {
  const distDir = dirname(fileURLToPath(import.meta.url));
  return join(distDir, "..", "models", modelName);
}

/** User data model path (~/.total-recall/models/) */
export function getUserModelPath(modelName: string): string {
  return join(getDataDir(), "models", modelName);
}

/**
 * Resolve model path: check bundled first, then user data dir.
 * Returns the first path where the model exists.
 */
export function getModelPath(modelName: string): string {
  const bundled = getBundledModelPath(modelName);
  if (isModelDownloaded(bundled)) return bundled;
  return getUserModelPath(modelName);
}

export function isModelDownloaded(modelPath: string): boolean {
  if (!existsSync(modelPath)) return false;
  try {
    const files = readdirSync(modelPath);
    return files.some((f) => f.endsWith(".onnx"));
  } catch {
    return false;
  }
}

/**
 * Sleep for `ms` milliseconds. Used for exponential backoff between retries.
 */
function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Download a single file with retries and progress reporting.
 *
 * If `response.body` is null/undefined (e.g. in tests returning a fake
 * response), the function falls back to `response.arrayBuffer()` and reports
 * progress once with bytesDone === bytesTotal === buffer length.
 */
async function downloadFile(
  url: string,
  dest: string,
  file: string,
  fileIndex: number,
  fileCount: number,
  options: DownloadOptions,
  maxRetries: number,
): Promise<void> {
  const { onProgress, signal, _sleep: sleepFn = sleep } = options;
  const tmpPath = `${dest}.tmp.${process.pid}.${Date.now()}`;

  let lastErr: unknown;
  for (let attempt = 0; attempt <= maxRetries; attempt++) {
    if (attempt > 0) {
      // Exponential backoff: 500ms, 1000ms, 2000ms (capped)
      const delayMs = Math.min(500 * Math.pow(2, attempt - 1), 2000);
      await sleepFn(delayMs);
    }

    try {
      const response = await fetch(url, { signal });

      if (!response.ok) {
        throw new Error(
          `Failed to download ${file} from ${url}: ${response.status} ${response.statusText}`,
        );
      }

      if (response.body) {
        // Streaming path: pipe response body to tmp file, tracking bytes.
        const contentLengthRaw = response.headers.get("content-length");
        const bytesTotal = contentLengthRaw ? parseInt(contentLengthRaw, 10) : 0;
        let bytesDone = 0;

        const nodeReadable = Readable.fromWeb(
          response.body as Parameters<typeof Readable.fromWeb>[0],
        );

        const writeStream = createWriteStream(tmpPath);

        nodeReadable.on("data", (chunk: Buffer) => {
          bytesDone += chunk.byteLength;
          onProgress?.({ file, bytesDone, bytesTotal, fileIndex, fileCount });
        });

        try {
          await pipeline(nodeReadable, writeStream);
        } catch (err) {
          // Best-effort cleanup of tmp file
          try { await unlink(tmpPath); } catch { /* ignore */ }
          throw err;
        }
      } else {
        // Fallback path (e.g. test fake responses with body: null):
        // Read the full buffer and report progress once.
        const buffer = Buffer.from(await response.arrayBuffer());
        const bytesDone = buffer.byteLength;
        const bytesTotal = buffer.byteLength;

        try {
          await writeFile(tmpPath, buffer);
        } catch (err) {
          try { await unlink(tmpPath); } catch { /* ignore */ }
          throw err;
        }

        onProgress?.({ file, bytesDone, bytesTotal, fileIndex, fileCount });
      }

      // Success: rename tmp → final destination
      if (existsSync(dest)) {
        try { await unlink(dest); } catch { /* ignore */ }
      }
      await rename(tmpPath, dest);
      return;
    } catch (err) {
      // AbortError must not be retried
      if (err instanceof Error && err.name === "AbortError") {
        try { await unlink(tmpPath); } catch { /* ignore */ }
        throw err;
      }
      lastErr = err;
      // Clean up tmp if it exists before retrying
      try { await unlink(tmpPath); } catch { /* ignore */ }
    }
  }

  throw lastErr;
}

/**
 * Download all files for a model, verify the sha256 of model.onnx, and write
 * a `.verified` sidecar so future `isModelChecksumValid` calls hit the cache.
 *
 * Downloads are atomic (written to a .tmp file, then renamed). Each file is
 * retried up to `maxRetries` times (default 3) with exponential backoff.
 * AbortSignal is honoured and propagates immediately without retry.
 */
export async function downloadModel(modelName: string, options: DownloadOptions = {}): Promise<string> {
  const { maxRetries = 3 } = options;

  // 1. Look up spec — throws if unknown model
  const spec = getModelSpec(modelName);

  // 2. Ensure target directory exists
  const target = getUserModelPath(modelName);
  mkdirSync(target, { recursive: true });

  // 3. Build ordered file list from spec
  const fileEntries = Object.entries(spec.files).map(([file, urlTemplate]) => ({
    file,
    url: expandUrl(urlTemplate, spec.revision),
  }));
  const fileCount = fileEntries.length;

  // 4. Download each file sequentially
  for (let i = 0; i < fileEntries.length; i++) {
    const { file, url } = fileEntries[i]!;
    const finalPath = join(target, file);
    await downloadFile(url, finalPath, file, i, fileCount, options, maxRetries);
  }

  // 5. Verify sha256 of model.onnx (NOT retried)
  const onnxPath = join(target, "model.onnx");
  const actualHash = await sha256File(onnxPath);
  if (actualHash !== spec.sha256) {
    // Remove bad model.onnx so a retry will start clean
    try { await unlink(onnxPath); } catch { /* ignore */ }
    throw new Error(
      `sha256 mismatch for model.onnx: expected ${spec.sha256}, actual ${actualHash}`,
    );
  }

  // 6. Write .verified sidecar atomically
  const sidecarPath = join(target, ".verified");
  await writeFileAtomic(sidecarPath, spec.sha256);

  return target;
}

export async function sha256File(path: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const hash = createHash("sha256");
    const stream = createReadStream(path);
    stream.on("data", (chunk) => hash.update(chunk));
    stream.on("end", () => resolve(hash.digest("hex")));
    stream.on("error", reject);
  });
}

export async function writeFileAtomic(dest: string, data: Buffer | string): Promise<void> {
  const tmp = `${dest}.tmp.${process.pid}.${Date.now()}`;
  try {
    await writeFile(tmp, data);
    // On Windows, rename fails if dest exists; remove first.
    if (existsSync(dest)) {
      try { await unlink(dest); } catch { /* ignore */ }
    }
    await rename(tmp, dest);
  } catch (err) {
    try { await unlink(tmp); } catch { /* ignore */ }
    throw err;
  }
}

export function isModelStructurallyValid(modelPath: string, spec: ModelSpec): boolean {
  if (!existsSync(modelPath)) return false;
  for (const file of Object.keys(spec.files)) {
    const p = join(modelPath, file);
    if (!existsSync(p)) return false;
  }
  try {
    const onnx = join(modelPath, "model.onnx");
    const size = statSync(onnx).size;
    return size === spec.sizeBytes;
  } catch {
    return false;
  }
}

export async function isModelChecksumValid(modelPath: string, spec: ModelSpec): Promise<boolean> {
  const sidecarPath = join(modelPath, ".verified");

  // If sidecar exists and matches spec.sha256, skip re-hashing
  if (existsSync(sidecarPath)) {
    try {
      const cached = (await readFile(sidecarPath, "utf8")).trim();
      if (cached === spec.sha256) return true;
    } catch {
      // Fall through to hash check
    }
  }

  // Compute hash of model.onnx
  const onnxPath = join(modelPath, "model.onnx");
  if (!existsSync(onnxPath)) return false;

  let computed: string;
  try {
    computed = await sha256File(onnxPath);
  } catch {
    return false;
  }

  if (computed === spec.sha256) {
    await writeFileAtomic(sidecarPath, spec.sha256);
    return true;
  }

  return false;
}
