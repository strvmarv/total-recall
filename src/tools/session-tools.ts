import { randomUUID } from "node:crypto";
import type { ToolContext } from "./registry.js";
import { ClaudeCodeImporter } from "../importers/claude-code.js";
import { CopilotCliImporter } from "../importers/copilot-cli.js";
import { listEntries } from "../db/entries.js";
import { compactHotTier } from "../compaction/compactor.js";

export const SESSION_TOOLS = [
  {
    name: "session_start",
    description: "Initialize a session: sync host tool imports and assemble hot tier context",
    inputSchema: {
      type: "object" as const,
      properties: {},
      required: [],
    },
  },
  {
    name: "session_end",
    description: "End a session: compact the hot tier and return compaction results",
    inputSchema: {
      type: "object" as const,
      properties: {},
      required: [],
    },
  },
  {
    name: "session_context",
    description: "Return current hot tier entries as formatted context text",
    inputSchema: {
      type: "object" as const,
      properties: {},
      required: [],
    },
  },
];

type ToolResult = { content: Array<{ type: "text"; text: string }> };

export async function handleSessionTool(
  name: string,
  args: Record<string, unknown>,
  ctx: ToolContext,
): Promise<ToolResult | null> {
  if (name === "session_start") {
    await ctx.embedder.ensureLoaded();
    const embedFn = (text: string) => ctx.embedder.embed(text);

    const importers = [
      new ClaudeCodeImporter(),
      new CopilotCliImporter(),
    ];

    const importSummary: Array<{
      tool: string;
      memoriesImported: number;
      knowledgeImported: number;
    }> = [];

    for (const importer of importers) {
      if (!importer.detect()) continue;

      const memResult = await importer.importMemories(ctx.db, embedFn);
      const kbResult = await importer.importKnowledge(ctx.db, embedFn);

      importSummary.push({
        tool: importer.name,
        memoriesImported: memResult.imported,
        knowledgeImported: kbResult.imported,
      });
    }

    // Assemble hot tier context
    const hotEntries = listEntries(ctx.db, "hot", "memory");
    const contextLines = hotEntries.map((e) => {
      const tags = e.tags.length > 0 ? ` [${e.tags.join(", ")}]` : "";
      return `- ${e.content}${tags}`;
    });
    const contextText = contextLines.join("\n");

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            sessionId: ctx.sessionId,
            importSummary,
            hotEntryCount: hotEntries.length,
            context: contextText,
          }),
        },
      ],
    };
  }

  if (name === "session_end") {
    await ctx.embedder.ensureLoaded();
    const embedFn = (text: string) => ctx.embedder.embed(text);

    const sessionId = ctx.sessionId ?? randomUUID();
    const result = await compactHotTier(ctx.db, embedFn, ctx.config.compaction, sessionId);

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            sessionId,
            carryForward: result.carryForward.length,
            promoted: result.promoted.length,
            discarded: result.discarded.length,
            details: result,
          }),
        },
      ],
    };
  }

  if (name === "session_context") {
    const hotMemories = listEntries(ctx.db, "hot", "memory");
    const hotKnowledge = listEntries(ctx.db, "hot", "knowledge");

    const allEntries = [...hotMemories, ...hotKnowledge];
    const lines = allEntries.map((e) => {
      const tags = e.tags.length > 0 ? ` [${e.tags.join(", ")}]` : "";
      const project = e.project ? ` (project: ${e.project})` : "";
      return `- ${e.content}${tags}${project}`;
    });

    const contextText = lines.length > 0
      ? lines.join("\n")
      : "(no hot tier entries)";

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            entryCount: allEntries.length,
            context: contextText,
          }),
        },
      ],
    };
  }

  return null;
}

export function registerSessionTools() {
  return SESSION_TOOLS;
}
