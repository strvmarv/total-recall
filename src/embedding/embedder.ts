import { readFile } from "node:fs/promises";
import { join } from "node:path";
import * as ort from "onnxruntime-node";
import { getModelPath, isModelDownloaded, downloadModel } from "./model-manager.js";
import { WordPieceTokenizer } from "./tokenizer.js";

interface EmbedderOptions {
  model: string;
  dimensions: number;
}

interface TokenizerJson {
  model: {
    vocab: Record<string, number>;
  };
}

export class Embedder {
  private readonly options: EmbedderOptions;
  private session: ort.InferenceSession | null = null;
  private tokenizer: WordPieceTokenizer | null = null;

  constructor(options: EmbedderOptions) {
    this.options = options;
  }

  isLoaded(): boolean {
    return this.session !== null && this.tokenizer !== null;
  }

  async ensureLoaded(): Promise<void> {
    if (this.isLoaded()) return;

    const modelPath = getModelPath(this.options.model);
    if (!isModelDownloaded(modelPath)) {
      await downloadModel(this.options.model);
    }

    const onnxPath = join(modelPath, "model.onnx");
    this.session = await ort.InferenceSession.create(onnxPath);

    const tokenizerPath = join(modelPath, "tokenizer.json");
    const tokenizerText = await readFile(tokenizerPath, "utf-8");
    const tokenizerJson = JSON.parse(tokenizerText) as TokenizerJson;
    this.tokenizer = new WordPieceTokenizer(tokenizerJson.model.vocab);
  }

  private tokenize(text: string): number[] {
    if (!this.tokenizer) throw new Error("Tokenizer not loaded");
    return this.tokenizer.tokenize(text);
  }

  async embed(text: string): Promise<Float32Array> {
    await this.ensureLoaded();
    if (!this.session) throw new Error("Session not loaded");

    const inputIds = this.tokenize(text);
    const seqLen = inputIds.length;

    const inputIdsTensor = new ort.Tensor(
      "int64",
      BigInt64Array.from(inputIds.map(BigInt)),
      [1, seqLen],
    );
    const attentionMask = new ort.Tensor(
      "int64",
      BigInt64Array.from(new Array(seqLen).fill(1n)),
      [1, seqLen],
    );
    const tokenTypeIds = new ort.Tensor(
      "int64",
      BigInt64Array.from(new Array(seqLen).fill(0n)),
      [1, seqLen],
    );

    const feeds: Record<string, ort.Tensor> = {
      input_ids: inputIdsTensor,
      attention_mask: attentionMask,
      token_type_ids: tokenTypeIds,
    };

    const results = await this.session.run(feeds);

    const outputKey = Object.keys(results)[0];
    if (!outputKey) throw new Error("No output from model");
    const output = results[outputKey];
    if (!output) throw new Error("Output tensor is undefined");

    const hiddenSize = this.options.dimensions;
    const data = output.data as Float32Array;
    const pooled = new Float32Array(hiddenSize);

    for (let i = 0; i < seqLen; i++) {
      for (let j = 0; j < hiddenSize; j++) {
        pooled[j] = (pooled[j] as number) + ((data[i * hiddenSize + j] as number | undefined) ?? 0) / seqLen;
      }
    }

    let norm = 0;
    for (let i = 0; i < hiddenSize; i++) norm += (pooled[i] as number) * (pooled[i] as number);
    norm = Math.sqrt(norm);
    if (norm > 0) {
      for (let i = 0; i < hiddenSize; i++) pooled[i] = (pooled[i] as number) / norm;
    }

    return pooled;
  }

  async embedBatch(texts: string[]): Promise<Float32Array[]> {
    const results: Float32Array[] = [];
    for (const text of texts) {
      results.push(await this.embed(text));
    }
    return results;
  }

  deterministicEmbed(text: string): Float32Array {
    const tokenIds = this.tokenize(text);
    const hiddenSize = this.options.dimensions;
    const vec = new Float32Array(hiddenSize);
    for (let i = 0; i < tokenIds.length; i++) {
      const tokenId = tokenIds[i] as number;
      for (let j = 0; j < hiddenSize; j++) {
        const h = Math.sin((tokenId * (j + 1)) / hiddenSize);
        vec[j] = (vec[j] as number) + h / tokenIds.length;
      }
    }
    let norm = 0;
    for (let i = 0; i < hiddenSize; i++) norm += (vec[i] as number) * (vec[i] as number);
    norm = Math.sqrt(norm);
    if (norm > 0) {
      for (let i = 0; i < hiddenSize; i++) vec[i] = (vec[i] as number) / norm;
    }
    return vec;
  }
}
