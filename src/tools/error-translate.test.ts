import { describe, it, expect } from "vitest";
import { translateModelNotReadyError } from "./error-translate.js";
import { ModelNotReadyError } from "../embedding/errors.js";

describe("translateModelNotReadyError", () => {
  it("returns null for unrelated errors", () => {
    expect(translateModelNotReadyError(new Error("some other error"))).toBeNull();
    expect(translateModelNotReadyError(null)).toBeNull();
    expect(translateModelNotReadyError(undefined)).toBeNull();
    expect(translateModelNotReadyError("a string error")).toBeNull();
    expect(translateModelNotReadyError(42)).toBeNull();
  });

  it("returns a structured MCP error response for a ModelNotReadyError", () => {
    const err = new ModelNotReadyError({
      modelName: "all-MiniLM-L6-v2",
      reason: "failed",
      hint: "manual install needed: run setup.sh",
    });

    const result = translateModelNotReadyError(err);

    expect(result).not.toBeNull();
    expect(result!.isError).toBe(true);
    expect(result!.content).toHaveLength(1);
    expect(result!.content[0]!.type).toBe("text");
  });

  it("serializes all fields correctly into the JSON payload", () => {
    const err = new ModelNotReadyError({
      modelName: "all-MiniLM-L6-v2",
      reason: "failed",
      hint: "manual install needed: run setup.sh",
    });

    const result = translateModelNotReadyError(err);
    const parsed = JSON.parse(result!.content[0]!.text);

    expect(parsed.error).toBe("model_not_ready");
    expect(parsed.modelName).toBe("all-MiniLM-L6-v2");
    expect(parsed.reason).toBe("failed");
    expect(parsed.hint).toContain("manual install needed");
    expect(typeof parsed.message).toBe("string");
    expect(parsed.message.length).toBeGreaterThan(0);
  });

  it("handles all reason variants", () => {
    const reasons = ["missing", "downloading", "failed", "corrupted"] as const;
    for (const reason of reasons) {
      const err = new ModelNotReadyError({ modelName: "test-model", reason });
      const result = translateModelNotReadyError(err);
      expect(result).not.toBeNull();
      const parsed = JSON.parse(result!.content[0]!.text);
      expect(parsed.reason).toBe(reason);
    }
  });

  it("handles ModelNotReadyError without a hint (hint is undefined)", () => {
    const err = new ModelNotReadyError({
      modelName: "all-MiniLM-L6-v2",
      reason: "missing",
    });

    const result = translateModelNotReadyError(err);
    const parsed = JSON.parse(result!.content[0]!.text);

    expect(parsed.error).toBe("model_not_ready");
    expect(parsed.hint).toBeUndefined();
  });
});
