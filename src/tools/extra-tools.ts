import { mkdirSync, writeFileSync, readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import type { ToolContext } from "./registry.js";
import { compactHotTier } from "../compaction/compactor.js";
import { getMemory } from "../memory/get.js";
import { listEntries, insertEntry } from "../db/entries.js";
import { getDataDir } from "../config.js";
import { ALL_TABLE_PAIRS } from "../types.js";
import type { Tier, ContentType, CompactionLogRow } from "../types.js";
import { validateString, validatePath, validateOptionalNumber, coerceStringArray } from "./validation.js";
import { insertEmbedding } from "../search/vector-search.js";

export function registerExtraTools() {
  return [
    {
      name: "compact_now",
      description: "Force compaction of a memory tier immediately",
      inputSchema: {
        type: "object" as const,
        properties: {
          tier: {
            type: "string",
            enum: ["hot"],
            description: "Tier to compact (currently only hot supported)",
          },
        },
        required: [],
      },
    },
    {
      name: "memory_inspect",
      description: "Deep dive on a single memory entry, including its compaction history",
      inputSchema: {
        type: "object" as const,
        properties: {
          id: { type: "string", description: "Entry ID" },
        },
        required: ["id"],
      },
    },
    {
      name: "memory_history",
      description: "List recent tier movements from compaction log",
      inputSchema: {
        type: "object" as const,
        properties: {
          limit: { type: "number", description: "Max results (default 20)" },
        },
        required: [],
      },
    },
    {
      name: "memory_lineage",
      description: "Show the full compaction ancestry tree for a memory entry",
      inputSchema: {
        type: "object" as const,
        properties: {
          id: { type: "string", description: "Entry ID" },
        },
        required: ["id"],
      },
    },
    {
      name: "memory_export",
      description: "Export memories to a JSON file",
      inputSchema: {
        type: "object" as const,
        properties: {
          tiers: {
            type: "array",
            items: { type: "string", enum: ["hot", "warm", "cold"] },
            description: "Tiers to export (default: all)",
          },
          content_types: {
            type: "array",
            items: { type: "string", enum: ["memory", "knowledge"] },
            description: "Content types to export (default: all)",
          },
          format: {
            type: "string",
            enum: ["json"],
            description: "Export format (default: json)",
          },
        },
        required: [],
      },
    },
    {
      name: "memory_import",
      description: "Import memories from a JSON export file",
      inputSchema: {
        type: "object" as const,
        properties: {
          path: { type: "string", description: "Path to export JSON file" },
        },
        required: ["path"],
      },
    },
  ];
}

type ToolResult = { content: Array<{ type: "text"; text: string }> };

function getCompactionLogForEntry(
  db: import("better-sqlite3").Database,
  id: string,
): CompactionLogRow[] {
  const rows = db
    .prepare(
      `SELECT * FROM compaction_log
       WHERE target_entry_id = ?
          OR source_entry_ids LIKE ?
       ORDER BY timestamp DESC`,
    )
    .all(id, `%"${id}"%`) as CompactionLogRow[];
  return rows;
}

interface LineageNode {
  id: string;
  compaction_log_id?: string;
  reason?: string;
  timestamp?: number;
  source_tier?: string;
  target_tier?: string | null;
  sources?: LineageNode[];
}

function buildLineage(
  db: import("better-sqlite3").Database,
  id: string,
  depth: number,
): LineageNode {
  if (depth >= 10) {
    return { id, sources: [] };
  }

  const row = db
    .prepare(`SELECT * FROM compaction_log WHERE target_entry_id = ? ORDER BY timestamp DESC LIMIT 1`)
    .get(id) as CompactionLogRow | undefined;

  if (!row) {
    return { id };
  }

  let sourceIds: string[] = [];
  try {
    sourceIds = JSON.parse(row.source_entry_ids) as string[];
  } catch {
    sourceIds = [];
  }

  const sources = sourceIds.map((srcId) => buildLineage(db, srcId, depth + 1));

  return {
    id,
    compaction_log_id: row.id,
    reason: row.reason,
    timestamp: row.timestamp,
    source_tier: row.source_tier,
    target_tier: row.target_tier,
    sources,
  };
}

export interface ExportEntry {
  id: string;
  content: string;
  summary: string | null;
  source: string | null;
  source_tool: string | null;
  project: string | null;
  tags: string[];
  created_at: number;
  updated_at: number;
  last_accessed_at: number;
  access_count: number;
  decay_score: number;
  parent_id: string | null;
  collection_id: string | null;
  metadata: Record<string, unknown>;
  tier: Tier;
  content_type: ContentType;
}

export async function handleExtraTool(
  name: string,
  args: Record<string, unknown>,
  ctx: ToolContext,
): Promise<ToolResult | null> {
  const { db } = ctx;

  if (name === "compact_now") {
    await ctx.embedder.ensureLoaded();
    const embedFn = (text: string) => ctx.embedder.embed(text);
    const result = await compactHotTier(
      db,
      embedFn,
      ctx.config.compaction,
      ctx.sessionId,
      ctx.configSnapshotId,
    );
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            carryForward: result.carryForward.length,
            promoted: result.promoted.length,
            discarded: result.discarded.length,
            carryForwardIds: result.carryForward,
            promotedIds: result.promoted,
            discardedIds: result.discarded,
          }),
        },
      ],
    };
  }

  if (name === "memory_inspect") {
    const id = validateString(args.id, "id");
    const location = getMemory(db, id);
    const compactionHistory = getCompactionLogForEntry(db, id);

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            entry: location,
            compaction_history: compactionHistory,
          }),
        },
      ],
    };
  }

  if (name === "memory_history") {
    const limit = validateOptionalNumber(args.limit, "limit", 1, 1000) ?? 20;
    const rows = db
      .prepare(`SELECT * FROM compaction_log ORDER BY timestamp DESC LIMIT ?`)
      .all(limit) as CompactionLogRow[];

    const movements = rows.map((row) => {
      let sourceIds: string[] = [];
      try {
        sourceIds = JSON.parse(row.source_entry_ids) as string[];
      } catch {
        sourceIds = [];
      }
      return {
        id: row.id,
        timestamp: row.timestamp,
        session_id: row.session_id,
        source_tier: row.source_tier,
        target_tier: row.target_tier,
        source_entry_ids: sourceIds,
        target_entry_id: row.target_entry_id,
        reason: row.reason,
        decay_scores: (() => {
          try {
            return JSON.parse(row.decay_scores) as Record<string, number>;
          } catch {
            return {};
          }
        })(),
      };
    });

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({ movements, count: movements.length }),
        },
      ],
    };
  }

  if (name === "memory_lineage") {
    const id = validateString(args.id, "id");
    const lineage = buildLineage(db, id, 0);

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({ lineage }),
        },
      ],
    };
  }

  if (name === "memory_export") {
    const tierFilter = coerceStringArray(args.tiers, "tiers");
    const typeFilter = coerceStringArray(args.content_types, "content_types");

    const pairs = ALL_TABLE_PAIRS.filter(
      (p) =>
        (!tierFilter || tierFilter.includes(p.tier)) &&
        (!typeFilter || typeFilter.includes(p.type)),
    );

    const allEntries: ExportEntry[] = [];
    for (const { tier, type } of pairs) {
      const entries = listEntries(db, tier, type);
      for (const entry of entries) {
        allEntries.push({
          ...entry,
          tier,
          content_type: type,
        });
      }
    }

    const exportsDir = join(getDataDir(), "exports");
    mkdirSync(exportsDir, { recursive: true });

    const timestamp = Date.now();
    const exportPath = join(exportsDir, `${timestamp}.json`);
    const exportData = { version: 1, exported_at: timestamp, entries: allEntries };
    const jsonStr = JSON.stringify(exportData, null, 2);
    writeFileSync(exportPath, jsonStr, "utf-8");

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            path: exportPath,
            entry_count: allEntries.length,
            size_bytes: Buffer.byteLength(jsonStr, "utf-8"),
          }),
        },
      ],
    };
  }

  if (name === "memory_import") {
    const filePath = validatePath(args.path, "path");

    let raw: string;
    try {
      raw = readFileSync(filePath, "utf-8");
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({ error: `Failed to read file: ${String(err)}` }),
          },
        ],
      };
    }

    let exportData: { version?: number; entries?: ExportEntry[] };
    try {
      exportData = JSON.parse(raw) as { version?: number; entries?: ExportEntry[] };
    } catch {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({ error: "Invalid JSON in export file" }),
          },
        ],
      };
    }

    const entries = exportData.entries ?? [];
    if (!Array.isArray(entries)) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({ error: "Export file missing entries array" }),
          },
        ],
      };
    }

    await ctx.embedder.ensureLoaded();

    let imported = 0;
    let skipped = 0;

    // Build sets for deduplication: by existing id and by existing content
    const existingIds = new Set<string>();
    const existingContents = new Set<string>();
    for (const { tier, type } of ALL_TABLE_PAIRS) {
      const existing = listEntries(db, tier, type);
      for (const e of existing) {
        existingIds.add(e.id);
        existingContents.add(e.content);
      }
    }

    // Also deduplicate by content within the import batch
    const seenContents = new Set<string>(existingContents);

    for (const entry of entries) {
      if (typeof entry.content !== "string" || !entry.content) {
        skipped++;
        continue;
      }

      // Skip if ID already exists
      if (existingIds.has(entry.id)) {
        skipped++;
        continue;
      }

      // Skip duplicate content within import
      if (seenContents.has(entry.content)) {
        skipped++;
        continue;
      }
      seenContents.add(entry.content);

      const tier: Tier = (["hot", "warm", "cold"].includes(entry.tier) ? entry.tier : "hot") as Tier;
      const content_type: ContentType = (["memory", "knowledge"].includes(entry.content_type)
        ? entry.content_type
        : "memory") as ContentType;

      const newId = insertEntry(db, tier, content_type, {
        content: entry.content,
        summary: entry.summary ?? null,
        source: entry.source ?? null,
        source_tool: entry.source_tool ?? null,
        project: entry.project ?? null,
        tags: Array.isArray(entry.tags) ? entry.tags : [],
        parent_id: entry.parent_id ?? null,
        collection_id: entry.collection_id ?? null,
        metadata: entry.metadata ?? {},
      });

      const vec = await ctx.embedder.embed(entry.content);
      insertEmbedding(db, tier, content_type, newId, vec);

      imported++;
    }

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({ imported, skipped }),
        },
      ],
    };
  }

  return null;
}
