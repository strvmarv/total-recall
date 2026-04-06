import { readFileSync, writeFileSync, existsSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { createHash, randomUUID } from "node:crypto";
import { parse as parseToml, stringify as stringifyToml } from "@iarna/toml";
import type { TotalRecallConfig } from "./types.js";
import type Database from "better-sqlite3";

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

export function setNestedKey(
  obj: Record<string, unknown>,
  dotKey: string,
  value: unknown,
): Record<string, unknown> {
  const result = { ...obj };
  const parts = dotKey.split(".");
  let current: Record<string, unknown> = result;

  for (let i = 0; i < parts.length - 1; i++) {
    const part = parts[i]!;
    if (!isSafeKey(part)) {
      throw new TypeError(`Invalid config key segment: "${part}"`);
    }
    if (typeof current[part] !== "object" || current[part] === null) {
      current[part] = {};
    } else {
      current[part] = { ...(current[part] as Record<string, unknown>) };
    }
    current = current[part] as Record<string, unknown>;
  }

  const lastKey = parts[parts.length - 1]!;
  if (!isSafeKey(lastKey)) {
    throw new TypeError(`Invalid config key segment: "${lastKey}"`);
  }
  current[lastKey] = value;
  return result;
}

export function saveUserConfig(overrides: Record<string, unknown>): void {
  const dataDir = getDataDir();
  mkdirSync(dataDir, { recursive: true });
  const configPath = join(dataDir, "config.toml");

  let existing: Record<string, unknown> = {};
  if (existsSync(configPath)) {
    existing = parseToml(readFileSync(configPath, "utf-8")) as Record<string, unknown>;
  }

  const merged = deepMerge(existing, overrides);
  writeFileSync(configPath, stringifyToml(merged as Parameters<typeof stringifyToml>[0]));
}

function sortKeysDeep(obj: unknown): unknown {
  if (obj === null || typeof obj !== "object") return obj;
  if (Array.isArray(obj)) return obj.map(sortKeysDeep);
  const sorted: Record<string, unknown> = {};
  for (const key of Object.keys(obj as Record<string, unknown>).sort()) {
    sorted[key] = sortKeysDeep((obj as Record<string, unknown>)[key]);
  }
  return sorted;
}

function hashConfig(config: unknown): string {
  return createHash("sha256")
    .update(JSON.stringify(sortKeysDeep(config)))
    .digest("hex");
}

export function createConfigSnapshot(
  db: Database.Database,
  config: unknown,
  name?: string,
): string {
  const configJson = JSON.stringify(config);
  const configHash = hashConfig(config);

  const latest = db.prepare(
    "SELECT id, config FROM config_snapshots ORDER BY timestamp DESC LIMIT 1",
  ).get() as { id: string; config: string } | undefined;

  if (latest && hashConfig(JSON.parse(latest.config)) === configHash) {
    return latest.id;
  }

  const id = randomUUID();
  db.prepare(
    "INSERT INTO config_snapshots (id, name, timestamp, config) VALUES (?, ?, ?, ?)",
  ).run(id, name ?? null, Date.now(), configJson);

  return id;
}

function isSafeKey(key: string): boolean {
  return key !== "__proto__" && key !== "constructor" && key !== "prototype";
}

function deepMerge(
  target: Record<string, unknown>,
  source: Record<string, unknown>,
): Record<string, unknown> {
  const result = { ...target };
  for (const key of Object.keys(source)) {
    if (!isSafeKey(key)) continue;
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
