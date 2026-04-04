import type Database from "better-sqlite3";
import { listEntries } from "../db/entries.js";
import { demoteEntry } from "../memory/promote-demote.js";

type EmbedFn = (text: string) => Float32Array;

export interface SweepWarmTierResult {
  demoted: string[];
  kept: string[];
}

export function sweepWarmTier(
  db: Database.Database,
  embed: EmbedFn,
  config: { coldDecayDays: number },
  sessionId: string,
): SweepWarmTierResult {
  const entries = listEntries(db, "warm", "memory");
  const now = Date.now();
  const coldDecayMs = config.coldDecayDays * 24 * 60 * 60 * 1000;

  const demoted: string[] = [];
  const kept: string[] = [];

  for (const entry of entries) {
    const age = now - entry.last_accessed_at;
    if (age > coldDecayMs && entry.access_count === 0) {
      demoteEntry(db, embed, entry.id, "warm", "memory", "cold", "memory");
      demoted.push(entry.id);
    } else {
      kept.push(entry.id);
    }
  }

  return { demoted, kept };
}
