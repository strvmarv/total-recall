import { existsSync, mkdirSync, readdirSync } from "node:fs";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { getDataDir } from "../config.js";

const HF_BASE_URL = "https://huggingface.co";

export function getModelPath(modelName: string): string {
  return join(getDataDir(), "models", modelName);
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

export async function downloadModel(modelName: string): Promise<string> {
  const modelPath = getModelPath(modelName);
  mkdirSync(modelPath, { recursive: true });

  const files = ["model.onnx", "tokenizer.json", "tokenizer_config.json"];
  const repoUrl = `${HF_BASE_URL}/${modelName}/resolve/main`;

  for (const file of files) {
    const url = `${repoUrl}/${file}`;
    const dest = join(modelPath, file);

    const response = await fetch(url);
    if (!response.ok) {
      throw new Error(
        `Failed to download ${file} from ${url}: ${response.status} ${response.statusText}`,
      );
    }
    const buffer = await response.arrayBuffer();
    await writeFile(dest, Buffer.from(buffer));
  }

  return modelPath;
}
