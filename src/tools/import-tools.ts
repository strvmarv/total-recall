import type { ToolContext } from "./registry.js";
import { ClaudeCodeImporter } from "../importers/claude-code.js";
import { CopilotCliImporter } from "../importers/copilot-cli.js";

export const IMPORT_TOOLS = [
  {
    name: "import_host",
    description: "Detect and import memories/knowledge from installed host tools (Claude Code, Copilot CLI)",
    inputSchema: {
      type: "object" as const,
      properties: {
        source: { type: "string", description: "Optional: restrict to a specific source ('claude-code' or 'copilot-cli')" },
      },
      required: [],
    },
  },
];

type ToolResult = { content: Array<{ type: "text"; text: string }> };

export async function handleImportTool(
  name: string,
  args: Record<string, unknown>,
  ctx: ToolContext,
): Promise<ToolResult | null> {
  if (name === "import_host") {
    const source = args.source as string | undefined;
    await ctx.embedder.ensureLoaded();
    const embedFn = ctx.embedder.makeSyncEmbedFn();

    const importers = [
      new ClaudeCodeImporter(),
      new CopilotCliImporter(),
    ];

    const results: Array<{
      tool: string;
      detected: boolean;
      scan?: ReturnType<typeof importers[0]["scan"]>;
      memoriesResult?: { imported: number; skipped: number; errors: string[] };
      knowledgeResult?: { imported: number; skipped: number; errors: string[] };
    }> = [];

    for (const importer of importers) {
      if (source && importer.name !== source) continue;

      const detected = importer.detect();
      if (!detected) {
        results.push({ tool: importer.name, detected: false });
        continue;
      }

      const scan = importer.scan();
      const memoriesResult = importer.importMemories(ctx.db, embedFn);
      const knowledgeResult = importer.importKnowledge(ctx.db, embedFn);

      results.push({
        tool: importer.name,
        detected: true,
        scan,
        memoriesResult,
        knowledgeResult,
      });
    }

    return { content: [{ type: "text", text: JSON.stringify({ results }) }] };
  }

  return null;
}

export function registerImportTools() {
  return IMPORT_TOOLS;
}
