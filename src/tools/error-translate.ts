import { isModelNotReadyError, type ModelNotReadyError } from "../embedding/errors.js";

export interface MCPErrorResponse {
  isError: true;
  content: Array<{ type: "text"; text: string }>;
}

export function translateModelNotReadyError(err: unknown): MCPErrorResponse | null {
  if (!isModelNotReadyError(err)) return null;
  const e = err as ModelNotReadyError;
  return {
    isError: true,
    content: [
      {
        type: "text",
        text: JSON.stringify(
          {
            error: "model_not_ready",
            modelName: e.modelName,
            reason: e.reason,
            hint: e.hint,
            message: e.message,
          },
          null,
          2,
        ),
      },
    ],
  };
}
