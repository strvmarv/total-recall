import { readFileSync, existsSync } from "node:fs";
import { join } from "node:path";
import { parse as parseToml } from "@iarna/toml";
import type { TotalRecallConfig } from "./types.js";

const DEFAULTS_PATH = new URL("./defaults.toml", import.meta.url);

export function getDataDir(): string {
  return (
    process.env.TOTAL_RECALL_HOME ??
    join(process.env.HOME ?? "~", ".total-recall")
  );
}

export function loadConfig(): TotalRecallConfig {
  const defaultsText = readFileSync(DEFAULTS_PATH, "utf-8");
  const defaults = parseToml(defaultsText) as unknown as TotalRecallConfig;

  const userConfigPath = join(getDataDir(), "config.toml");
  if (existsSync(userConfigPath)) {
    const userText = readFileSync(userConfigPath, "utf-8");
    const userConfig = parseToml(userText) as Record<string, unknown>;
    return deepMerge(defaults as unknown as Record<string, unknown>, userConfig) as unknown as TotalRecallConfig;
  }

  return defaults;
}

function deepMerge(
  target: Record<string, unknown>,
  source: Record<string, unknown>,
): Record<string, unknown> {
  const result = { ...target };
  for (const key of Object.keys(source)) {
    if (
      source[key] !== null &&
      typeof source[key] === "object" &&
      !Array.isArray(source[key]) &&
      typeof target[key] === "object" &&
      target[key] !== null
    ) {
      result[key] = deepMerge(
        target[key] as Record<string, unknown>,
        source[key] as Record<string, unknown>,
      );
    } else {
      result[key] = source[key];
    }
  }
  return result;
}
