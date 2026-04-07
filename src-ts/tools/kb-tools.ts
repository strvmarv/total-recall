import { statSync } from "node:fs";
import type { ToolContext } from "./registry.js";
import { ingestFile, ingestDirectory } from "../ingestion/ingest.js";
import { listCollections, getCollection } from "../ingestion/hierarchical-index.js";
import { searchMemory } from "../memory/search.js";
import { listEntries, deleteEntry, getEntry, updateEntry } from "../db/entries.js";
import { deleteEmbedding } from "../search/vector-search.js";
import { validatePath, validateOptionalString, validateOptionalNumber, validateString } from "./validation.js";

export const KB_TOOLS = [
  {
    name: "kb_ingest_file",
    description: "Ingest a single file into the knowledge base",
    inputSchema: {
      type: "object" as const,
      properties: {
        path: { type: "string", description: "Path to the file to ingest" },
        collection: { type: "string", description: "Optional collection ID to add to" },
      },
      required: ["path"],
    },
  },
  {
    name: "kb_ingest_dir",
    description: "Ingest a directory of files into the knowledge base",
    inputSchema: {
      type: "object" as const,
      properties: {
        path: { type: "string", description: "Path to the directory to ingest" },
        glob: { type: "string", description: "Optional glob pattern to filter files" },
        collection: { type: "string", description: "Optional collection name override" },
      },
      required: ["path"],
    },
  },
  {
    name: "kb_search",
    description: "Search the knowledge base (cold/knowledge tier)",
    inputSchema: {
      type: "object" as const,
      properties: {
        query: { type: "string", description: "Search query" },
        collection: { type: "string", description: "Optional collection ID to restrict search" },
        top_k: { type: "number", description: "Number of results to return (default: 10)" },
      },
      required: ["query"],
    },
  },
  {
    name: "kb_list_collections",
    description: "List all knowledge base collections",
    inputSchema: {
      type: "object" as const,
      properties: {},
      required: [],
    },
  },
  {
    name: "kb_remove",
    description: "Remove an entry from the knowledge base",
    inputSchema: {
      type: "object" as const,
      properties: {
        id: { type: "string", description: "Entry ID to remove" },
        cascade: { type: "boolean", description: "If true, also delete child entries" },
      },
      required: ["id"],
    },
  },
  {
    name: "kb_refresh",
    description: "Refresh a knowledge base collection (re-ingest)",
    inputSchema: {
      type: "object" as const,
      properties: {
        collection: { type: "string", description: "Collection ID to refresh" },
      },
      required: ["collection"],
    },
  },
  {
    name: "kb_summarize",
    description: "Store a summary for a knowledge base collection. Call this when a collection's needs_summary flag is true.",
    inputSchema: {
      type: "object" as const,
      properties: {
        collection: { type: "string", description: "Collection ID" },
        summary: { type: "string", description: "Summary text for the collection" },
      },
      required: ["collection", "summary"],
    },
  },
];

type ToolResult = { content: Array<{ type: "text"; text: string }> };

export async function handleKbTool(
  name: string,
  args: Record<string, unknown>,
  ctx: ToolContext,
): Promise<ToolResult | null> {
  if (name === "kb_ingest_file") {
    const filePath = validatePath(args.path, "path");
    const collectionId = validateOptionalString(args.collection, "collection");
    await ctx.embedder.ensureLoaded();
    const embedFn = (text: string) => ctx.embedder.embed(text);
    const result = await ingestFile(ctx.db, embedFn, filePath, collectionId);
    return { content: [{ type: "text", text: JSON.stringify(result) }] };
  }

  if (name === "kb_ingest_dir") {
    const dirPath = validatePath(args.path, "path");
    const glob = validateOptionalString(args.glob, "glob");
    await ctx.embedder.ensureLoaded();
    const embedFn = (text: string) => ctx.embedder.embed(text);
    const result = await ingestDirectory(ctx.db, embedFn, dirPath, glob);
    return { content: [{ type: "text", text: JSON.stringify(result) }] };
  }

  if (name === "kb_search") {
    const query = validateString(args.query, "query");
    let collectionId = validateOptionalString(args.collection, "collection");
    const topK = validateOptionalNumber(args.top_k, "top_k", 1, 1000) ?? 10;
    await ctx.embedder.ensureLoaded();
    const vec = await ctx.embedder.embed(query);
    const embedFn = () => vec;

    // Hierarchical search: if no collection specified, try matching against
    // collection summaries first to narrow scope
    let hierarchicalMatch: string | null = null;
    if (!collectionId) {
      const collections = listCollections(ctx.db);
      const withSummaries = collections.filter((c) => c.summary);

      if (withSummaries.length > 0) {
        // Search collection entries (which have summaries) by vector similarity
        const collectionResults = await searchMemory(ctx.db, embedFn, query, {
          tiers: [{ tier: "cold", content_type: "knowledge" }],
          topK: 3,
          minScore: ctx.config.tiers.warm.similarity_threshold,
          ftsWeight: ctx.config.search?.fts_weight,
        });

        // Find the best matching collection entry
        for (const r of collectionResults) {
          const meta = r.entry.metadata as Record<string, unknown>;
          if (meta["type"] === "collection") {
            collectionId = r.entry.id;
            hierarchicalMatch = (r.entry as { name?: string }).name ?? r.entry.id;
            break;
          }
        }
      }
    }

    // Search within scope (collection-filtered or flat)
    let results;
    if (collectionId) {
      // Scoped search: only entries in this collection
      const allResults = await searchMemory(ctx.db, embedFn, query, {
        tiers: [{ tier: "cold", content_type: "knowledge" }],
        topK: topK * 2,
        ftsWeight: ctx.config.search?.fts_weight,
      });
      results = allResults.filter(
        (r) => r.entry.collection_id === collectionId || r.entry.parent_id === collectionId,
      ).slice(0, topK);

      // Track collection access and check summary threshold
      ctx.db.prepare(
        `UPDATE cold_knowledge SET access_count = access_count + 1, last_accessed_at = ? WHERE id = ?`
      ).run(Date.now(), collectionId);
    } else {
      results = await searchMemory(ctx.db, embedFn, query, {
        tiers: [{ tier: "cold", content_type: "knowledge" }],
        topK,
        ftsWeight: ctx.config.search?.fts_weight,
      });
    }

    // Check if collection needs a summary
    let needsSummary = false;
    if (collectionId) {
      const collection = getCollection(ctx.db, collectionId);
      if (collection && !collection.summary && collection.access_count >= (ctx.config.tiers.cold.lazy_summary_threshold ?? 5)) {
        needsSummary = true;
      }
    }

    return { content: [{ type: "text", text: JSON.stringify({ results, hierarchicalMatch, needsSummary }) }] };
  }

  if (name === "kb_list_collections") {
    const collections = listCollections(ctx.db);
    return { content: [{ type: "text", text: JSON.stringify(collections) }] };
  }

  if (name === "kb_remove") {
    const id = validateString(args.id, "id");
    const cascade = (args.cascade as boolean | undefined) ?? false;

    if (cascade) {
      // Delete children (chunks/documents belonging to this collection/document)
      const children = listEntries(ctx.db, "cold", "knowledge").filter(
        (e) => e.parent_id === id || e.collection_id === id,
      );
      for (const child of children) {
        deleteEmbedding(ctx.db, "cold", "knowledge", child.id);
        deleteEntry(ctx.db, "cold", "knowledge", child.id);
      }
    }

    deleteEmbedding(ctx.db, "cold", "knowledge", id);
    deleteEntry(ctx.db, "cold", "knowledge", id);
    return { content: [{ type: "text", text: JSON.stringify({ removed: id, cascade }) }] };
  }

  if (name === "kb_refresh") {
    const collectionId = validateString(args.collection, "collection");

    // Look up the collection entry to get its source_path from metadata
    const collectionEntry = getEntry(ctx.db, "cold", "knowledge", collectionId);
    if (!collectionEntry) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({ error: `Collection not found: ${collectionId}` }),
          },
        ],
      };
    }

    const sourcePath = (collectionEntry.metadata as Record<string, unknown>)?.source_path as string | undefined;
    if (!sourcePath) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({ error: "Collection has no source_path in metadata; cannot refresh" }),
          },
        ],
      };
    }

    // Delete all child entries (documents and chunks) belonging to this collection
    const children = listEntries(ctx.db, "cold", "knowledge").filter(
      (e) => e.collection_id === collectionId || e.parent_id === collectionId,
    );
    for (const child of children) {
      deleteEmbedding(ctx.db, "cold", "knowledge", child.id);
      deleteEntry(ctx.db, "cold", "knowledge", child.id);
    }
    // Delete the collection root entry itself
    deleteEmbedding(ctx.db, "cold", "knowledge", collectionId);
    deleteEntry(ctx.db, "cold", "knowledge", collectionId);

    // Re-ingest from source path
    await ctx.embedder.ensureLoaded();
    const embedFn = (text: string) => ctx.embedder.embed(text);

    let result: import("../ingestion/ingest.js").IngestDirectoryResult | import("../ingestion/ingest.js").IngestFileResult;
    let isDir = false;
    try {
      isDir = statSync(sourcePath).isDirectory();
    } catch (err) {
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({ error: `Cannot stat source path: ${String(err)}` }),
          },
        ],
      };
    }

    if (isDir) {
      result = await ingestDirectory(ctx.db, embedFn, sourcePath);
    } else {
      result = await ingestFile(ctx.db, embedFn, sourcePath);
    }

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            refreshed: true,
            source_path: sourcePath,
            deleted_children: children.length,
            ...result,
          }),
        },
      ],
    };
  }

  if (name === "kb_summarize") {
    const collectionId = validateString(args.collection, "collection");
    const summary = validateString(args.summary, "summary");

    const entry = getEntry(ctx.db, "cold", "knowledge", collectionId);
    if (!entry) {
      return { content: [{ type: "text", text: JSON.stringify({ error: `Collection not found: ${collectionId}` }) }] };
    }

    updateEntry(ctx.db, "cold", "knowledge", collectionId, { summary });
    return { content: [{ type: "text", text: JSON.stringify({ collection: collectionId, summarized: true }) }] };
  }

  return null;
}

export function registerKbTools() {
  return KB_TOOLS;
}
