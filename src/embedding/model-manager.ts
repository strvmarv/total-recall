import { existsSync, mkdirSync, readdirSync } from "node:fs";
import { readFileSync, statSync } from "node:fs";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { getDataDir } from "../config.js";

const HF_BASE_URL = "https://huggingface.co";

// Pin to a specific revision for reproducibility
const HF_REVISION = "main"; // Can be changed to a specific commit hash

/** Bundled model path (shipped with the package) */
function getBundledModelPath(modelName: string): string {
  const distDir = new URL(".", import.meta.url).pathname;
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
