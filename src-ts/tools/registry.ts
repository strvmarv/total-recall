import { readFileSync } from "node:fs";
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CallToolRequestSchema, ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";
import type { Database } from "bun:sqlite";
import type { TotalRecallConfig } from "../types.js";
import type { Embedder } from "../embedding/embedder.js";
import { pkgPath } from "../pkg-root.js";
import { MEMORY_TOOLS, handleMemoryTool } from "./memory-tools.js";
import { SYSTEM_TOOLS, handleSystemTool } from "./system-tools.js";
import { registerKbTools, handleKbTool } from "./kb-tools.js";
import { registerEvalTools, handleEvalTool } from "./eval-tools.js";
import { registerImportTools, handleImportTool } from "./import-tools.js";
import { registerSessionTools, handleSessionTool, runSessionInit } from "./session-tools.js";
import { registerExtraTools, handleExtraTool } from "./extra-tools.js";
import { translateModelNotReadyError } from "./error-translate.js";

export interface SessionInitResult {
  project: string | null;
  importSummary: Array<{ tool: string; memoriesImported: number; knowledgeImported: number }>;
  warmSweep: { demoted: number } | null;
  warmPromoted: number;
  projectDocs: { filesIngested: number; totalChunks: number } | null;
  hotEntryCount: number;
  context: string;
  tierSummary: {
    hot: number;
    warm: number;
    cold: number;
    kb: number;
    collections: number;
  };
  hints: string[];
  lastSessionAge: string | null;
  smokeTest?: { passed: boolean; exactMatchRate: number; avgLatencyMs: number };
  regressionAlerts?: Array<{
    metric: "miss_rate" | "latency";
    previous: number;
    current: number;
    delta: number;
    threshold: number;
  }>;
}

export interface ToolContext {
  db: Database;
  config: TotalRecallConfig;
  embedder: Embedder;
  sessionId: string;
  configSnapshotId: string;
  sessionInitialized: boolean;
  sessionInitResult: SessionInitResult | null;
  /** Promise that resolves when background init (from oninitialized) completes */
  sessionInitPromise: Promise<void> | null;
}

function readPackageVersion(): string {
  const pkg = JSON.parse(readFileSync(pkgPath("package.json"), "utf-8")) as { version: string };
  return pkg.version;
}

export async function startServer(ctx: ToolContext): Promise<void> {
  const server = new Server(
    { name: "total-recall", version: readPackageVersion() },
    { capabilities: { tools: {} } },
  );

  // Fire-and-forget: start init as soon as MCP handshake completes.
  // The SDK types oninitialized as () => void, so we can't await —
  // but we store the promise so the lazy-init guard can await it
  // instead of re-running the work.
  server.oninitialized = () => {
    ctx.sessionInitPromise = runSessionInit(ctx).then(() => {
      ctx.sessionInitialized = true;
    }).catch((err) => {
      process.stderr.write(`total-recall: background init failed: ${err}\n`);
    });
  };

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

    try {
      // Lazy-init: ensure session is initialized before any tool runs.
      // If oninitialized already started it, await that promise instead of re-running.
      if (!ctx.sessionInitialized) {
        if (ctx.sessionInitPromise) {
          await ctx.sessionInitPromise;
        } else {
          await runSessionInit(ctx);
          ctx.sessionInitialized = true;
        }
      }

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
    } catch (err) {
      const translated = translateModelNotReadyError(err);
      if (translated) return translated;
      throw err;
    }
  });

  const transport = new StdioServerTransport();
  await server.connect(transport);
}
