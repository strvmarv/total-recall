import type { ToolContext } from "./registry.js";
import { countEntries } from "../db/entries.js";
import { ALL_TABLE_PAIRS } from "../types.js";

export const SYSTEM_TOOLS = [
  {
    name: "status",
    description: "Get the status of the total-recall memory system",
    inputSchema: {
      type: "object" as const,
      properties: {},
      required: [],
    },
  },
  {
    name: "config_get",
    description: "Get a configuration value by dot-notation key",
    inputSchema: {
      type: "object" as const,
      properties: {
        key: { type: "string", description: "Dot-notation config key (e.g. 'tiers.hot.max_entries'). Omit for full config." },
      },
      required: [],
    },
  },
  {
    name: "config_set",
    description: "Set a configuration value (acknowledgment only; full persistence deferred to Phase 2)",
    inputSchema: {
      type: "object" as const,
      properties: {
        key: { type: "string", description: "Dot-notation config key" },
        value: { description: "Value to set" },
      },
      required: ["key", "value"],
    },
  },
];

type ToolResult = { content: Array<{ type: "text"; text: string }> };

export function handleSystemTool(
  name: string,
  args: Record<string, unknown>,
  ctx: ToolContext,
): ToolResult | null {
  if (name === "status") {
    const tierSizes: Record<string, number> = {};
    for (const { tier, type } of ALL_TABLE_PAIRS) {
      const key = `${tier}_${type === "memory" ? "memories" : "knowledge"}`;
      tierSizes[key] = countEntries(ctx.db, tier, type);
    }
    const dbInfo = {
      sessionId: ctx.sessionId,
    };
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({ tierSizes, db: dbInfo }),
        },
      ],
    };
  }

  if (name === "config_get") {
    const key = args.key as string | undefined;
    if (!key) {
      return { content: [{ type: "text", text: JSON.stringify(ctx.config) }] };
    }
    const parts = key.split(".");
    let value: unknown = ctx.config;
    for (const part of parts) {
      if (value === null || typeof value !== "object") return { content: [{ type: "text", text: JSON.stringify({ error: "key not found" }) }] };
      if (!Object.prototype.hasOwnProperty.call(value, part)) return { content: [{ type: "text", text: JSON.stringify({ error: `key not found: ${key}` }) }] };
      value = (value as Record<string, unknown>)[part];
    }
    return { content: [{ type: "text", text: JSON.stringify({ key, value }) }] };
  }

  if (name === "config_set") {
    const key = args.key as string;
    const value = args.value;
    return {
      content: [
        {
          type: "text",
          text: JSON.stringify({
            acknowledged: true,
            key,
            value,
            note: "Config persistence deferred to Phase 2",
          }),
        },
      ],
    };
  }

  return null;
}
