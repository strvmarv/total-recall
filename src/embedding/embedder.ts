import { readFile } from "node:fs/promises";
import { join } from "node:path";
import * as ort from "onnxruntime-node";
import { getModelPath, isModelDownloaded, downloadModel } from "./model-manager.js";

interface EmbedderOptions {
  model: string;
  dimensions: number;
}

interface TokenizerVocab {
  [token: string]: number;
}

interface TokenizerModel {
  vocab: TokenizerVocab;
}

interface TokenizerJson {
  model: TokenizerModel;
}

const CLS_TOKEN_ID = 101;
const SEP_TOKEN_ID = 102;
const UNK_TOKEN_ID = 100;
const MAX_SEQ_LEN = 512;

export class Embedder {
  private readonly options: EmbedderOptions;
  private session: ort.InferenceSession | null = null;
  private vocab: TokenizerVocab | null = null;

  constructor(options: EmbedderOptions) {
    this.options = options;
  }

  isLoaded(): boolean {
    return this.session !== null && this.vocab !== null;
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
    this.vocab = tokenizerJson.model.vocab;
  }

  private tokenize(text: string): number[] {
    if (!this.vocab) throw new Error("Tokenizer not loaded");

    const words = text.toLowerCase().split(/\s+/).filter(Boolean);
    const ids: number[] = [CLS_TOKEN_ID];

    for (const word of words) {
      const id = this.vocab[word] ?? UNK_TOKEN_ID;
      ids.push(id);
      if (ids.length >= MAX_SEQ_LEN - 1) break;
    }

    ids.push(SEP_TOKEN_ID);
    return ids;
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

    // Get the first output (last_hidden_state or similar)
    const outputKey = Object.keys(results)[0];
    if (!outputKey) throw new Error("No output from model");
    const output = results[outputKey];
    if (!output) throw new Error("Output tensor is undefined");

    // Mean pooling over sequence length dimension
    // output shape: [1, seqLen, hiddenSize]
    const hiddenSize = this.options.dimensions;
    const data = output.data as Float32Array;
    const pooled = new Float32Array(hiddenSize);

    for (let i = 0; i < seqLen; i++) {
      for (let j = 0; j < hiddenSize; j++) {
        pooled[j] = (pooled[j] as number) + ((data[i * hiddenSize + j] as number | undefined) ?? 0) / seqLen;
      }
    }

    // L2 normalize
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
}
