import { randomUUID } from "node:crypto";
import type Database from "better-sqlite3";
import { listEntries } from "../db/entries.js";
import { demoteEntry } from "../memory/promote-demote.js";

type EmbedFn = (text: string) => Float32Array | Promise<Float32Array>;

export interface SweepWarmTierResult {
  demoted: string[];
  kept: string[];
}

export async function sweepWarmTier(
  db: Database.Database,
  embed: EmbedFn,
  config: { coldDecayDays: number },
  sessionId: string,
): Promise<SweepWarmTierResult> {
  const entries = listEntries(db, "warm", "memory");
  const now = Date.now();
  const coldDecayMs = config.coldDecayDays * 24 * 60 * 60 * 1000;

  const demoted: string[] = [];
  const kept: string[] = [];

  for (const entry of entries) {
    const age = now - entry.last_accessed_at;
    if (age > coldDecayMs && entry.access_count === 0) {
      await demoteEntry(db, embed, entry.id, "warm", "memory", "cold", "memory");

      db.prepare(`
        INSERT INTO compaction_log
          (id, timestamp, session_id, source_tier, target_tier, source_entry_ids,
           target_entry_id, decay_scores, reason, config_snapshot_id)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
      `).run(
        randomUUID(), now, sessionId, "warm", "cold",
        JSON.stringify([entry.id]), entry.id,
        JSON.stringify([entry.decay_score]),
        "warm_sweep_decay", "default",
      );

      demoted.push(entry.id);
    } else {
      kept.push(entry.id);
    }
  }

  return { demoted, kept };
}
