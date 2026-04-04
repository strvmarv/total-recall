import { randomUUID } from "node:crypto";
import type Database from "better-sqlite3";
import { listEntries, deleteEntry } from "../db/entries.js";
import { deleteEmbedding } from "../search/vector-search.js";
import { promoteEntry } from "../memory/promote-demote.js";
import { calculateDecayScore } from "../memory/decay.js";
import type { TotalRecallConfig } from "../types.js";

type EmbedFn = (text: string) => Float32Array;

export interface CompactHotTierResult {
  carryForward: string[];
  promoted: string[];
  discarded: string[];
}

function logCompactionEvent(
  db: Database.Database,
  opts: {
    sessionId: string;
    sourceTier: string;
    targetTier: string | null;
    sourceEntryIds: string[];
    targetEntryId: string | null;
    decayScores: Record<string, number>;
    reason: string;
    configSnapshotId: string;
  },
): void {
  const id = randomUUID();
  const timestamp = Date.now();
  db.prepare(`
    INSERT INTO compaction_log
      (id, timestamp, session_id, source_tier, target_tier, source_entry_ids,
       target_entry_id, semantic_drift, facts_preserved, facts_in_original,
       preservation_ratio, decay_scores, reason, config_snapshot_id)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `).run(
    id,
    timestamp,
    opts.sessionId,
    opts.sourceTier,
    opts.targetTier,
    JSON.stringify(opts.sourceEntryIds),
    opts.targetEntryId,
    null,
    null,
    null,
    null,
    JSON.stringify(opts.decayScores),
    opts.reason,
    opts.configSnapshotId,
  );
}

export function compactHotTier(
  db: Database.Database,
  embed: EmbedFn,
  config: TotalRecallConfig["compaction"],
  sessionId: string,
  configSnapshotId?: string,
): CompactHotTierResult {
  const snapshotId = configSnapshotId ?? "default";
  const entries = listEntries(db, "hot", "memory");
  const now = Date.now();

  const carryForward: string[] = [];
  const promoted: string[] = [];
  const discarded: string[] = [];

  for (const entry of entries) {
    const entryType =
      (entry.metadata?.entry_type as string | undefined) ?? "decision";

    const score = calculateDecayScore(
      {
        last_accessed_at: entry.last_accessed_at,
        created_at: entry.created_at,
        access_count: entry.access_count,
        type: entryType,
      },
      config,
      now,
    );

    if (score > config.promote_threshold) {
      carryForward.push(entry.id);
    } else if (score >= config.warm_threshold) {
      promoteEntry(db, embed, entry.id, "hot", "memory", "warm", "memory");
      promoted.push(entry.id);
      logCompactionEvent(db, {
        sessionId,
        sourceTier: "hot",
        targetTier: "warm",
        sourceEntryIds: [entry.id],
        targetEntryId: entry.id,
        decayScores: { [entry.id]: score },
        reason: "decay_score_below_promote_threshold",
        configSnapshotId: snapshotId,
      });
    } else {
      deleteEmbedding(db, "hot", "memory", entry.id);
      deleteEntry(db, "hot", "memory", entry.id);
      discarded.push(entry.id);
      logCompactionEvent(db, {
        sessionId,
        sourceTier: "hot",
        targetTier: null,
        sourceEntryIds: [entry.id],
        targetEntryId: null,
        decayScores: { [entry.id]: score },
        reason: "decay_score_below_warm_threshold",
        configSnapshotId: snapshotId,
      });
    }
  }

  return { carryForward, promoted, discarded };
}
