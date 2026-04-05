import type { ToolContext } from "./registry.js";
import { storeMemory } from "../memory/store.js";
import { searchMemory } from "../memory/search.js";
import { getMemory } from "../memory/get.js";
import { updateMemory } from "../memory/update.js";
import { deleteMemory } from "../memory/delete.js";
import { promoteEntry, demoteEntry } from "../memory/promote-demote.js";
import type { Tier, ContentType, EntryType, SourceTool } from "../types.js";
import { ALL_TABLE_PAIRS } from "../types.js";
import { logRetrievalEvent } from "../eval/event-logger.js";
import {
  validateContent,
  validateEntryType,
  validateTags,
  validateOptionalString,
  validateString,
  validateTier,
  validateContentType,
  validateOptionalNumber,
} from "./validation.js";

export const MEMORY_TOOLS = [
  {
    name: "memory_store",
    description: "Store a new memory or knowledge entry",
    inputSchema: {
      type: "object" as const,
      properties: {
        content: { type: "string", description: "The content to store" },
        tier: { type: "string", enum: ["hot", "warm", "cold"], description: "Storage tier (default: hot)" },
        contentType: { type: "string", enum: ["memory", "knowledge"], description: "Content type (default: memory)" },
        entryType: {
          type: "string",
          enum: ["correction", "preference", "decision", "surfaced", "imported", "compacted", "ingested"],
          description: "Entry type",
        },
        project: { type: "string", description: "Project scope" },
        tags: { type: "array", items: { type: "string" }, description: "Tags" },
        source: { type: "string", description: "Source identifier" },
      },
      required: ["content"],
    },
  },
  {
    name: "memory_search",
    description: "Search memories and knowledge using semantic similarity",
    inputSchema: {
      type: "object" as const,
      properties: {
        query: { type: "string", description: "Search query" },
        topK: { type: "number", description: "Number of results to return (default: 10)" },
        minScore: { type: "number", description: "Minimum similarity score (0-1)" },
        tiers: {
          type: "array",
          items: { type: "string", enum: ["hot", "warm", "cold"] },
          description: "Tiers to search (default: all)",
        },
        contentTypes: {
          type: "array",
          items: { type: "string", enum: ["memory", "knowledge"] },
          description: "Content types to search (default: all)",
        },
      },
      required: ["query"],
    },
  },
  {
    name: "memory_get",
    description: "Retrieve a specific memory entry by ID",
    inputSchema: {
      type: "object" as const,
      properties: {
        id: { type: "string", description: "Entry ID" },
      },
      required: ["id"],
    },
  },
  {
    name: "memory_update",
    description: "Update an existing memory entry",
    inputSchema: {
      type: "object" as const,
      properties: {
        id: { type: "string", description: "Entry ID" },
        content: { type: "string", description: "New content" },
        summary: { type: "string", description: "New summary" },
        tags: { type: "array", items: { type: "string" }, description: "New tags" },
        project: { type: "string", description: "New project" },
      },
      required: ["id"],
    },
  },
  {
    name: "memory_delete",
    description: "Delete a memory entry by ID",
    inputSchema: {
      type: "object" as const,
      properties: {
        id: { type: "string", description: "Entry ID to delete" },
      },
      required: ["id"],
    },
  },
  {
    name: "memory_promote",
    description: "Promote a memory entry to a higher tier",
    inputSchema: {
      type: "object" as const,
      properties: {
        id: { type: "string", description: "Entry ID" },
        toTier: { type: "string", enum: ["hot", "warm", "cold"], description: "Target tier" },
        toType: { type: "string", enum: ["memory", "knowledge"], description: "Target content type" },
      },
      required: ["id", "toTier", "toType"],
    },
  },
  {
    name: "memory_demote",
    description: "Demote a memory entry to a lower tier",
    inputSchema: {
      type: "object" as const,
      properties: {
        id: { type: "string", description: "Entry ID" },
        toTier: { type: "string", enum: ["hot", "warm", "cold"], description: "Target tier" },
        toType: { type: "string", enum: ["memory", "knowledge"], description: "Target content type" },
      },
      required: ["id", "toTier", "toType"],
    },
  },
];

type ToolResult = { content: Array<{ type: "text"; text: string }> };

export async function handleMemoryTool(
  name: string,
  args: Record<string, unknown>,
  ctx: ToolContext,
): Promise<ToolResult | null> {
  if (name === "memory_store") {
    const content = validateContent(args.content);
    const tier: Tier = args.tier !== undefined ? validateTier(args.tier) : "hot";
    const contentType: ContentType = args.contentType !== undefined ? validateContentType(args.contentType) : "memory";
    const type = validateEntryType(args.entryType) as EntryType | undefined;
    const project = validateOptionalString(args.project, "project");
    const tags = validateTags(args.tags);
    const source = validateOptionalString(args.source, "source");
    await ctx.embedder.ensureLoaded();
    const vec = await ctx.embedder.embed(content);
    const embedFn = () => vec;
    const id = await storeMemory(ctx.db, embedFn, {
      content,
      tier,
      contentType,
      type,
      project,
      tags,
      source,
    });
    return { content: [{ type: "text", text: JSON.stringify({ id }) }] };
  }

  if (name === "memory_search") {
    const query = validateString(args.query, "query");
    const topK = validateOptionalNumber(args.topK, "topK", 1, 1000) ?? 10;
    const minScore = validateOptionalNumber(args.minScore, "minScore", 0, 1);
    await ctx.embedder.ensureLoaded();
    const vec = await ctx.embedder.embed(query);
    const embedFn = () => vec;

    const tierFilter = args.tiers as string[] | undefined;
    const typeFilter = args.contentTypes as string[] | undefined;

    const tiers = ALL_TABLE_PAIRS.filter(
      (p) =>
        (!tierFilter || tierFilter.includes(p.tier)) &&
        (!typeFilter || typeFilter.includes(p.type)),
    ).map((p) => ({ tier: p.tier, content_type: p.type }));

    const searchStart = performance.now();
    const results = await searchMemory(ctx.db, embedFn, query, {
      tiers,
      topK,
      minScore,
      ftsWeight: ctx.config.search?.fts_weight,
    });
    const latencyMs = Math.round(performance.now() - searchStart);

    // Log retrieval event for eval metrics
    logRetrievalEvent(ctx.db, {
      sessionId: ctx.sessionId,
      queryText: query,
      querySource: "mcp_tool",
      results: results.map((r, i) => ({
        entry_id: r.entry.id,
        tier: r.tier,
        content_type: r.content_type,
        score: r.score,
        rank: i + 1,
      })),
      tiersSearched: tiers.map((t) => t.tier),
      configSnapshotId: ctx.configSnapshotId,
      latencyMs,
    });

    return { content: [{ type: "text", text: JSON.stringify(results) }] };
  }

  if (name === "memory_get") {
    const id = validateString(args.id, "id");
    const location = getMemory(ctx.db, id);
    return { content: [{ type: "text", text: JSON.stringify(location) }] };
  }

  if (name === "memory_update") {
    const id = validateString(args.id, "id");
    await ctx.embedder.ensureLoaded();
    const newContent = args.content !== undefined ? validateContent(args.content) : undefined;
    const summary = validateOptionalString(args.summary, "summary");
    const tags = args.tags !== undefined ? validateTags(args.tags) : undefined;
    const project = validateOptionalString(args.project, "project");
    let embedFn: (() => Float32Array) | undefined;
    if (newContent !== undefined) {
      const vec = await ctx.embedder.embed(newContent);
      embedFn = () => vec;
    }
    const updated = await updateMemory(ctx.db, embedFn, id, {
      content: newContent,
      summary,
      tags,
      project,
    });
    return { content: [{ type: "text", text: JSON.stringify({ updated }) }] };
  }

  if (name === "memory_delete") {
    const id = validateString(args.id, "id");
    const deleted = deleteMemory(ctx.db, id);
    return { content: [{ type: "text", text: JSON.stringify({ deleted }) }] };
  }

  if (name === "memory_promote") {
    const id = validateString(args.id, "id");
    const toTier = validateTier(args.toTier);
    const toType = validateContentType(args.toType);
    const location = getMemory(ctx.db, id);
    if (!location) {
      return { content: [{ type: "text", text: JSON.stringify({ error: "Entry not found" }) }] };
    }
    await ctx.embedder.ensureLoaded();
    const vec = await ctx.embedder.embed(location.entry.content);
    const embedFn = () => vec;
    await promoteEntry(
      ctx.db,
      embedFn,
      id,
      location.tier,
      location.content_type,
      toTier,
      toType,
    );
    return { content: [{ type: "text", text: JSON.stringify({ promoted: true }) }] };
  }

  if (name === "memory_demote") {
    const id = validateString(args.id, "id");
    const toTier = validateTier(args.toTier);
    const toType = validateContentType(args.toType);
    const location = getMemory(ctx.db, id);
    if (!location) {
      return { content: [{ type: "text", text: JSON.stringify({ error: "Entry not found" }) }] };
    }
    await ctx.embedder.ensureLoaded();
    const vec = await ctx.embedder.embed(location.entry.content);
    const embedFn = () => vec;
    await demoteEntry(
      ctx.db,
      embedFn,
      id,
      location.tier,
      location.content_type,
      toTier,
      toType,
    );
    return { content: [{ type: "text", text: JSON.stringify({ demoted: true }) }] };
  }

  return null;
}
