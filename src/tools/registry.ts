import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CallToolRequestSchema, ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";
import type Database from "better-sqlite3";
import type { TotalRecallConfig } from "../types.js";
import type { Embedder } from "../embedding/embedder.js";
import { MEMORY_TOOLS, handleMemoryTool } from "./memory-tools.js";
import { SYSTEM_TOOLS, handleSystemTool } from "./system-tools.js";

export interface ToolContext {
  db: Database.Database;
  config: TotalRecallConfig;
  embedder: Embedder;
  sessionId: string;
}

export async function startServer(ctx: ToolContext): Promise<void> {
  const server = new Server(
    { name: "total-recall", version: "0.1.0" },
    { capabilities: { tools: {} } },
  );

  server.setRequestHandler(ListToolsRequestSchema, async () => {
    return { tools: [...MEMORY_TOOLS, ...SYSTEM_TOOLS] };
  });

  server.setRequestHandler(CallToolRequestSchema, async (request) => {
    const { name, arguments: rawArgs } = request.params;
    const args = (rawArgs ?? {}) as Record<string, unknown>;

    const memoryResult = await handleMemoryTool(name, args, ctx);
    if (memoryResult !== null) return memoryResult;

    const systemResult = handleSystemTool(name, args, ctx);
    if (systemResult !== null) return systemResult;

    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify({ error: `Unknown tool: ${name}` }),
        },
      ],
      isError: true,
    };
  });

  const transport = new StdioServerTransport();
  await server.connect(transport);
}
