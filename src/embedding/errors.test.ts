import { describe, it, expect } from "vitest";
import {
  ModelNotReadyError,
  isModelNotReadyError,
  type ModelNotReadyReason,
} from "./errors.js";

describe("ModelNotReadyError", () => {
  it("constructor without hint produces correct message", () => {
    const err = new ModelNotReadyError({
      modelName: "foo",
      reason: "missing",
    });
    expect(err.message).toBe("Model 'foo' not ready: missing");
  });

  it("constructor with hint includes hint in parentheses", () => {
    const err = new ModelNotReadyError({
      modelName: "foo",
      reason: "downloading",
      hint: "downloaded 45MB / 90MB",
    });
    expect(err.message).toBe(
      "Model 'foo' not ready: downloading (downloaded 45MB / 90MB)"
    );
  });

  it("name property is ModelNotReadyError", () => {
    const err = new ModelNotReadyError({
      modelName: "foo",
      reason: "failed",
    });
    expect(err.name).toBe("ModelNotReadyError");
  });

  it("properties are accessible", () => {
    const cause = new Error("network timeout");
    const err = new ModelNotReadyError({
      modelName: "bar",
      reason: "corrupted",
      hint: "checksum mismatch",
      cause,
    });
    expect(err.modelName).toBe("bar");
    expect(err.reason).toBe("corrupted");
    expect(err.hint).toBe("checksum mismatch");
    expect(err.cause).toBe(cause);
  });

  it("is instanceof Error", () => {
    const err = new ModelNotReadyError({
      modelName: "foo",
      reason: "missing",
    });
    expect(err instanceof Error).toBe(true);
  });
});

describe("isModelNotReadyError", () => {
  it("returns true for a ModelNotReadyError instance", () => {
    const err = new ModelNotReadyError({
      modelName: "foo",
      reason: "missing",
    });
    expect(isModelNotReadyError(err)).toBe(true);
  });

  it("returns false for a plain Error", () => {
    const err = new Error("some error");
    expect(isModelNotReadyError(err)).toBe(false);
  });

  it("returns false for null", () => {
    expect(isModelNotReadyError(null)).toBe(false);
  });

  it("returns false for undefined", () => {
    expect(isModelNotReadyError(undefined)).toBe(false);
  });

  it("returns true for a duck-typed object with name === ModelNotReadyError", () => {
    const duckTyped = {
      name: "ModelNotReadyError",
      message: "test",
    };
    expect(isModelNotReadyError(duckTyped)).toBe(true);
  });
});
