import type { ToolContext } from "./registry.js";
import { ClaudeCodeImporter } from "../importers/claude-code.js";
import { CopilotCliImporter } from "../importers/copilot-cli.js";
import { CursorImporter } from "../importers/cursor.js";
import { ClineImporter } from "../importers/cline.js";
import { OpenCodeImporter } from "../importers/opencode.js";
import { HermesImporter } from "../importers/hermes.js";
import { validateOptionalString } from "./validation.js";

export const IMPORT_TOOLS = [
  {
    name: "import_host",
    description: "Detect and import memories/knowledge from installed host tools (Claude Code, Copilot CLI, Cursor, Cline, OpenCode, Hermes)",
    inputSchema: {
      type: "object" as const,
      properties: {
        source: { type: "string", description: "Optional: restrict to a specific source ('claude-code', 'copilot-cli', 'cursor', 'cline', 'opencode', 'hermes')" },
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
    const source = validateOptionalString(args.source, "source");
    await ctx.embedder.ensureLoaded();
    const embedFn = (text: string) => ctx.embedder.embed(text);

    const importers = [
      new ClaudeCodeImporter(),
      new CopilotCliImporter(),
      new CursorImporter(),
      new ClineImporter(),
      new OpenCodeImporter(),
      new HermesImporter(),
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
      const memoriesResult = await importer.importMemories(ctx.db, embedFn);
      const knowledgeResult = await importer.importKnowledge(ctx.db, embedFn);

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
