import { describe, it, expect, beforeAll } from "vitest";
import { readFileSync } from "node:fs";
import { WordPieceTokenizer } from "./tokenizer.js";
import { getModelPath, isModelDownloaded } from "./model-manager.js";

const MODEL_NAME = "sentence-transformers/all-MiniLM-L6-v2";
const modelPath = getModelPath(MODEL_NAME);
const modelAvailable = isModelDownloaded(modelPath);

describe.skipIf(!modelAvailable)("WordPieceTokenizer", () => {
  let tokenizer: WordPieceTokenizer;

  beforeAll(() => {
    const tokenizerPath = `${modelPath}/tokenizer.json`;
    const tokenizerJson = JSON.parse(readFileSync(tokenizerPath, "utf-8"));
    tokenizer = new WordPieceTokenizer(tokenizerJson.model.vocab);
  });

  it("wraps tokens with CLS and SEP", () => {
    const ids = tokenizer.tokenize("hello");
    expect(ids[0]).toBe(101); // CLS
    expect(ids[ids.length - 1]).toBe(102); // SEP
  });

  it("tokenizes a known in-vocab word", () => {
    const ids = tokenizer.tokenize("hello");
    expect(ids.length).toBe(3); // CLS, hello, SEP
    expect(ids[1]).not.toBe(100); // not UNK
  });

  it("applies WordPiece subword splitting for out-of-vocab words", () => {
    const ids = tokenizer.tokenize("toml");
    // "toml" is not in vocab, but "tom" + "##l" are
    expect(ids.length).toBe(4); // CLS, tom, ##l, SEP
    expect(ids).not.toContain(100);
  });

  it("splits sqlite into subwords", () => {
    const ids = tokenizer.tokenize("sqlite");
    // "sql" + "##ite" are in vocab
    expect(ids.length).toBe(4); // CLS, sql, ##ite, SEP
    expect(ids).not.toContain(100);
  });

  it("splits on punctuation (BertPreTokenizer)", () => {
    const ids = tokenizer.tokenize("sqlite-vec");
    expect(ids.length).toBeGreaterThan(4);
    expect(ids).not.toContain(100);
  });

  it("lowercases input", () => {
    const lower = tokenizer.tokenize("hello");
    const upper = tokenizer.tokenize("HELLO");
    expect(lower).toEqual(upper);
  });

  it("handles empty input", () => {
    const ids = tokenizer.tokenize("");
    expect(ids).toEqual([101, 102]);
  });

  it("handles very long words by emitting UNK", () => {
    const longWord = "a".repeat(200);
    const ids = tokenizer.tokenize(longWord);
    expect(ids).toContain(100);
  });

  it("respects max sequence length of 512", () => {
    const manyWords = Array(600).fill("hello").join(" ");
    const ids = tokenizer.tokenize(manyWords);
    expect(ids.length).toBeLessThanOrEqual(512);
    expect(ids[ids.length - 1]).toBe(102);
  });

  it("produces different tokens for different OOV words", () => {
    const tomlIds = tokenizer.tokenize("toml");
    const sqliteIds = tokenizer.tokenize("sqlite");
    expect(tomlIds).not.toEqual(sqliteIds);
  });
});
