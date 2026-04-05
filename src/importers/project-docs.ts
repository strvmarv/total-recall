import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join, basename } from "node:path";
import { createHash } from "node:crypto";
import type Database from "better-sqlite3";
import { ingestFile } from "../ingestion/ingest.js";
import { createCollection, listCollections } from "../ingestion/hierarchical-index.js";

type EmbedFn = (text: string) => Float32Array | Promise<Float32Array>;

export interface ProjectDocsResult {
  filesIngested: number;
  totalChunks: number;
  skipped: number;
}

const DOC_FILES = ["README.md", "CONTRIBUTING.md", "CLAUDE.md", "AGENTS.md"];
const DOC_DIRS = ["docs", "doc"];

function contentHash(content: string): string {
  return createHash("sha256").update(content).digest("hex");
}

function isAlreadyIngested(db: Database.Database, hash: string): boolean {
  const row = db
    .prepare("SELECT id FROM import_log WHERE content_hash = ? AND source_tool = 'project-docs'")
    .get(hash) as { id: string } | undefined;
  return row !== undefined;
}

function logIngest(db: Database.Database, sourcePath: string, hash: string, entryId: string): void {
  const id = createHash("md5").update(`project-docs:${sourcePath}:${hash}`).digest("hex");
  db.prepare(`
    INSERT OR IGNORE INTO import_log
      (id, timestamp, source_tool, source_path, content_hash, target_entry_id, target_tier, target_type)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?)
  `).run(id, Date.now(), "project-docs", sourcePath, hash, entryId, "cold", "knowledge");
}

export async function ingestProjectDocs(
  db: Database.Database,
  embed: EmbedFn,
  cwd: string,
): Promise<ProjectDocsResult> {
  const result: ProjectDocsResult = { filesIngested: 0, totalChunks: 0, skipped: 0 };

  const collectionName = `${basename(cwd)}-project-docs`;
  let collectionId: string | null = null;

  // Collect all files to ingest
  const filesToIngest: string[] = [];

  for (const file of DOC_FILES) {
    const path = join(cwd, file);
    if (existsSync(path)) filesToIngest.push(path);
  }

  for (const dir of DOC_DIRS) {
    const dirPath = join(cwd, dir);
    if (existsSync(dirPath) && statSync(dirPath).isDirectory()) {
      collectMarkdownFiles(dirPath, filesToIngest);
    }
  }

  if (filesToIngest.length === 0) return result;

  // Reuse existing collection or create new one
  const existing = listCollections(db).find((c) => c.name === collectionName);
  collectionId = existing
    ? existing.id
    : await createCollection(db, embed, { name: collectionName, sourcePath: cwd });

  for (const filePath of filesToIngest) {
    const content = readFileSync(filePath, "utf-8").trim();
    if (!content) { result.skipped++; continue; }

    const hash = contentHash(content);
    if (isAlreadyIngested(db, hash)) { result.skipped++; continue; }

    const ingestResult = await ingestFile(db, embed, filePath, collectionId);
    logIngest(db, filePath, hash, collectionId);

    result.filesIngested++;
    result.totalChunks += ingestResult.chunkCount;
  }

  return result;
}

function collectMarkdownFiles(dirPath: string, files: string[]): void {
  for (const entry of readdirSync(dirPath, { withFileTypes: true })) {
    const full = join(dirPath, entry.name);
    if (entry.isDirectory()) {
      collectMarkdownFiles(full, files);
    } else if (entry.name.endsWith(".md")) {
      files.push(full);
    }
  }
}
