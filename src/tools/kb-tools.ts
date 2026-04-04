import type { ToolContext } from "./registry.js";
import { ingestFile, ingestDirectory } from "../ingestion/ingest.js";
import { listCollections } from "../ingestion/hierarchical-index.js";
import { searchMemory } from "../memory/search.js";
import { listEntries, deleteEntry } from "../db/entries.js";
import { deleteEmbedding } from "../search/vector-search.js";

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
];

type ToolResult = { content: Array<{ type: "text"; text: string }> };

export async function handleKbTool(
  name: string,
  args: Record<string, unknown>,
  ctx: ToolContext,
): Promise<ToolResult | null> {
  if (name === "kb_ingest_file") {
    const filePath = args.path as string;
    const collectionId = args.collection as string | undefined;
    await ctx.embedder.ensureLoaded();
    const embedFn = ctx.embedder.makeSyncEmbedFn();
    const result = ingestFile(ctx.db, embedFn, filePath, collectionId);
    return { content: [{ type: "text", text: JSON.stringify(result) }] };
  }

  if (name === "kb_ingest_dir") {
    const dirPath = args.path as string;
    const glob = args.glob as string | undefined;
    await ctx.embedder.ensureLoaded();
    const embedFn = ctx.embedder.makeSyncEmbedFn();
    const result = ingestDirectory(ctx.db, embedFn, dirPath, glob);
    return { content: [{ type: "text", text: JSON.stringify(result) }] };
  }

  if (name === "kb_search") {
    const query = args.query as string;
    const topK = (args.top_k as number | undefined) ?? 10;
    await ctx.embedder.ensureLoaded();
    const vec = await ctx.embedder.embed(query);
    const embedFn = () => vec;
    const results = searchMemory(ctx.db, embedFn, query, {
      tiers: [{ tier: "cold", content_type: "knowledge" }],
      topK,
    });
    return { content: [{ type: "text", text: JSON.stringify(results) }] };
  }

  if (name === "kb_list_collections") {
    const collections = listCollections(ctx.db);
    return { content: [{ type: "text", text: JSON.stringify(collections) }] };
  }

  if (name === "kb_remove") {
    const id = args.id as string;
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
    const collection = args.collection as string;
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            acknowledged: true,
            collection,
            note: "Refresh scheduled — re-ingest the source path to update",
          }),
        },
      ],
    };
  }

  return null;
}

export function registerKbTools() {
  return KB_TOOLS;
}
