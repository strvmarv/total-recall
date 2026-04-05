import type { Tier, ContentType } from "../types.js";
import { resolve } from "node:path";

const VALID_TIERS = new Set<string>(["hot", "warm", "cold"]);
const VALID_CONTENT_TYPES = new Set<string>(["memory", "knowledge"]);
const VALID_ENTRY_TYPES = new Set<string>(["correction", "preference", "decision", "surfaced"]);
const MAX_CONTENT_LENGTH = 100_000; // 100KB

export function validateString(value: unknown, name: string): string {
  if (typeof value !== "string" || value.length === 0) {
    throw new Error(`${name} must be a non-empty string`);
  }
  return value;
}

export function validateOptionalString(value: unknown, name: string): string | undefined {
  if (value === undefined || value === null) return undefined;
  if (typeof value !== "string") throw new Error(`${name} must be a string`);
  return value;
}

export function validateTier(value: unknown): Tier {
  if (!VALID_TIERS.has(value as string)) {
    throw new Error(`Invalid tier: ${String(value)}. Must be hot, warm, or cold`);
  }
  return value as Tier;
}

export function validateContentType(value: unknown): ContentType {
  if (!VALID_CONTENT_TYPES.has(value as string)) {
    throw new Error(`Invalid content type: ${String(value)}. Must be memory or knowledge`);
  }
  return value as ContentType;
}

export function validateEntryType(value: unknown): string | undefined {
  if (value === undefined || value === null) return undefined;
  if (!VALID_ENTRY_TYPES.has(value as string)) {
    throw new Error(`Invalid entry type: ${String(value)}`);
  }
  return value as string;
}

export function validateContent(value: unknown): string {
  const content = validateString(value, "content");
  if (content.length > MAX_CONTENT_LENGTH) {
    throw new Error(`Content exceeds maximum length of ${MAX_CONTENT_LENGTH} characters`);
  }
  return content;
}

export function validateNumber(value: unknown, name: string, min?: number, max?: number): number {
  if (typeof value !== "number" || isNaN(value)) {
    throw new Error(`${name} must be a number`);
  }
  if (min !== undefined && value < min) throw new Error(`${name} must be >= ${min}`);
  if (max !== undefined && value > max) throw new Error(`${name} must be <= ${max}`);
  return value;
}

export function validateOptionalNumber(value: unknown, name: string, min?: number, max?: number): number | undefined {
  if (value === undefined || value === null) return undefined;
  return validateNumber(value, name, min, max);
}

export function validateTags(value: unknown): string[] {
  if (value === undefined || value === null) return [];
  if (!Array.isArray(value)) throw new Error("tags must be an array");
  return value.map((v, i) => {
    if (typeof v !== "string") throw new Error(`tags[${i}] must be a string`);
    return v;
  });
}

export function validatePath(value: unknown, name: string): string {
  const path = validateString(value, name);
  const resolved = resolve(path);

  // Block obvious dangerous paths
  const dangerous = ["/etc", "/proc", "/sys", "/dev", "/var/run", "/root"];
  for (const prefix of dangerous) {
    if (resolved.startsWith(prefix)) {
      throw new Error(`Access denied: ${name} cannot access ${prefix}`);
    }
  }

  // Block hidden files starting with . (except .env.example-like patterns)
  const basename = resolved.split("/").pop() ?? "";
  if (basename === ".env" || basename === ".credentials.json") {
    throw new Error(`Access denied: ${name} cannot access sensitive files`);
  }

  return resolved;
}
