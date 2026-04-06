import { existsSync, mkdirSync, readdirSync } from "node:fs";
import { readFileSync, statSync, createReadStream } from "node:fs";
import { writeFile, rename, unlink, readFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { getDataDir } from "../config.js";
import type { ModelSpec } from "./registry.js";

const HF_BASE_URL = "https://huggingface.co";

// Pin to a specific revision for reproducibility
const HF_REVISION = "main"; // Can be changed to a specific commit hash

/** Bundled model path (shipped with the package) */
function getBundledModelPath(modelName: string): string {
  const distDir = dirname(fileURLToPath(import.meta.url));
  return join(distDir, "..", "models", modelName);
}

/** User data model path (~/.total-recall/models/) */
function getUserModelPath(modelName: string): string {
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

async function validateDownload(modelPath: string): Promise<void> {
  // Check model.onnx exists and is substantial
  const modelStat = statSync(join(modelPath, "model.onnx"));
  if (modelStat.size < 1_000_000) {
    throw new Error("model.onnx appears corrupted (< 1MB)");
  }

  // Check tokenizer.json is valid JSON
  const tokenizerText = readFileSync(join(modelPath, "tokenizer.json"), "utf-8");
  try {
    JSON.parse(tokenizerText);
  } catch {
    throw new Error("tokenizer.json is not valid JSON");
  }
}

export async function downloadModel(modelName: string): Promise<string> {
  // Always download to user data dir, not bundled location
  const modelPath = getUserModelPath(modelName);
  mkdirSync(modelPath, { recursive: true });

  // model.onnx lives in onnx/ subdir, tokenizer files at repo root
  const fileUrls: Array<{ file: string; url: string }> = [
    {
      file: "model.onnx",
      url: `${HF_BASE_URL}/sentence-transformers/${modelName}/resolve/${HF_REVISION}/onnx/model.onnx`,
    },
    {
      file: "tokenizer.json",
      url: `${HF_BASE_URL}/sentence-transformers/${modelName}/resolve/${HF_REVISION}/tokenizer.json`,
    },
    {
      file: "tokenizer_config.json",
      url: `${HF_BASE_URL}/sentence-transformers/${modelName}/resolve/${HF_REVISION}/tokenizer_config.json`,
    },
  ];

  for (const { file, url } of fileUrls) {
    const dest = join(modelPath, file);

    const response = await fetch(url);
    if (!response.ok) {
      throw new Error(
        `Failed to download ${file} from ${url}: ${response.status} ${response.statusText}`,
      );
    }
    const buffer = await response.arrayBuffer();
    if (buffer.byteLength === 0) {
      throw new Error(`Downloaded ${file} is empty`);
    }
    await writeFile(dest, Buffer.from(buffer));
  }

  await validateDownload(modelPath);

  return modelPath;
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
