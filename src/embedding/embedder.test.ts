import { describe, it, expect } from "vitest";
import { Embedder } from "./embedder.js";
import { getModelPath, isModelDownloaded } from "./model-manager.js";

const MODEL_NAME = "sentence-transformers/all-MiniLM-L6-v2";
const DIMENSIONS = 384;

const isCI = process.env.CI === "true";
const modelPath = getModelPath(MODEL_NAME);
const modelAvailable = isModelDownloaded(modelPath);
const skipIntegration = isCI || !modelAvailable;

describe("Embedder", () => {
  it("is not loaded before first use", () => {
    const embedder = new Embedder({ model: MODEL_NAME, dimensions: DIMENSIONS });
    expect(embedder.isLoaded()).toBe(false);
  });

  describe.skipIf(skipIntegration)("integration", () => {
    it("embed returns 384-dimensional normalized vector", async () => {
      const embedder = new Embedder({ model: MODEL_NAME, dimensions: DIMENSIONS });
      const vec = await embedder.embed("Hello world");
      expect(vec.length).toBe(DIMENSIONS);

      // Check unit norm (L2 = 1)
      let norm = 0;
      for (let i = 0; i < DIMENSIONS; i++) norm += (vec[i] as number) * (vec[i] as number);
      expect(Math.sqrt(norm)).toBeCloseTo(1.0, 5);
    });

    it("similar texts get higher cosine similarity than dissimilar texts", async () => {
      const embedder = new Embedder({ model: MODEL_NAME, dimensions: DIMENSIONS });

      const vecA = await embedder.embed("The cat sat on the mat");
      const vecB = await embedder.embed("A cat is sitting on a mat");
      const vecC = await embedder.embed("Quantum physics equations");

      const cosineSim = (a: Float32Array, b: Float32Array): number => {
        let dot = 0;
        for (let i = 0; i < a.length; i++) dot += (a[i] as number) * (b[i] as number);
        return dot;
      };

      const simAB = cosineSim(vecA, vecB);
      const simAC = cosineSim(vecA, vecC);

      expect(simAB).toBeGreaterThan(simAC);
    });
  });
});
