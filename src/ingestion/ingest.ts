import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, dirname, basename, extname } from "node:path";
import type Database from "better-sqlite3";
import { chunkFile } from "./chunker.js";
import {
  createCollection,
  addDocumentToCollection,
  type EmbedFn,
} from "./hierarchical-index.js";
import { insertEmbedding, searchByVector } from "../search/vector-search.js";
import { insertEntry } from "../db/entries.js";

const INGESTABLE_EXTENSIONS = new Set([
  ".md",
  ".mdx",
  ".markdown",
  ".txt",
  ".rst",
  ".ts",
  ".tsx",
  ".js",
  ".jsx",
  ".py",
  ".go",
  ".rs",
  ".java",
  ".kt",
  ".cs",
  ".cpp",
  ".c",
  ".h",
  ".json",
  ".yaml",
  ".yml",
  ".toml",
]);

export interface IngestFileResult {
  documentId: string;
  chunkCount: number;
  validationPassed: boolean;
}

export interface IngestDirectoryResult {
  collectionId: string;
  documentCount: number;
  totalChunks: number;
  errors: string[];
}

export async function ingestFile(
  db: Database.Database,
  embed: EmbedFn,
  filePath: string,
  collectionId?: string,
): Promise<IngestFileResult> {
  const content = readFileSync(filePath, "utf-8");
  const chunks = chunkFile(content, filePath, { maxTokens: 512, overlapTokens: 50 });

  let resolvedCollectionId = collectionId;
  if (!resolvedCollectionId) {
    const dirPath = dirname(filePath);
    const dirName = basename(dirPath);
    resolvedCollectionId = await createCollection(db, embed, {
      name: dirName,
      sourcePath: dirPath,
    });
  }

  const documentId = await addDocumentToCollection(db, embed, {
    collectionId: resolvedCollectionId,
    sourcePath: filePath,
    chunks: chunks.map((c) => ({
      content: c.content,
      headingPath: c.headingPath,
      name: c.name,
      kind: c.kind,
    })),
  });

  // Validation: embed first chunk, search by vector, verify score > 0.5
  let validationPassed = false;
  if (chunks.length > 0) {
    const firstChunk = chunks[0]!;
    const queryVec = await embed(firstChunk.content);
    const results = searchByVector(db, "cold", "knowledge", queryVec, {
      topK: 5,
      minScore: 0,
    });
    validationPassed = results.some((r) => r.score > 0.5);
  }

  return {
    documentId,
    chunkCount: chunks.length,
    validationPassed,
  };
}

function matchesGlob(filename: string, glob: string): boolean {
  // Only support simple patterns: *.ext, *.{ext1,ext2}, exact names
  if (glob.startsWith("*.")) {
    const ext = glob.slice(1); // ".md"
    return filename.endsWith(ext);
  }
  return filename === glob;
}

function walkDirectory(dirPath: string): string[] {
  const files: string[] = [];

  let entries: string[];
  try {
    entries = readdirSync(dirPath);
  } catch {
    return files;
  }

  for (const entry of entries) {
    // Skip hidden directories/files and node_modules
    if (entry.startsWith(".") || entry === "node_modules") continue;

    const fullPath = join(dirPath, entry);
    let stat;
    try {
      stat = statSync(fullPath);
    } catch {
      continue;
    }

    if (stat.isDirectory()) {
      files.push(...walkDirectory(fullPath));
    } else if (stat.isFile()) {
      const ext = extname(entry).toLowerCase();
      if (INGESTABLE_EXTENSIONS.has(ext)) {
        files.push(fullPath);
      }
    }
  }

  return files;
}

export async function ingestDirectory(
  db: Database.Database,
  embed: EmbedFn,
  dirPath: string,
  glob?: string,
): Promise<IngestDirectoryResult> {
  const dirName = basename(dirPath);
  const collectionId = await createCollection(db, embed, {
    name: dirName,
    sourcePath: dirPath,
  });

  const files = walkDirectory(dirPath);

  let documentCount = 0;
  let totalChunks = 0;
  const errors: string[] = [];

  for (const filePath of files) {
    // If a glob filter is provided, apply simple basename matching
    if (glob !== undefined) {
      const name = basename(filePath);
      if (!matchesGlob(name, glob)) continue;
    }

    try {
      const result = await ingestFile(db, embed, filePath, collectionId);
      documentCount++;
      totalChunks += result.chunkCount;
    } catch (err) {
      errors.push(`${filePath}: ${err instanceof Error ? err.message : String(err)}`);
    }
  }

  return {
    collectionId,
    documentCount,
    totalChunks,
    errors,
  };
}
