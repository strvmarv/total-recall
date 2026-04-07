import { describe, it, expect } from "vitest";
import { selectProbeIndices } from "./ingest-validation.js";

describe("selectProbeIndices", () => {
  it("returns all indices for 1 chunk", () => {
    expect(selectProbeIndices(1)).toEqual([0]);
  });

  it("returns all indices for 2 chunks", () => {
    expect(selectProbeIndices(2)).toEqual([0, 1]);
  });

  it("returns all indices for 3 chunks", () => {
    expect(selectProbeIndices(3)).toEqual([0, 1, 2]);
  });

  it("returns 0, N/3, 2N/3 for 6 chunks", () => {
    expect(selectProbeIndices(6)).toEqual([0, 2, 4]);
  });

  it("returns 0, N/3, 2N/3 for 10 chunks", () => {
    expect(selectProbeIndices(10)).toEqual([0, 3, 6]);
  });

  it("returns 0, N/3, 2N/3 for 100 chunks", () => {
    expect(selectProbeIndices(100)).toEqual([0, 33, 66]);
  });
});
