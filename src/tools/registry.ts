import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CallToolRequestSchema, ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";
import type Database from "better-sqlite3";
import type { TotalRecallConfig } from "../types.js";
import type { Embedder } from "../embedding/embedder.js";
import { MEMORY_TOOLS, handleMemoryTool } from "./memory-tools.js";
import { SYSTEM_TOOLS, handleSystemTool } from "./system-tools.js";
import { registerKbTools, handleKbTool } from "./kb-tools.js";
import { registerEvalTools, handleEvalTool } from "./eval-tools.js";
import { registerImportTools, handleImportTool } from "./import-tools.js";
import { registerSessionTools, handleSessionTool } from "./session-tools.js";
import { registerExtraTools, handleExtraTool } from "./extra-tools.js";

export interface ToolContext {
  db: Database.Database;
  config: TotalRecallConfig;
  embedder: Embedder;
  sessionId: string;
  configSnapshotId: string;
}

export async function startServer(ctx: ToolContext): Promise<void> {
  const server = new Server(
    { name: "total-recall", version: "0.1.0" },
    { capabilities: { tools: {} } },
  );

  const allTools = [
    ...MEMORY_TOOLS,
    ...SYSTEM_TOOLS,
    ...registerKbTools(),
    ...registerEvalTools(),
    ...registerImportTools(),
    ...registerSessionTools(),
    ...registerExtraTools(),
  ];

  server.setRequestHandler(ListToolsRequestSchema, async () => {
    return { tools: allTools };
  });

  server.setRequestHandler(CallToolRequestSchema, async (request) => {
    const { name, arguments: rawArgs } = request.params;
    const args = (rawArgs ?? {}) as Record<string, unknown>;

    const memResult = await handleMemoryTool(name, args ?? {}, ctx);
    if (memResult !== null) return memResult;
    const sysResult = handleSystemTool(name, args ?? {}, ctx);
    if (sysResult !== null) return sysResult;
    const kbResult = await handleKbTool(name, args ?? {}, ctx);
    if (kbResult !== null) return kbResult;
    const evalResult = await handleEvalTool(name, args ?? {}, ctx);
    if (evalResult !== null) return evalResult;
    const importResult = await handleImportTool(name, args ?? {}, ctx);
    if (importResult !== null) return importResult;
    const sessionResult = await handleSessionTool(name, args ?? {}, ctx);
    if (sessionResult !== null) return sessionResult;
    const extraResult = await handleExtraTool(name, args ?? {}, ctx);
    if (extraResult !== null) return extraResult;

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
